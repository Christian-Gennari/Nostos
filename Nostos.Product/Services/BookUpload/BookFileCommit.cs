using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;
using Nostos.Product.BookText;
using Nostos.Shared.Enums;

namespace Nostos.Backend.Services.BookUpload;

/// <summary>
/// The one place a book's primary file becomes the row's file (#1266): both
/// the legacy whole-request upload and the resumable commit call
/// <see cref="FinishAsync"/>, so the row updates, track replacement and text
/// re-index scheduling can never drift apart between the two paths.
///
/// The caller owns the <see cref="BookFileMutationGate"/> lease and the book
/// row load; this type only writes the file and the row.
/// </summary>
public sealed class BookFileCommit(
    IBookRepository repo,
    IBookAssetStorage storage,
    MediaMetadataService metadataService,
    IBookTextIngestionScheduler textScheduler,
    IBookTrackStorage? trackStorage = null)
{
    /// <summary>Legacy path: enrich from one read of the request stream, then save a fresh read.</summary>
    public async Task CommitStreamAsync(
        BookModel book,
        Func<Stream> openContent,
        string fileName,
        CancellationToken ct)
    {
        await using (var metadataContent = openContent())
        {
            await EnrichAsync(book, metadataContent, fileName, ct);
        }

        await using (var content = openContent())
        {
            await storage.SaveBookFileAsync(book.Id, content, fileName, ct);
        }

        await FinishAsync(book, fileName, ct);
    }

    /// <summary>
    /// Metadata enrichment from the staged bytes, before adoption. Audio gets
    /// chapters and duration; an EPUB or PDF fills what the row left empty
    /// (see <see cref="ApplyFileMetadataAsync"/>). 
    /// </summary>
    public async Task EnrichAsync(BookModel book, Stream content, string fileName, CancellationToken ct)
    {
        if (BookFileMetadataReader.CanRead(fileName))
        {
            var found = await BookFileMetadataReader.ReadAsync(content, fileName, ct);
            await ApplyFileMetadataAsync(book, found, fileName, ct);
        }
        else
        {
            metadataService.EnrichBookMetadata(book, content);
        }
    }

    /// <summary>
    /// What the user typed always wins: a field is filled only when it is empty.
    /// The title counts as empty when it is just the file's own name, which is
    /// what a form or an API client sends when it knows nothing better.
    /// </summary>
    private async Task ApplyFileMetadataAsync(
        BookModel book,
        BookFileMetadata found,
        string fileName,
        CancellationToken ct)
    {
        if (found.IsEmpty) return;

        if (found.Title is not null && IsPlaceholderTitle(book.Title, fileName))
            book.Title = found.Title;
        if (found.Author is not null && string.IsNullOrWhiteSpace(book.Author))
            book.Author = found.Author;
        if (found.Language is not null && string.IsNullOrWhiteSpace(book.Metadata.Language))
            book.Metadata.Language = found.Language;

        if (found.PageCount is { } pages)
        {
            if (book is EBookModel { PageCount: null } ebook) ebook.PageCount = pages;
        }

        if (found.CoverBytes is not null && string.IsNullOrEmpty(book.FileDetails.CoverFileName))
        {
            var coverName = $"cover{found.CoverExtension}";
            await using var cover = new MemoryStream(found.CoverBytes);
            await storage.SaveBookCoverAsync(book.Id, cover, coverName, ct);
            book.FileDetails.SetCover(coverName);
        }
    }

    private static bool IsPlaceholderTitle(string title, string fileName)
    {
        static string Key(string value) =>
            string.Join(' ', value.ToLowerInvariant()
                .Split([' ', '_', '-', '.'], StringSplitOptions.RemoveEmptyEntries));

        var key = Key(title);
        return key.Length == 0
            || key == Key(fileName)
            || key == Key(Path.GetFileNameWithoutExtension(fileName));
    }

    /// <summary>Row-level part of the commit, shared by every transport.</summary>
    public async Task FinishAsync(BookModel book, string fileName, CancellationToken ct)
    {
        var id = book.Id;

        // A book holds either one primary file or a track list, never both.
        if (book.FileDetails.TracksJson is not null)
        {
            book.FileDetails.TracksJson = null;
            if (trackStorage is not null)
                await trackStorage.DeleteTracksAsync(id, ct);
        }

        book.FileDetails.HasFile = true;
        book.FileDetails.FileName = $"book{Path.GetExtension(fileName)}";

        // A row created by the legacy "file was expected" flow leaves
        // UploadPending; success is its lifecycle boundary. The resumable flow
        // creates no such state, so this is simply a no-op there.
        if (book.Status == BookStatus.UploadPending)
        {
            book.Status = BookStatus.Ready;
            book.StatusMessage = null;
        }

        // Clear old locations/chapters if a new file is uploaded.
        book.FileDetails.LocationsJson = null;

        await repo.UpdateAsync(book);

        // Upload/replacement is a source-revision event. Scheduling
        // invalidates any old searchable revision immediately; the scheduler
        // deliberately degrades without failing the valid file upload if
        // extraction/indexing cannot start.
        await textScheduler.ScheduleAsync(id, book.FileDetails.FileName, ct);
    }
}

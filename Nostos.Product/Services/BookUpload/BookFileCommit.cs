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
    IBookTrackStorage? trackStorage,
    MediaMetadataService metadataService,
    IBookTextIngestionScheduler textScheduler)
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
            metadataService.EnrichBookMetadata(book, metadataContent);
        }

        await using (var content = openContent())
        {
            await storage.SaveBookFileAsync(book.Id, content, fileName, ct);
        }

        await FinishAsync(book, fileName, ct);
    }

    /// <summary>Metadata enrichment from the staged bytes, before adoption.</summary>
    public void Enrich(BookModel book, Stream content) =>
        metadataService.EnrichBookMetadata(book, content);

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

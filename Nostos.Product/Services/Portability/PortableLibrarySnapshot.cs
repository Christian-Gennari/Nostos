using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Services.Portability;

/// <summary>
/// Where a book's media actually lives, as observed by the snapshot's caller:
/// the export reads the stored file details, while a recovery copy derives the
/// flags from the media it retained. The rest of the book projection is the
/// same on both paths.
/// </summary>
internal sealed record PortableBookMedia(
    bool HasBookFile,
    bool HasCover,
    string? TracksJson);

/// <summary>
/// The single projection from the live relational model to
/// <see cref="PortableLibraryData"/> (camelCase JSON, data version 6).
/// </summary>
/// <remarks>
/// <para>
/// A normal export and a recovery copy must serialize exactly the same shape,
/// or the archive and the recovery payload drift apart. Both therefore read
/// through here; neither keeps its own copy of the entity projection.
/// </para>
/// <para>
/// The caller owns the transactional boundary. <see cref="PortableArchiveService"/>
/// wraps <see cref="ReadAsync"/> in one serializable read transaction, and
/// <see cref="PortableLibraryRecoverySource"/> reads a retained database copy
/// that nothing else is writing.
/// </para>
/// <para>
/// Book values (rating, favourite, review) live on the work and are read from
/// there; the progress record carries only this edition's own exception, and
/// the favourite is never repeated per edition.
/// </para>
/// </remarks>
internal static class PortableLibrarySnapshot
{
    internal static async Task<PortableLibraryData> ReadAsync(
        NostosDbContext db,
        Func<BookModel, PortableBookMedia> bookMedia,
        CancellationToken cancellationToken,
        string unsupportedBookTypeSubject = "Book")
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(bookMedia);

        var works = (await db.Works
            .AsNoTracking()
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(x => new PortableWork(
                x.Id,
                x.Title,
                x.Author,
                x.CreatedAt,
                x.Rating,
                x.IsFavorite,
                x.PersonalReview))
            .ToList();

        var books = (await db.Books
            .AsNoTracking()
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(x => ToPortableBook(x, bookMedia(x), unsupportedBookTypeSubject))
            .ToList();

        var collections = (await db.Collections
            .AsNoTracking()
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(x => new PortableCollection(
                x.Id,
                x.Name,
                x.ParentId))
            .ToList();

        var bookCollections = (await db.BookCollections
            .AsNoTracking()
            .OrderBy(x => x.BookId)
            .ThenBy(x => x.CollectionId)
            .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(x => new PortableBookCollection(
                x.BookId,
                x.CollectionId,
                x.AddedAt))
            .ToList();

        var notes = (await db.Notes
            .AsNoTracking()
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(x => new PortableNote(
                x.Id,
                x.Content,
                x.CfiRange,
                x.SelectedText,
                x.CreatedAt,
                x.BookId,
                x.RawContent,
                x.CaptureSource,
                x.ProcessingMode,
                x.SourceAnchorKind,
                x.SourceAnchorValue,
                x.AnchorVerified,
                x.HighlightColour,
                x.Kind,
                x.PassageTitle,
                x.PassageEnd,
                x.PassageHtml))
            .ToList();

        var topics = (await db.Topics
            .AsNoTracking()
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(x => new PortableTopic(
                x.Id,
                x.Topic,
                x.Description))
            .ToList();

        var noteTopics = (await db.NoteTopics
            .AsNoTracking()
            .OrderBy(x => x.NoteId)
            .ThenBy(x => x.TopicId)
            .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(x => new PortableNoteTopic(
                x.NoteId,
                x.TopicId))
            .ToList();

        var writings = (await db.Writings
            .AsNoTracking()
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(x => new PortableWriting(
                x.Id,
                x.Name,
                x.Type.ToString(),
                x.Content,
                x.ParentId,
                x.CreatedAt,
                x.UpdatedAt,
                x.PageSetup))
            .ToList();

        var acquisitions = (await db.BookAcquisitions
            .AsNoTracking()
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(x => new PortableBookAcquisition(
                x.Id,
                x.BookId,
                x.ProviderId,
                x.ProviderDisplayName,
                x.ExternalId,
                x.AssetId,
                x.AssetFormat,
                x.ImportedExtension,
                x.SourceUrl,
                x.RightsStatement,
                x.AcquiredAt))
            .ToList();

        var writingNotes = (await db.WritingNotes
            .AsNoTracking()
            .OrderBy(x => x.WritingId)
            .ThenBy(x => x.NoteId)
            .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(x => new PortableWritingNote(
                x.WritingId,
                x.NoteId,
                x.AddedAt))
            .ToList();

        var noteImportBookLinks = (await db.NoteImportBookLinks
            .AsNoTracking()
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(x => new PortableNoteImportBookLink(
                x.Id,
                x.Source,
                x.SourceKey,
                x.BookId,
                x.CreatedAtUtc))
            .ToList();

        var readingListEntries = (await db.ReadingListEntries
            .AsNoTracking()
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(x => new PortableReadingListEntry(
                x.Id,
                x.Title,
                x.Author,
                x.Isbn,
                x.PublishedDate,
                x.Why,
                x.RecommendedBy,
                x.NextPosition,
                x.CreatedAt))
            .ToList();

        var assistant = await db.AssistantSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return new PortableLibraryData(
            PortableArchiveFormat.DataVersion,
            works,
            books,
            collections,
            bookCollections,
            notes,
            topics,
            noteTopics,
            writings,
            acquisitions,
            assistant is null
                ? null
                : new PortableAssistantSettings(
                    assistant.CaptureProcessingMode,
                    assistant.UpdatedAtUtc),
            writingNotes,
            noteImportBookLinks,
            readingListEntries);
    }

    /// <summary>
    /// Projects one edition. The concrete type decides the archive type, the
    /// type-specific identifiers and which media flags apply; everything else
    /// is common.
    /// </summary>
    internal static PortableBook ToPortableBook(
        BookModel book,
        PortableBookMedia media,
        string unsupportedBookTypeSubject = "Book")
    {
        var type = book switch
        {
            PhysicalBookModel => "physical",
            EBookModel => "ebook",
            AudioBookModel => "audiobook",
            _ => throw new PortableArchiveException(
                "unsupported_book_type",
                $"{unsupportedBookTypeSubject} {book.Id} has an unsupported concrete type."),
        };

        var isbn = book switch
        {
            PhysicalBookModel physical => physical.Isbn,
            EBookModel ebook => ebook.Isbn,
            _ => null,
        };
        var pageCount = book switch
        {
            PhysicalBookModel physical => physical.PageCount,
            EBookModel ebook => ebook.PageCount,
            _ => null,
        };
        var audio = book as AudioBookModel;

        return new PortableBook(
            book.Id,
            book.WorkId,
            type,
            book.Status.ToString(),
            book.StatusMessage,
            book.Title,
            book.Author,
            new PortableBookMetadata(
                book.Metadata.Subtitle,
                book.Metadata.Description,
                book.Metadata.Editor,
                book.Metadata.Translator,
                book.Metadata.Publisher,
                book.Metadata.PlaceOfPublication,
                book.Metadata.PublishedDate,
                book.Metadata.Language,
                book.Metadata.Categories,
                book.Metadata.Edition,
                book.Metadata.Series,
                book.Metadata.VolumeNumber),
            new PortableReadingProgress(
                book.Progress.LastLocation,
                book.Progress.ProgressPercent,
                book.Progress.Rating,
                // The favourite is the book's and travels on its work.
                false,
                book.Progress.PersonalReview,
                book.Progress.LastReadAt,
                book.Progress.FinishedAt),
            book.CreatedAt,
            isbn,
            pageCount,
            audio?.Asin,
            audio?.Duration,
            audio?.Narrator,
            book.FileDetails.ChaptersJson,
            media.HasBookFile,
            media.HasCover,
            media.TracksJson);
    }
}

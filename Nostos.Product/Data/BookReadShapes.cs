using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data.Models;
using Nostos.Shared.Enums;

namespace Nostos.Backend.Data;

/// <summary>
/// Book reads that leave <see cref="FileInfoDetails.LocationsJson"/> in the
/// database. That column is the reader's epub.js cache, and on a real book it is
/// hundreds of kilobytes. A plain book entity selects it, and <c>Include(n =&gt;
/// n.Book)</c> repeats it once per note, so a list or note read pays for a cache
/// only <c>GET /books/{id}/locations</c> wants.
///
/// A projection rather than a table split: splitting the column off changes the
/// EF model, which every host would then have to migrate.
///
/// The models these shapes build are for reading. They are untracked and
/// incomplete on purpose, so nothing may save them.
/// </summary>
internal static class BookReadShapes
{
    /// <summary>
    /// A whole book with its work, collections and acquisition, for
    /// <c>ToDto</c> and <c>ToEditionSummary</c>: everything but the locations.
    /// Runs on no-tracking queries only and goes last, in place of <c>Include</c>.
    /// A column added to a book has to be added here too;
    /// <c>BookReadShapesTests</c> fails until it is.
    /// </summary>
    public static readonly Expression<Func<BookModel, BookModel>> WithoutLocations = b => Fill(
        b is AudioBookModel
            ? new AudioBookModel
            {
                Asin = ((AudioBookModel)b).Asin,
                Duration = ((AudioBookModel)b).Duration,
                Narrator = ((AudioBookModel)b).Narrator,
            }
            : b is EBookModel
                ? new EBookModel
                {
                    Isbn = ((EBookModel)b).Isbn,
                    PageCount = ((EBookModel)b).PageCount,
                }
                : new PhysicalBookModel
                {
                    Isbn = ((PhysicalBookModel)b).Isbn,
                    PageCount = ((PhysicalBookModel)b).PageCount,
                },
        b.Id,
        b.Status,
        b.StatusMessage,
        b.Title,
        b.Author,
        b.Metadata,
        b.Progress,
        new FileInfoDetails
        {
            HasFile = b.FileDetails.HasFile,
            FileName = b.FileDetails.FileName,
            CoverFileName = b.FileDetails.CoverFileName,
            CoverVersion = b.FileDetails.CoverVersion,
            ChaptersJson = b.FileDetails.ChaptersJson,
            TracksJson = b.FileDetails.TracksJson,
        },
        b.CreatedAt,
        b.NormalizedIsbn,
        b.NormalizedAsin,
        b.WorkId,
        b.Work,
        b.BookCollections.ToList(),
        b.Acquisition);

    private static BookModel Fill(
        BookModel book,
        Guid id,
        BookStatus status,
        string? statusMessage,
        string title,
        string? author,
        BookMetadata? metadata,
        ReadingProgress progress,
        FileInfoDetails fileDetails,
        DateTime createdAt,
        string? normalizedIsbn,
        string? normalizedAsin,
        Guid workId,
        WorkModel? work,
        List<BookCollectionModel> collections,
        BookAcquisitionModel? acquisition)
    {
        book.Id = id;
        book.Status = status;
        book.StatusMessage = statusMessage;
        book.Title = title;
        book.Author = author;
        // An owned row whose columns are all null reads back as no row.
        book.Metadata = metadata ?? new BookMetadata();
        book.Progress = progress;
        book.FileDetails = fileDetails;
        book.CreatedAt = createdAt;
        book.NormalizedIsbn = normalizedIsbn;
        book.NormalizedAsin = normalizedAsin;
        book.WorkId = workId;
        book.Work = work;
        book.BookCollections = collections;
        book.Acquisition = acquisition;
        return book;
    }

    /// <summary>
    /// A note with only what note readers show about its book: title, author,
    /// work and format. A note read never needs more, and one row per note is
    /// exactly where a wide book row hurts.
    /// </summary>
    public static readonly Expression<Func<NoteModel, NoteWithBook>> NoteWithBookHeadline = n => new NoteWithBook(
        n,
        n.Book == null
            ? null
            : Headline(
                n.Book is AudioBookModel,
                n.Book is PhysicalBookModel,
                n.Book.Id,
                n.Book.Title,
                n.Book.Author,
                n.Book.WorkId,
                n.Book.FileDetails.FileName));

    private static BookModel Headline(
        bool audiobook,
        bool physical,
        Guid id,
        string title,
        string? author,
        Guid workId,
        string? fileName)
    {
        BookModel book = audiobook ? new AudioBookModel() : physical ? new PhysicalBookModel() : new EBookModel();
        book.Id = id;
        book.Title = title;
        book.Author = author;
        book.WorkId = workId;
        book.FileDetails.FileName = fileName;
        return book;
    }

    /// <summary>
    /// Runs a note query with <see cref="NoteModel.Book"/> set to its headline.
    /// Other includes on the query still apply. Always no-tracking: a tracked note
    /// would see the headline as a new book and try to insert it.
    /// </summary>
    public static async Task<List<NoteModel>> ToListWithBookHeadlineAsync(
        this IQueryable<NoteModel> notes,
        CancellationToken ct = default)
    {
        var rows = await notes.AsNoTracking().Select(NoteWithBookHeadline).ToListAsync(ct);
        return rows.Select(row => row.ToNote()).ToList();
    }
}

/// <summary>A note and its book headline, as one row of <see cref="BookReadShapes.NoteWithBookHeadline"/>.</summary>
internal sealed record NoteWithBook(NoteModel Note, BookModel? Book)
{
    public NoteModel ToNote()
    {
        Note.Book = Book;
        return Note;
    }
}

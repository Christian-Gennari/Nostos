using Nostos.Backend.Data.Models;
using static Nostos.Backend.Services.Portability.Verification.PortableVerificationPrimitives;

namespace Nostos.Backend.Services.Portability.Verification;

/// <summary>Works, their editions and every edition-level field, including the
/// stored file metadata that names the archive media.</summary>
internal static class PortableBookComparisons
{
    internal static readonly IReadOnlyList<ComparisonEntry<WorkContext>> WorkComparisons =
    [
        Entry<WorkContext>(
            nameof(PortableWork.Id),
            Ef(("WorkModel", nameof(WorkModel.Id))),
            (context, failures) => CompareField(failures, "work", context.Row.Id, nameof(PortableWork.Id), context.Row.Id, context.Expected.Id)),
        Entry<WorkContext>(
            nameof(PortableWork.Title),
            Ef(("WorkModel", nameof(WorkModel.Title))),
            (context, failures) => CompareField(failures, "work", context.Row.Id, nameof(PortableWork.Title), context.Row.Title, context.Expected.Title)),
        Entry<WorkContext>(
            nameof(PortableWork.Author),
            Ef(("WorkModel", nameof(WorkModel.Author))),
            (context, failures) => CompareField(failures, "work", context.Row.Id, nameof(PortableWork.Author), context.Row.Author, context.Expected.Author)),
        Entry<WorkContext>(
            nameof(PortableWork.CreatedAt),
            Ef(("WorkModel", nameof(WorkModel.CreatedAt))),
            (context, failures) => CompareField(failures, "work", context.Row.Id, nameof(PortableWork.CreatedAt), context.Row.CreatedAt, context.Expected.CreatedAt)),
        Entry<WorkContext>(
            nameof(PortableWork.Rating),
            Ef(("WorkModel", nameof(WorkModel.Rating))),
            (context, failures) => CompareField(failures, "work", context.Row.Id, nameof(PortableWork.Rating), context.Row.Rating, context.Expected.Rating)),
        Entry<WorkContext>(
            nameof(PortableWork.IsFavorite),
            Ef(("WorkModel", nameof(WorkModel.IsFavorite))),
            (context, failures) => CompareField(failures, "work", context.Row.Id, nameof(PortableWork.IsFavorite), context.Row.IsFavorite, context.Expected.IsFavorite)),
        Entry<WorkContext>(
            nameof(PortableWork.PersonalReview),
            Ef(("WorkModel", nameof(WorkModel.PersonalReview))),
            (context, failures) => CompareField(failures, "work", context.Row.Id, nameof(PortableWork.PersonalReview), context.Row.PersonalReview, context.Expected.PersonalReview)),
    ];

    internal static readonly IReadOnlyList<ComparisonEntry<BookContext>> BookComparisons =
    [
        Entry<BookContext>(
            nameof(PortableBook.Id),
            Ef(("BookModel", nameof(BookModel.Id))),
            (context, failures) => CompareField(failures, "book", context.Row.Id, nameof(PortableBook.Id), context.Row.Id, context.Expected.Id)),
        Entry<BookContext>(
            nameof(PortableBook.WorkId),
            Ef(("BookModel", nameof(BookModel.WorkId))),
            (context, failures) => CompareField(failures, "book", context.Row.Id, nameof(PortableBook.WorkId), context.Row.WorkId, context.Expected.WorkId)),
        Entry<BookContext>(
            nameof(PortableBook.Type),
            NoFields,
            (context, failures) => CompareField(failures, "book", context.Row.Id, "Type", ConcreteType(context.Row), context.Expected.Type, ignoreCase: true)),
        Entry<BookContext>(
            nameof(PortableBook.Status),
            Ef(("BookModel", nameof(BookModel.Status))),
            (context, failures) => CompareField(failures, "book", context.Row.Id, nameof(PortableBook.Status), context.Row.Status.ToString(), context.Expected.Status, ignoreCase: true)),
        Entry<BookContext>(
            nameof(PortableBook.StatusMessage),
            Ef(("BookModel", nameof(BookModel.StatusMessage))),
            (context, failures) => CompareField(failures, "book", context.Row.Id, nameof(PortableBook.StatusMessage), context.Row.StatusMessage, context.Expected.StatusMessage)),
        Entry<BookContext>(
            nameof(PortableBook.Title),
            Ef(("BookModel", nameof(BookModel.Title))),
            (context, failures) => CompareField(failures, "book", context.Row.Id, nameof(PortableBook.Title), context.Row.Title, context.Expected.Title)),
        Entry<BookContext>(
            nameof(PortableBook.Author),
            Ef(("BookModel", nameof(BookModel.Author))),
            (context, failures) => CompareField(failures, "book", context.Row.Id, nameof(PortableBook.Author), context.Row.Author, context.Expected.Author)),
        Entry<BookContext>(
            nameof(PortableBook.Metadata),
            Ef(
                ("BookMetadata", nameof(BookMetadata.Subtitle)),
                ("BookMetadata", nameof(BookMetadata.Description)),
                ("BookMetadata", nameof(BookMetadata.Editor)),
                ("BookMetadata", nameof(BookMetadata.Translator)),
                ("BookMetadata", nameof(BookMetadata.Publisher)),
                ("BookMetadata", nameof(BookMetadata.PlaceOfPublication)),
                ("BookMetadata", nameof(BookMetadata.PublishedDate)),
                ("BookMetadata", nameof(BookMetadata.Language)),
                ("BookMetadata", nameof(BookMetadata.Categories)),
                ("BookMetadata", nameof(BookMetadata.Edition)),
                ("BookMetadata", nameof(BookMetadata.Series)),
                ("BookMetadata", nameof(BookMetadata.VolumeNumber))),
            (context, failures) => Run(
                BookMetadataComparisons,
                new BookMetadataContext(context.Row.Id, context.Row.Metadata, context.Expected.Metadata),
                failures)),
        Entry<BookContext>(
            nameof(PortableBook.Progress),
            Ef(
                ("ReadingProgress", nameof(ReadingProgress.LastLocation)),
                ("ReadingProgress", nameof(ReadingProgress.ProgressPercent)),
                ("ReadingProgress", nameof(ReadingProgress.Rating)),
                ("ReadingProgress", nameof(ReadingProgress.PersonalReview)),
                ("ReadingProgress", nameof(ReadingProgress.LastReadAt)),
                ("ReadingProgress", nameof(ReadingProgress.FinishedAt))),
            (context, failures) => Run(
                PortableProgressComparisons.BookProgressComparisons,
                new BookProgressContext(context.Row.Id, context.Row.Progress, context.Expected.Progress),
                failures)),
        Entry<BookContext>(
            nameof(PortableBook.CreatedAt),
            Ef(("BookModel", nameof(BookModel.CreatedAt))),
            (context, failures) => CompareField(failures, "book", context.Row.Id, nameof(PortableBook.CreatedAt), context.Row.CreatedAt, context.Expected.CreatedAt)),
        Entry<BookContext>(
            nameof(PortableBook.Isbn),
            Ef(("PhysicalBookModel", nameof(PhysicalBookModel.Isbn)), ("EBookModel", nameof(EBookModel.Isbn))),
            (context, failures) =>
            {
                var isbn = context.Row switch
                {
                    PhysicalBookModel physical => physical.Isbn,
                    EBookModel ebook => ebook.Isbn,
                    _ => null,
                };
                CompareField(failures, "book", context.Row.Id, nameof(PhysicalBookModel.Isbn), isbn, context.Expected.Isbn);
            }),
        Entry<BookContext>(
            nameof(PortableBook.PageCount),
            Ef(("PhysicalBookModel", nameof(PhysicalBookModel.PageCount)), ("EBookModel", nameof(EBookModel.PageCount))),
            (context, failures) =>
            {
                var pageCount = context.Row switch
                {
                    PhysicalBookModel physical => physical.PageCount,
                    EBookModel ebook => ebook.PageCount,
                    _ => null,
                };
                CompareField(failures, "book", context.Row.Id, nameof(PhysicalBookModel.PageCount), pageCount, context.Expected.PageCount);
            }),
        Entry<BookContext>(
            nameof(PortableBook.Asin),
            Ef(("AudioBookModel", nameof(AudioBookModel.Asin))),
            (context, failures) => CompareField(failures, "book", context.Row.Id, nameof(AudioBookModel.Asin), (context.Row as AudioBookModel)?.Asin, context.Expected.Asin)),
        Entry<BookContext>(
            nameof(PortableBook.Duration),
            Ef(("AudioBookModel", nameof(AudioBookModel.Duration))),
            (context, failures) => CompareField(failures, "book", context.Row.Id, nameof(AudioBookModel.Duration), (context.Row as AudioBookModel)?.Duration, context.Expected.Duration)),
        Entry<BookContext>(
            nameof(PortableBook.Narrator),
            Ef(("AudioBookModel", nameof(AudioBookModel.Narrator))),
            (context, failures) => CompareField(failures, "book", context.Row.Id, nameof(AudioBookModel.Narrator), (context.Row as AudioBookModel)?.Narrator, context.Expected.Narrator)),
        Entry<BookContext>(
            nameof(PortableBook.ChaptersJson),
            Ef(("FileInfoDetails", nameof(FileInfoDetails.ChaptersJson))),
            (context, failures) => CompareField(failures, "book", context.Row.Id, nameof(FileInfoDetails.ChaptersJson), context.Row.FileDetails.ChaptersJson, context.Expected.ChaptersJson)),
        Entry<BookContext>(
            nameof(PortableBook.TracksJson),
            Ef(("FileInfoDetails", nameof(FileInfoDetails.TracksJson))),
            (context, failures) => CompareField(
                failures,
                "book",
                context.Row.Id,
                nameof(FileInfoDetails.TracksJson),
                context.Row.FileDetails.TracksJson,
                string.IsNullOrWhiteSpace(context.Expected.TracksJson) ? null : context.Expected.TracksJson)),
        Entry<BookContext>(
            nameof(PortableBook.HasBookFile),
            Ef(("FileInfoDetails", nameof(FileInfoDetails.HasFile)), ("FileInfoDetails", nameof(FileInfoDetails.FileName))),
            (context, failures) =>
            {
                CompareField(
                    failures,
                    "book",
                    context.Row.Id,
                    nameof(FileInfoDetails.HasFile),
                    context.Row.FileDetails.HasFile,
                    // A multi-track audiobook has a file to play without a
                    // primary book file.
                    context.Expected.HasBookFile || !string.IsNullOrWhiteSpace(context.Expected.TracksJson));
                CompareField(
                    failures,
                    "book",
                    context.Row.Id,
                    nameof(FileInfoDetails.FileName),
                    context.Row.FileDetails.FileName,
                    ResolveMediaFileName(context.Media, context.Row.Id, PortableArchiveFormat.BookMediaKind));
            }),
        Entry<BookContext>(
            nameof(PortableBook.HasCover),
            Ef(("FileInfoDetails", nameof(FileInfoDetails.CoverFileName))),
            (context, failures) =>
            {
                var expectedName = ResolveMediaFileName(context.Media, context.Row.Id, PortableArchiveFormat.CoverMediaKind);
                CompareField(
                    failures,
                    "book",
                    context.Row.Id,
                    "HasCover",
                    context.Row.FileDetails.CoverFileName is not null,
                    context.Expected.HasCover);
                CompareField(
                    failures,
                    "book",
                    context.Row.Id,
                    nameof(FileInfoDetails.CoverFileName),
                    context.Row.FileDetails.CoverFileName,
                    expectedName);
            }),
    ];

    internal static readonly IReadOnlyList<ComparisonEntry<BookMetadataContext>> BookMetadataComparisons =
    [
        Entry<BookMetadataContext>(
            nameof(PortableBookMetadata.Subtitle),
            Ef(("BookMetadata", nameof(BookMetadata.Subtitle))),
            (context, failures) => CompareField(failures, "book", context.BookId, nameof(BookMetadata.Subtitle), context.Row.Subtitle, context.Expected.Subtitle)),
        Entry<BookMetadataContext>(
            nameof(PortableBookMetadata.Description),
            Ef(("BookMetadata", nameof(BookMetadata.Description))),
            (context, failures) => CompareField(failures, "book", context.BookId, nameof(BookMetadata.Description), context.Row.Description, context.Expected.Description)),
        Entry<BookMetadataContext>(
            nameof(PortableBookMetadata.Editor),
            Ef(("BookMetadata", nameof(BookMetadata.Editor))),
            (context, failures) => CompareField(failures, "book", context.BookId, nameof(BookMetadata.Editor), context.Row.Editor, context.Expected.Editor)),
        Entry<BookMetadataContext>(
            nameof(PortableBookMetadata.Translator),
            Ef(("BookMetadata", nameof(BookMetadata.Translator))),
            (context, failures) => CompareField(failures, "book", context.BookId, nameof(BookMetadata.Translator), context.Row.Translator, context.Expected.Translator)),
        Entry<BookMetadataContext>(
            nameof(PortableBookMetadata.Publisher),
            Ef(("BookMetadata", nameof(BookMetadata.Publisher))),
            (context, failures) => CompareField(failures, "book", context.BookId, nameof(BookMetadata.Publisher), context.Row.Publisher, context.Expected.Publisher)),
        Entry<BookMetadataContext>(
            nameof(PortableBookMetadata.PlaceOfPublication),
            Ef(("BookMetadata", nameof(BookMetadata.PlaceOfPublication))),
            (context, failures) => CompareField(failures, "book", context.BookId, nameof(BookMetadata.PlaceOfPublication), context.Row.PlaceOfPublication, context.Expected.PlaceOfPublication)),
        Entry<BookMetadataContext>(
            nameof(PortableBookMetadata.PublishedDate),
            Ef(("BookMetadata", nameof(BookMetadata.PublishedDate))),
            (context, failures) => CompareField(failures, "book", context.BookId, nameof(BookMetadata.PublishedDate), context.Row.PublishedDate, context.Expected.PublishedDate)),
        Entry<BookMetadataContext>(
            nameof(PortableBookMetadata.Language),
            Ef(("BookMetadata", nameof(BookMetadata.Language))),
            (context, failures) => CompareField(failures, "book", context.BookId, nameof(BookMetadata.Language), context.Row.Language, context.Expected.Language)),
        Entry<BookMetadataContext>(
            nameof(PortableBookMetadata.Categories),
            Ef(("BookMetadata", nameof(BookMetadata.Categories))),
            (context, failures) => CompareField(failures, "book", context.BookId, nameof(BookMetadata.Categories), context.Row.Categories, context.Expected.Categories)),
        Entry<BookMetadataContext>(
            nameof(PortableBookMetadata.Edition),
            Ef(("BookMetadata", nameof(BookMetadata.Edition))),
            (context, failures) => CompareField(failures, "book", context.BookId, nameof(BookMetadata.Edition), context.Row.Edition, context.Expected.Edition)),
        Entry<BookMetadataContext>(
            nameof(PortableBookMetadata.Series),
            Ef(("BookMetadata", nameof(BookMetadata.Series))),
            (context, failures) => CompareField(failures, "book", context.BookId, nameof(BookMetadata.Series), context.Row.Series, context.Expected.Series)),
        Entry<BookMetadataContext>(
            nameof(PortableBookMetadata.VolumeNumber),
            Ef(("BookMetadata", nameof(BookMetadata.VolumeNumber))),
            (context, failures) => CompareField(failures, "book", context.BookId, nameof(BookMetadata.VolumeNumber), context.Row.VolumeNumber, context.Expected.VolumeNumber)),
    ];

    internal static void CompareWorks(
        PortableLibraryData data,
        List<WorkModel> works,
        FailureList failures)
    {
        var expected = data.Works.ToDictionary(item => item.Id);
        var actual = works.ToDictionary(item => item.Id);
        CompareIdSets("work", expected.Keys, actual.Keys, failures);
        foreach (var (id, item) in expected)
        {
            if (actual.TryGetValue(id, out var row))
            {
                Run(WorkComparisons, new WorkContext(row, item), failures);
            }
        }
    }

    internal static void CompareBooks(
        PortableLibraryData data,
        List<BookModel> books,
        IReadOnlyDictionary<(Guid BookId, string Kind), PortableArchiveMediaEntry> media,
        FailureList failures)
    {
        var expected = data.Books.ToDictionary(
            item => item.Id,
            item => PortableLegacyPhysicalFileBooks.ToCurrent(
                item,
                ResolveMediaFileName(media, item.Id, PortableArchiveFormat.BookMediaKind)));
        var actual = books.ToDictionary(item => item.Id);
        CompareIdSets("book", expected.Keys, actual.Keys, failures);
        foreach (var (id, item) in expected)
        {
            if (actual.TryGetValue(id, out var row))
            {
                Run(BookComparisons, new BookContext(row, item, media), failures);
            }
        }
    }
}

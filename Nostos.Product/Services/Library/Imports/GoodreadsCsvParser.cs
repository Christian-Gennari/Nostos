using Nostos.Backend.Services.Notes.Imports;

namespace Nostos.Backend.Services.Library.Imports;

/// <summary>
/// Reads a Goodreads library export (<c>goodreads_library_export.csv</c>, from
/// My Books → Import and export): one row per book with the shelf it sits on,
/// the rating, the review, the date read and the custom shelves. Columns are
/// found by name, so an export with more, fewer or reordered columns still
/// reads. Identifiers arrive wrapped as <c>="9780000000019"</c>.
/// </summary>
internal static class GoodreadsCsvParser
{
    public const string Source = "goodreads";

    internal const string NotGoodreads = "This file is not a Goodreads library export.";

    public static bool Looks(string text)
    {
        var header = ReadingHistoryCsv.HeaderText(text);
        var columns = ReadwiseCsvParser.Records(header).FirstOrDefault() ?? [];
        return ReadingHistoryCsv.Column(columns, "Book Id") >= 0
            && ReadingHistoryCsv.Column(columns, "Exclusive Shelf") >= 0
            && ReadingHistoryCsv.Column(columns, "My Rating") >= 0;
    }

    public static (IReadOnlyList<ReadingHistorySourceBook> Books, IReadOnlyList<ReadingHistorySkipped> Skipped) Parse(string text)
    {
        var records = ReadwiseCsvParser.Records(text.TrimStart('\uFEFF')).ToList();
        if (records.Count == 0)
            throw new FormatException("This Goodreads export is empty.");

        var header = records[0];
        var bookId = ReadingHistoryCsv.Column(header, "Book Id");
        var title = ReadingHistoryCsv.Column(header, "Title");
        if (title < 0)
            throw new FormatException(NotGoodreads);

        var author = ReadingHistoryCsv.Column(header, "Author");
        var isbn = ReadingHistoryCsv.Column(header, "ISBN");
        var isbn13 = ReadingHistoryCsv.Column(header, "ISBN13");
        var rating = ReadingHistoryCsv.Column(header, "My Rating");
        var review = ReadingHistoryCsv.Column(header, "My Review");
        var notes = ReadingHistoryCsv.Column(header, "Private Notes");
        var dateRead = ReadingHistoryCsv.Column(header, "Date Read");
        var bookshelves = ReadingHistoryCsv.Column(header, "Bookshelves");
        var exclusive = ReadingHistoryCsv.Column(header, "Exclusive Shelf");
        var readCount = ReadingHistoryCsv.Column(header, "Read Count");

        var books = new List<ReadingHistorySourceBook>();
        var skipped = new ReadingHistorySkipTally();

        foreach (var record in records.Skip(1))
        {
            if (record.All(string.IsNullOrWhiteSpace))
                continue;

            var rowTitle = ReadingHistoryCsv.Cell(record, title);
            var rowAuthor = ReadingHistoryCsv.Cell(record, author);
            if (rowTitle is null)
            {
                skipped.Add(null, rowAuthor, ReadingHistoryCsv.EmptyReason);
                continue;
            }

            var rawShelf = ReadingHistoryCsv.Cell(record, exclusive);
            var status = ReadingHistoryCsv.Status(rawShelf);
            var (rowIsbn10, rowIsbn13) = Identifiers(
                ReadingHistoryCsv.UnwrapIdentifier(ReadingHistoryCsv.Cell(record, isbn)),
                ReadingHistoryCsv.UnwrapIdentifier(ReadingHistoryCsv.Cell(record, isbn13)));
            var (stars, notice) = ReadingHistoryCsv.Rating(ReadingHistoryCsv.Cell(record, rating));

            books.Add(new ReadingHistorySourceBook(
                ReadingHistoryCsv.Key(Source, ReadingHistoryCsv.Cell(record, bookId), rowIsbn13 ?? rowIsbn10, rowTitle, rowAuthor),
                rowTitle,
                rowAuthor,
                rowIsbn10,
                rowIsbn13,
                stars,
                ReadingHistoryCsv.Cell(record, review),
                ReadingHistoryCsv.Cell(record, notes),
                // Goodreads keeps the last read in Date Read; Read Count says
                // it was read more than once and has no further dates.
                ReadingHistoryCsv.Date(ReadingHistoryCsv.Cell(record, dateRead)),
                status,
                ReadingHistoryCsv.Count(ReadingHistoryCsv.Cell(record, readCount)),
                ReadingHistoryCsv.Shelves(ReadingHistoryCsv.Cell(record, bookshelves), rawShelf),
                notice));
        }

        return (books, skipped.ToList());
    }

    /// <summary>
    /// Both identifier columns are optional and either may hold a 10- or
    /// 13-digit ISBN; they are sorted by length so matching can try ISBN-13
    /// first, as the issue asks.
    /// </summary>
    private static (string? Isbn10, string? Isbn13) Identifiers(string? isbn, string? isbn13)
    {
        string? ten = null;
        string? thirteen = null;
        foreach (var candidate in new[] { BookIdentityNormalizer.NormalizeIsbn(isbn13), BookIdentityNormalizer.NormalizeIsbn(isbn) })
        {
            switch (candidate?.Length)
            {
                case 13 when thirteen is null:
                    thirteen = candidate;
                    break;
                case 10 when ten is null:
                    ten = candidate;
                    break;
            }
        }

        return (ten, thirteen);
    }
}

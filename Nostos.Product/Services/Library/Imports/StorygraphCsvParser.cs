using Nostos.Backend.Services.Notes.Imports;

namespace Nostos.Backend.Services.Library.Imports;

/// <summary>
/// Reads a StoryGraph library export (CSV, from Manage Account → Export
/// StoryGraph Library): one row per book with its read status, quarter-star
/// rating, review, every read's dates and custom tags. The identifier column
/// mixes ISBNs with StoryGraph's own ids, so only a valid ISBN is used to
/// match. Columns are found by name, so reordering does not break it.
/// </summary>
internal static class StorygraphCsvParser
{
    public const string Source = "storygraph";

    internal const string NotStorygraph = "This file is not a StoryGraph export.";

    public static bool Looks(string text)
    {
        var header = ReadingHistoryCsv.HeaderText(text);
        var columns = ReadwiseCsvParser.Records(header).FirstOrDefault() ?? [];
        return ReadingHistoryCsv.Column(columns, "Read Status") >= 0
            && ReadingHistoryCsv.Column(columns, "Star Rating") >= 0
            && ReadingHistoryCsv.Column(columns, "ISBN/UID") >= 0;
    }

    public static (IReadOnlyList<ReadingHistorySourceBook> Books, IReadOnlyList<ReadingHistorySkipped> Skipped) Parse(string text)
    {
        var records = ReadwiseCsvParser.Records(text.TrimStart('\uFEFF')).ToList();
        if (records.Count == 0)
            throw new FormatException("This StoryGraph export is empty.");

        var header = records[0];
        var title = ReadingHistoryCsv.Column(header, "Title");
        if (title < 0)
            throw new FormatException(NotStorygraph);

        var authors = ReadingHistoryCsv.Column(header, "Authors");
        var identifier = ReadingHistoryCsv.Column(header, "ISBN/UID");
        var status = ReadingHistoryCsv.Column(header, "Read Status");
        var rating = ReadingHistoryCsv.Column(header, "Star Rating");
        var review = ReadingHistoryCsv.Column(header, "Review");
        var lastDateRead = ReadingHistoryCsv.Column(header, "Last Date Read");
        var datesRead = ReadingHistoryCsv.Column(header, "Dates Read");
        var readCount = ReadingHistoryCsv.Column(header, "Read Count");
        var tags = ReadingHistoryCsv.Column(header, "Tags");

        var books = new List<ReadingHistorySourceBook>();
        var skipped = new ReadingHistorySkipTally();

        foreach (var record in records.Skip(1))
        {
            if (record.All(string.IsNullOrWhiteSpace))
                continue;

            var rowTitle = ReadingHistoryCsv.Cell(record, title);
            var rowAuthor = ReadingHistoryCsv.Cell(record, authors);
            if (rowTitle is null)
            {
                skipped.Add(null, rowAuthor, ReadingHistoryCsv.EmptyReason);
                continue;
            }

            var rawIdentifier = ReadingHistoryCsv.Cell(record, identifier);
            var isbn = BookIdentityNormalizer.NormalizeIsbn(rawIdentifier);
            var (stars, notice) = ReadingHistoryCsv.Rating(ReadingHistoryCsv.Cell(record, rating));

            // Last Date Read is the direct answer; Dates Read is the fallback
            // when it is empty, and its last end is the last read.
            var finishedAt = ReadingHistoryCsv.Date(ReadingHistoryCsv.Cell(record, lastDateRead))
                ?? ReadingHistoryCsv.LastRead([ReadingHistoryCsv.Cell(record, datesRead)]);

            books.Add(new ReadingHistorySourceBook(
                // A StoryGraph id that is not an ISBN is not portable, so the
                // row falls back to ISBN or title+author for its identity.
                ReadingHistoryCsv.Key(Source, null, isbn, rowTitle, rowAuthor),
                rowTitle,
                rowAuthor,
                isbn?.Length == 10 ? isbn : null,
                isbn?.Length == 13 ? isbn : null,
                stars,
                ReadingHistoryCsv.Cell(record, review),
                // StoryGraph's export has no private-notes column.
                null,
                finishedAt,
                ReadingHistoryCsv.Status(ReadingHistoryCsv.Cell(record, status)),
                ReadingHistoryCsv.Count(ReadingHistoryCsv.Cell(record, readCount)),
                ReadingHistoryCsv.Shelves(ReadingHistoryCsv.Cell(record, tags)),
                notice));
        }

        return (books, skipped.ToList());
    }
}

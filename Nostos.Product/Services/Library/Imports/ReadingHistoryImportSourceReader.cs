using Nostos.Backend.Services.Notes.Imports;

namespace Nostos.Backend.Services.Library.Imports;

/// <summary>
/// Decides which reading-history export a file is by its header row, as the
/// highlight import decides its sources by content: a Goodreads header names
/// <c>Exclusive Shelf</c>, a StoryGraph header names <c>Read Status</c>. The
/// file name is never consulted.
/// </summary>
internal static class ReadingHistoryImportSourceReader
{
    // A full library export is a few megabytes at most; this only stops an
    // obviously wrong file from being read whole.
    private const long MaxTextBytes = 32 * 1024 * 1024;

    internal const string Unrecognised =
        "This file is not an export Nostos can read. Choose the CSV from Goodreads (My Books → Import and export) or StoryGraph (Manage Account → Export StoryGraph Library).";

    public static ReadingHistorySourceFile Read(string path)
    {
        if (new FileInfo(path).Length > MaxTextBytes)
            throw new FormatException(Unrecognised);

        var text = HighlightImportSourceReader.DecodeText(File.ReadAllBytes(path)).TrimStart('\uFEFF');
        if (GoodreadsCsvParser.Looks(text))
        {
            var (books, skipped) = GoodreadsCsvParser.Parse(text);
            return new ReadingHistorySourceFile(GoodreadsCsvParser.Source, books, skipped);
        }

        if (StorygraphCsvParser.Looks(text))
        {
            var (books, skipped) = StorygraphCsvParser.Parse(text);
            return new ReadingHistorySourceFile(StorygraphCsvParser.Source, books, skipped);
        }

        throw new FormatException(Unrecognised);
    }
}

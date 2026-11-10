namespace Nostos.Backend.Services.Notes.Imports;

/// <summary>
/// The one event that says what an import file looked like when it failed or
/// when a known source changed shape (issue #1523). Cloud logs can be searched
/// for <c>HighlightImportShape</c> and counted by <c>Outcome</c>, so a new Kobo
/// firmware, Readwise column or Kindle language is visible without a user
/// writing in. The event carries the source, the outcome and a structural
/// description from <see cref="ImportFileShape"/>: never a highlight, title,
/// author, file name, path or user identifier.
/// </summary>
internal static class HighlightImportShapeLog
{
    public const string Name = "HighlightImportShape";

    /// <summary>The file failed to read as any source the import knows.</summary>
    public const string ParseFailed = "parse-failed";

    /// <summary>A column the reader tolerates was gone: the earliest sign of a change.</summary>
    public const string MissingOptionalColumns = "missing-optional-columns";

    /// <summary>A preview read the file but left entries out as an unknown format.</summary>
    public const string UnrecognisedEntries = "unrecognised-entries";

    // The issue number keeps the numeric id stable; the name is what a log
    // search uses and what the message below repeats.
    public static readonly EventId Event = new(1523, Name);

    public static void Write(
        ILogger logger,
        string path,
        string outcome,
        IReadOnlyList<string>? missingOptionalColumns = null)
    {
        var (source, shape) = Describe(path);
        if (missingOptionalColumns is { Count: > 0 })
            shape = ImportFileShape.Cap($"{shape}; missing optional columns: {string.Join(", ", missingOptionalColumns)}");

        logger.LogWarning(Event, "HighlightImportShape {Source} {Outcome} {Shape}", source, outcome, shape);
    }

    // Describing a user-supplied file is best effort: the import's own answer
    // must never change because a diagnostic could not be produced.
    private static (string Source, string Shape) Describe(string path)
    {
        try
        {
            return ImportFileShape.Describe(path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ("unknown", "shape unavailable");
        }
    }
}

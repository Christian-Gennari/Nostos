namespace Nostos.Backend.Services.Library;

/// <summary>
/// A physical row that owns a digital file: the shape older builds created,
/// because Add Book defaulted to "Physical book" and any row accepted an
/// upload. Since #332 a physical book is metadata-only, so these rows are not
/// a shape the product makes any more — but they exist, and the book page shows
/// paper controls beside a working Read button for them.
///
/// The rule here is the one the database migration runs in SQL
/// (<c>ConvertLegacyPhysicalBooksWithFiles</c>): classify the row by the file it
/// already holds, and leave it alone when the file's format or the saved
/// reading position makes the answer a guess.
///
/// Nothing is ever split into two editions: the "physical" label came from the
/// Add Book default, not from anyone saying they own a paper copy.
/// </summary>
public static class LegacyPhysicalFileBook
{
    /// <summary>What a legacy physical row with a file becomes.</summary>
    public enum Resolution
    {
        /// <summary>Not this shape, or ambiguous: the row stays physical.</summary>
        LeavePhysical,

        Ebook,

        Audiobook,
    }

    /// <summary>
    /// Classifies one physical row by the file name it holds.
    /// <paramref name="lastLocation"/> is the saved reading position: a row
    /// read in the paper format (<c>{"page":N}</c>) was being read as paper, so
    /// converting it would move someone's place in the book. Those rows stay
    /// physical, as do rows whose file name has no book extension this product
    /// accepts.
    /// </summary>
    public static Resolution Resolve(string? fileName, string? lastLocation)
    {
        if (IsPaperProgress(lastLocation))
            return Resolution.LeavePhysical;

        return ResolutionForFile(fileName);
    }

    /// <summary>
    /// The same rule for a RESTORE of an archive that carries this shape.
    ///
    /// A paper reading position does not stop the conversion here, and it is
    /// left exactly as it was: restoring cannot leave a physical book holding a
    /// file (the model refuses that shape now), and dropping the file to keep
    /// the label would lose the book. Refusing the archive is not an option
    /// either — an older build wrote this shape legitimately.
    /// </summary>
    public static Resolution ResolveForRestore(string? fileName) => ResolutionForFile(fileName);

    private static Resolution ResolutionForFile(string? fileName) =>
        BookAssetFormats.DigitalFormatForFileName(fileName) switch
        {
            BookDigitalFormat.Audiobook => Resolution.Audiobook,
            BookDigitalFormat.Ebook => Resolution.Ebook,
            _ => Resolution.LeavePhysical,
        };

    /// <summary>
    /// True when a saved position is in the paper reader's format, the JSON
    /// object <c>{"page":N}</c>. The PDF reader saves
    /// <c>{"pageNumber":N,...}</c> and the EPUB reader an <c>epubcfi(...)</c>
    /// string, so neither is mistaken for a paper page.
    ///
    /// The check is the paper reader's own serialization
    /// (<c>JSON.stringify({page})</c>) rather than a general JSON read, so it
    /// says exactly what the migration's SQL <c>LIKE</c> says and the two rules
    /// cannot drift apart.
    /// </summary>
    public static bool IsPaperProgress(string? lastLocation)
    {
        if (string.IsNullOrWhiteSpace(lastLocation))
            return false;

        var compact = string.Concat(lastLocation.Where(c => !char.IsWhiteSpace(c)));
        return compact.StartsWith("{\"page\":", StringComparison.Ordinal);
    }
}

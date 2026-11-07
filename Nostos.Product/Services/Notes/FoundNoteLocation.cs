using System.Globalization;
using System.Text.Json;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Mapping;

namespace Nostos.Backend.Services.Notes;

/// <summary>
/// The rule for a location a reader found by a note's text (issue #1338). The
/// reader is trusted with where the words are, never with which notes may be
/// moved, so that stays narrow here: nothing that has a place loses it, and a
/// note a person wrote or marked is never touched.
/// </summary>
internal static class FoundNoteLocation
{
    private const int MaxCfiLength = 1024;
    private const int MaxPdfLocationLength = 32 * 1024;
    private const int MaxPdfRects = 256;

    /// <summary>
    /// The shape of a quote Ask Nostos or a connected app (MCP, <c>mcp</c>)
    /// saved and nothing else. Every other way
    /// to write this shape leaves a trace: a highlight made in a reader has a
    /// location, a typed note has no quote, a photographed page is
    /// <c>photo</c>, an import is <c>import</c>. No stamp is needed, and none
    /// exists on the notes saved before this rule; capture receipts cannot
    /// serve, because they are pruned and do not travel in an archive.
    /// </summary>
    public static bool IsAskNostosQuote(NoteModel note) =>
        !note.IsPassage()
        && note.CaptureSource is "text" or "mcp"
        && !string.IsNullOrWhiteSpace(note.SelectedText)
        && note.SourceAnchorKind is "unknown" or "epub_cfi" or "pdf_page";

    public static bool IsWellFormed(string location) =>
        IsEpubCfi(location) || PdfLocation.TryParse(location) is { Rects: > 0 };

    /// <summary>Whether this note may be given this location at all.</summary>
    public static bool Allows(NoteModel note, string location)
    {
        if (note.CaptureSource == "import")
            return IsEpubCfi(location);
        if (!IsAskNostosQuote(note))
            return false;

        // A range, so it can be drawn. For a PDF, the rectangles of the page the
        // note already names: the reader is not choosing the page.
        return IsEpubCfi(location)
            ? IsEpubCfiRange(location) && note.SourceAnchorKind != "pdf_page"
            : PdfLocation.TryParse(location)?.Page == AnchoredPage(note);
    }

    /// <summary>
    /// True when the location adds to the note instead of replacing anything.
    /// A PDF page with no rectangles says only where the note belongs, so
    /// rectangles on that same page fill it in. Any other location stays.
    /// </summary>
    public static bool Fills(NoteModel note, string location)
    {
        if (string.IsNullOrWhiteSpace(note.CfiRange))
            return true;

        return PdfLocation.TryParse(note.CfiRange) is { Rects: 0 } stored
            && stored.Page == PdfLocation.TryParse(location)?.Page;
    }

    private static int? AnchoredPage(NoteModel note) =>
        note.SourceAnchorKind == "pdf_page"
        && int.TryParse(note.SourceAnchorValue, NumberStyles.None, CultureInfo.InvariantCulture, out var page)
        && page > 0
            ? page
            : null;

    private static bool IsEpubCfi(string location) =>
        location.StartsWith("epubcfi(", StringComparison.Ordinal)
        && location.EndsWith(')')
        && location.Length <= MaxCfiLength;

    // epubcfi(parent,start,end). A comma that belongs to the path is escaped.
    private static bool IsEpubCfiRange(string location)
    {
        var separators = 0;
        for (var i = 1; i < location.Length; i++)
        {
            if (location[i] == ',' && location[i - 1] != '^')
                separators++;
        }

        return separators == 2;
    }

    private sealed record PdfLocation(int Page, int Rects)
    {
        public static PdfLocation? TryParse(string? location)
        {
            if (location is not { Length: > 0 and <= MaxPdfLocationLength } || location[0] != '{')
                return null;

            try
            {
                using var document = JsonDocument.Parse(location);
                var root = document.RootElement;
                if (!root.TryGetProperty("pageNumber", out var number)
                    || number.ValueKind != JsonValueKind.Number
                    || !number.TryGetInt32(out var page)
                    || page < 1
                    || !root.TryGetProperty("rects", out var rects)
                    || rects.ValueKind != JsonValueKind.Array
                    || rects.GetArrayLength() > MaxPdfRects
                    || !rects.EnumerateArray().All(IsPageRect))
                {
                    return null;
                }

                return new PdfLocation(page, rects.GetArrayLength());
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static bool IsPageRect(JsonElement rect) =>
            rect.ValueKind == JsonValueKind.Object
            && IsFraction(rect, "left")
            && IsFraction(rect, "top")
            && IsFraction(rect, "width")
            && IsFraction(rect, "height");

        private static bool IsFraction(JsonElement rect, string name) =>
            rect.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.GetDouble() is >= 0 and <= 1;
    }
}

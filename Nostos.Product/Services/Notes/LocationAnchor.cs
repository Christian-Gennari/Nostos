using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Services.Notes;

/// <summary>
/// The source anchor a note gets from the place the reader stored in its
/// <c>CfiRange</c> (issue #1381). A PDF location is JSON with the page; an
/// EPUB location is a CFI. Both were stored as kind <c>unknown</c> with no
/// value, so the card, the Markdown export and the footnotes had no locator.
/// Only an unanchored note is touched: an anchor someone or something else
/// set (an import, an audio timestamp, a passage) is never replaced.
/// </summary>
public static class LocationAnchor
{
    private const int MaxLength = 1024;

    /// <summary>The anchor a stored location stands for, or null when it names none.</summary>
    public static (string Kind, string Value)? FromLocation(string? location)
    {
        var text = location?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;

        if (text[0] == '{')
        {
            if (text.Length > 1024 * 1024)
                return null;
            try
            {
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("pageNumber", out var number)
                    && number.ValueKind == JsonValueKind.Number
                    && number.TryGetInt32(out var page)
                    && page >= 1)
                {
                    return ("pdf_page", page.ToString(CultureInfo.InvariantCulture));
                }
            }
            catch (JsonException)
            {
            }

            return null;
        }

        if (text.StartsWith("epubcfi(", StringComparison.Ordinal) && text.EndsWith(')') && text.Length <= MaxLength)
            return ("epub_cfi", RangeStart(text));

        return null;
    }

    /// <summary>
    /// Gives an unanchored note the anchor its location stands for. Returns
    /// true when the note changed.
    /// </summary>
    public static bool Apply(NoteModel note)
    {
        if (note.SourceAnchorKind != "unknown" || !string.IsNullOrWhiteSpace(note.SourceAnchorValue))
            return false;
        if (FromLocation(note.CfiRange) is not var (kind, value))
            return false;

        note.SourceAnchorKind = kind;
        note.SourceAnchorValue = value;
        // The reader reported the place from the open book.
        note.AnchorVerified = true;
        return true;
    }

    /// <summary>
    /// The one-time pass for notes saved before this rule. Provider-neutral
    /// EF code (the location is JSON, so SQL would not be portable); safe to
    /// run on every start, and writes only the notes it changes.
    /// </summary>
    public static async Task BackfillAsync(NostosDbContext db, CancellationToken ct = default)
    {
        var candidates = await db.Notes
            .Where(n => n.SourceAnchorKind == "unknown" && n.SourceAnchorValue == null && n.CfiRange != null)
            .ToListAsync(ct);

        var changed = false;
        foreach (var note in candidates)
            changed |= Apply(note);

        if (changed)
            await db.SaveChangesAsync(ct);
    }

    // epubcfi(parent,start,end) -> epubcfi(parent+start). A comma that belongs
    // to the path is escaped with ^.
    private static string RangeStart(string cfi)
    {
        var inner = cfi["epubcfi(".Length..^1];
        var commas = new List<int>();
        for (var i = 0; i < inner.Length; i++)
        {
            if (inner[i] == ',' && (i == 0 || inner[i - 1] != '^'))
                commas.Add(i);
        }

        if (commas.Count != 2)
            return cfi;

        return "epubcfi(" + inner[..commas[0]] + inner[(commas[0] + 1)..commas[1]] + ")";
    }
}

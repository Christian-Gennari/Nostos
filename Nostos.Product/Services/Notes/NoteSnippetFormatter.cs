using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Services.Notes;

/// <summary>
/// The fragment of a note a search row shows, so a row can say WHY it matched
/// without the whole note body travelling to the client (issue #1619, extracted
/// from <see cref="NoteService"/>). Pure text work: no database, no state.
/// </summary>
public sealed class NoteSnippetFormatter
{
    /// <summary>
    /// The fragment around the match. Prefers the quotation — it is the passage
    /// from the book either way — and centres on <paramref name="term"/> when
    /// there is one, rather than always showing the opening words.
    /// </summary>
    public string? Snippet(string content, string? quote, string? term)
    {
        var text = string.IsNullOrWhiteSpace(quote) ? content : quote!;
        if (string.IsNullOrWhiteSpace(text)) return null;

        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (flat.Length <= 160) return flat;

        var at = term is null ? -1 : flat.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return flat[..157] + "…";

        var start = Math.Max(0, at - 60);
        var end = Math.Min(flat.Length, start + 157);
        start = Math.Max(0, Math.Min(start, end - 40));
        return (start > 0 ? "…" : string.Empty) + flat[start..end] + (end < flat.Length ? "…" : string.Empty);
    }

    /// <summary>
    /// The all-words search records the whole query as its term, which rarely
    /// occurs verbatim when its words matched different fields. Centre the
    /// snippet on a word the note's own text really contains instead.
    /// </summary>
    public string? TermFor(NoteModel note, string? snippetTerm, IReadOnlyList<string> tokens)
    {
        bool InText(string term) =>
            note.Content.Contains(term, StringComparison.OrdinalIgnoreCase)
            || (note.SelectedText?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false);

        if (snippetTerm is { } term && InText(term)) return term;
        return tokens.FirstOrDefault(InText) ?? snippetTerm;
    }
}

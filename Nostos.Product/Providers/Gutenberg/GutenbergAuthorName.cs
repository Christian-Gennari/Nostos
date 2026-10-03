using System.Text.RegularExpressions;

namespace Nostos.Backend.Providers.Gutenberg;

/// <summary>
/// Turns Gutenberg's catalogue form of an author's name into the form the rest of
/// Nostos uses.
///
/// This lives in the provider, and deliberately NOT in
/// <c>BookIdentityNormalizer</c>. "Shelley, Mary Wollstonecraft, 1797-1851" is a
/// *cataloguing* convention rather than a fact about the author: the normalizer
/// is shared by every book already in the library, so teaching it to invert names
/// would silently re-group the user's existing works, and it would get
/// non-Western name orders, single-name authors and compound surnames wrong in
/// the process. A source adapter is exactly where a source's conventions belong.
/// </summary>
internal static partial class GutenbergAuthorName
{
    // Gutenberg appends life dates as their own comma-separated field:
    // "1797-1851", "1797-", "1797", sometimes "b. 1797" / "d. 1851".
    [GeneratedRegex(@"^\s*(?:[bdc]\.\s*)?\d{3,4}\s*(?:[-–]\s*(?:[bdc]\.\s*)?\d{0,4})?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex LifeDates();

    // Kept as a suffix rather than folded into the name: "Martin Luther King,
    // Jr." is how the author is actually written.
    [GeneratedRegex(@"^(?:jr|sr|ii|iii|iv|v|esq|ph\.?\s?d)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex NameSuffix();

    /// <summary>
    /// "Austen, Jane" becomes "Jane Austen"; "Shelley, Mary Wollstonecraft,
    /// 1797-1851" becomes "Mary Wollstonecraft Shelley"; "King, Martin Luther,
    /// Jr." becomes "Martin Luther King, Jr."; a name written without a comma is
    /// returned with any life dates stripped.
    ///
    /// Returns null when nothing usable is left, so the caller stores no author
    /// rather than an empty string.
    /// </summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var parts = raw.Split(
            ',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
            return null;

        if (parts.Length == 1)
        {
            // Already "First Last"; only the dates need removing.
            return Clean(LifeDates().Replace(parts[0], string.Empty));
        }

        var surname = parts[0];
        var given = parts[1];
        var suffix = parts.Skip(2).FirstOrDefault(part => NameSuffix().IsMatch(part));

        var name = Clean($"{given} {surname}");
        return name is null ? null : suffix is null ? name : $"{name}, {suffix}";
    }

    /// <summary>
    /// Normalizes separately-tagged author elements (Gutenberg emits one per
    /// author) into the single author string Nostos stores.
    ///
    /// Gutenberg's detail feeds list the same person more than once — the
    /// <c>dcterms:creator</c> entry and the OPDS entry both carry the author — so
    /// joining naively would store "Jane Austen, Jane Austen" as the author of
    /// Pride and Prejudice. Names are de-duplicated *after* normalization, which
    /// also collapses the same person written two different ways.
    /// </summary>
    public static string? NormalizeAll(IEnumerable<string?> names)
    {
        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in names)
        {
            var candidate = Normalize(name);
            if (string.IsNullOrWhiteSpace(candidate) || !seen.Add(candidate))
                continue;

            normalized.Add(candidate);
        }

        return normalized.Count == 0 ? null : string.Join(", ", normalized);
    }

    private static string? Clean(string value)
    {
        var collapsed = string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length == 0 ? null : collapsed;
    }
}

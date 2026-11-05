namespace Nostos.Backend.Services.Notes;

/// <summary>
/// PDF text layers often carry typographic ligature glyphs (U+FB00 to U+FB06,
/// "ﬁ" in "Speciﬁcally"). They look the same on screen but search, quoting and
/// export see a different character. Quotes are stored as plain letters.
/// </summary>
public static class TextLigatures
{
    // Longest first, so "ffi" is never read as "ff" followed by "i".
    private static readonly (string Letters, string Glyph)[] Pairs =
    [
        ("ffi", "ﬃ"),
        ("ffl", "ﬄ"),
        ("ff", "ﬀ"),
        ("fi", "ﬁ"),
        ("fl", "ﬂ"),
        ("st", "ﬆ"),
    ];

    /// <summary>Replace U+FB00 to U+FB06 with the letters they stand for.</summary>
    public static string? Expand(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.AsSpan().IndexOfAnyInRange('ﬀ', 'ﬆ') < 0)
            return text;

        return text
            .Replace("ﬀ", "ff")
            .Replace("ﬁ", "fi")
            .Replace("ﬂ", "fl")
            .Replace("ﬃ", "ffi")
            .Replace("ﬄ", "ffl")
            .Replace("ﬅ", "st")
            .Replace("ﬆ", "st");
    }

    /// <summary>
    /// The search term as a stored, not yet cleaned, quote might spell it, or
    /// null when the term has no letter pair a ligature could replace. Lets a
    /// search for "specifically" find a note saved before quotes were cleaned.
    /// </summary>
    public static string? LigatureForm(string term)
    {
        var result = term;
        foreach (var (letters, glyph) in Pairs)
            result = result.Replace(letters, glyph, StringComparison.OrdinalIgnoreCase);

        return string.Equals(result, term, StringComparison.Ordinal) ? null : result;
    }
}

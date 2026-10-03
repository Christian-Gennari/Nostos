using System.Text;

namespace Nostos.Backend.Services.Library;

/// <summary>
/// Pure, deterministic normalization for library book identity. No EF, no IO,
/// no state. Every rule here is mirrored in the EF migration backfill for
/// existing rows (see the AddLibraryCommandSurface migration).
/// </summary>
public static class BookIdentityNormalizer
{
    /// <summary>
    /// Normalizes an ISBN: strips spaces/hyphens, uppercases a terminal X and
    /// validates the checksum. Returns the canonical digits(+X) form, or null
    /// when the value is absent or fails validation.
    /// </summary>
    public static string? NormalizeIsbn(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch is ' ' or '-')
                continue;
            sb.Append(char.ToUpperInvariant(ch));
        }

        var cleaned = sb.ToString();
        return IsValidIsbn(cleaned) ? cleaned : null;
    }

    /// <summary>
    /// Normalizes an ASIN: trimmed, uppercased, exactly 10 characters from
    /// A-Z0-9. Returns null when absent or invalid.
    /// </summary>
    public static string? NormalizeAsin(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var cleaned = value.Trim().ToUpperInvariant();
        if (cleaned.Length != 10)
            return null;

        foreach (var ch in cleaned)
        {
            if (!(char.IsAsciiLetterUpper(ch) || char.IsAsciiDigit(ch)))
                return null;
        }

        return cleaned;
    }

    /// <summary>
    /// Unicode-normalized, trimmed, whitespace-collapsed, case-folded title.
    /// Punctuation runs normalize to single spaces (frozen contract), so
    /// "Title : Subtitle" and "Title: Subtitle" share one identity; subtitle
    /// TEXT is preserved. Subtitles are intentionally preserved.
    /// </summary>
    public static string NormalizeTitle(string? value) => NormalizeText(value, collapsePunctuation: true);

    /// <summary>
    /// Unicode-normalized, trimmed, whitespace-collapsed, case-folded author
    /// name. Accents, name order, and PUNCTUATION are intentionally preserved
    /// (the punctuation-spacing rule applies to titles only).
    /// </summary>
    public static string NormalizeAuthor(string? value) => NormalizeText(value, collapsePunctuation: false);

    private static string NormalizeText(string? value, bool collapsePunctuation)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(normalized.Length);
        var inWhitespace = false;
        foreach (var ch in normalized)
        {
            if (char.IsWhiteSpace(ch) || (collapsePunctuation && char.IsPunctuation(ch)))
            {
                inWhitespace = true;
                continue;
            }

            if (inWhitespace && sb.Length > 0)
                sb.Append(' ');
            inWhitespace = false;
            sb.Append(char.ToUpperInvariant(ch));
        }

        return sb.ToString();
    }

    private static bool IsValidIsbn(string cleaned)
    {
        switch (cleaned.Length)
        {
            case 10:
                return IsValidIsbn10(cleaned);
            case 13:
                return IsValidIsbn13(cleaned);
            default:
                return false;
        }
    }

    private static bool IsValidIsbn10(string isbn)
    {
        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            if (!char.IsAsciiDigit(isbn[i]))
                return false;
            sum += (10 - i) * (isbn[i] - '0');
        }

        var check = isbn[9] switch
        {
            'X' => 10,
            >= '0' and <= '9' => isbn[9] - '0',
            _ => -1,
        };
        if (check < 0)
            return false;

        return (sum + check) % 11 == 0;
    }

    private static bool IsValidIsbn13(string isbn)
    {
        var sum = 0;
        for (var i = 0; i < 13; i++)
        {
            if (!char.IsAsciiDigit(isbn[i]))
                return false;
            var digit = isbn[i] - '0';
            sum += (i % 2 == 0) ? digit : digit * 3;
        }

        return sum % 10 == 0;
    }
}

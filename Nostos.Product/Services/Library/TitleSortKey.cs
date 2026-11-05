using System.Globalization;
using System.Text;

namespace Nostos.Backend.Services.Library;

/// <summary>
/// The key books are ordered by when sorted by title: case-insensitive, accents
/// folded to their base letter, and Å, Ä, Ö (and the Danish/Norwegian Æ, Ø)
/// placed after Z, as in Swedish.
///
/// It is computed here, not by a database collation, so SQLite (self-hosted)
/// and PostgreSQL (Cloud) give the same order and neither needs ICU: compare
/// keys with <see cref="StringComparer.Ordinal"/>.
/// </summary>
internal static class TitleSortKey
{
    public static readonly IComparer<string?> Comparer =
        Comparer<string?>.Create((a, b) => string.CompareOrdinal(Of(a), Of(b)));

    public static string Of(string? title)
    {
        if (string.IsNullOrEmpty(title)) return string.Empty;

        var lowered = title.Trim().ToLowerInvariant();
        var builder = new StringBuilder(lowered.Length);
        foreach (var rune in lowered.EnumerateRunes())
        {
            // The ASCII characters right after "z" keep the three letters
            // after Z, in Swedish order, and above every other letter.
            switch (rune.Value)
            {
                case 'å': builder.Append('{'); continue;
                case 'ä' or 'æ': builder.Append('|'); continue;
                case 'ö' or 'ø': builder.Append('}'); continue;
            }

            foreach (var ch in rune.ToString().Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                    builder.Append(ch);
            }
        }
        return builder.ToString();
    }
}

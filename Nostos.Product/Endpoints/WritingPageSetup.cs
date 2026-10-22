using System.Text.Json;
using System.Text.RegularExpressions;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Endpoints;

/// <summary>
/// Reads, checks and writes a writing's export page setup (issue #1164). It is
/// stored as JSON in <c>Writings.PageSetup</c>; the shape lives here so the
/// endpoint and anything else that reads it agree.
/// </summary>
public static partial class WritingPageSetup
{
    public const double MinMarginMm = 5;
    public const double MaxMarginMm = 80;
    public const double MinLineSpacing = 0.8;
    public const double MaxLineSpacing = 3;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex IdentifierPattern();

    /// <summary>Why the setup cannot be saved, or null when it can.</summary>
    public static string? Validate(WritingPageSetupDto? dto)
    {
        if (dto is null)
            return "A page setup is required.";
        if (dto.Paper is not ("a4" or "letter"))
            return "Paper must be 'a4' or 'letter'.";
        if (!double.IsFinite(dto.MarginMm) || dto.MarginMm < MinMarginMm || dto.MarginMm > MaxMarginMm)
            return $"Margins must be between {MinMarginMm} and {MaxMarginMm} mm.";
        if (!double.IsFinite(dto.LineSpacing) || dto.LineSpacing < MinLineSpacing || dto.LineSpacing > MaxLineSpacing)
            return $"Line spacing must be between {MinLineSpacing} and {MaxLineSpacing}.";
        if (dto.Font is null || !IdentifierPattern().IsMatch(dto.Font))
            return "Font must be a font identifier.";
        if (dto.CitationStyle is null || !IdentifierPattern().IsMatch(dto.CitationStyle))
            return "Citation style must be a style identifier.";
        return null;
    }

    /// <summary>Rounded so a saved setup reads back exactly as written.</summary>
    public static WritingPageSetupDto Normalize(WritingPageSetupDto dto) =>
        dto with
        {
            MarginMm = Math.Round(dto.MarginMm, 1),
            LineSpacing = Math.Round(dto.LineSpacing, 2),
        };

    public static string Write(WritingPageSetupDto dto) => JsonSerializer.Serialize(dto, Json);

    /// <summary>The stored setup, or null when none is saved or it cannot be read.</summary>
    public static WritingPageSetupDto? Read(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return null;
        try
        {
            var dto = JsonSerializer.Deserialize<WritingPageSetupDto>(stored, Json);
            return Validate(dto) is null ? dto : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

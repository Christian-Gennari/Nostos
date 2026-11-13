namespace Nostos.Backend.Data;

/// <summary>
/// Escapes SQL <c>LIKE</c> wildcard characters (<c>%</c>, <c>_</c>) and the
/// escape character (<c>\</c>) so literal search terms match only themselves
/// when paired with <c>EF.Functions.Like(..., pattern, SqlLikePatternHelper.EscapeChar)</c>.
/// </summary>
internal static class SqlLikePatternHelper
{
    public const string EscapeChar = "\\";

    public static string Escape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");
    }

    public static string Contains(string value) => $"%{Escape(value)}%";
}

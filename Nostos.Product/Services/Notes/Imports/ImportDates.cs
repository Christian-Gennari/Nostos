using System.Globalization;

namespace Nostos.Backend.Services.Notes.Imports;

/// <summary>
/// Reads the date an e-reader or export file recorded for a highlight (issue
/// #1367), so the imported note keeps it as its <c>CreatedAt</c>. Every parse
/// is culture-invariant. None of the sources writes a time zone reliably (a
/// Kindle and KOReader write the device's local clock), so a time without an
/// offset is taken as UTC; one with an offset is converted to UTC. A value that
/// does not parse, or lies in the future, gives null and the import falls back
/// to the time of import.
/// </summary>
internal static class ImportDates
{
    private const DateTimeStyles Styles =
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal | DateTimeStyles.AllowWhiteSpaces;

    // A Kindle writes the clock in the device's UI language; English is the
    // only wording read here, in the US and UK orders.
    private static readonly string[] KindleFormats =
    [
        "dddd, MMMM d, yyyy h:mm:ss tt",
        "dddd, MMMM d, yyyy, hh:mm tt", // older firmware: "Friday, March 4, 2011, 07:30 PM"
        "dddd, MMMM d, yyyy",
        "dddd, d MMMM yyyy H:mm:ss",
        "dddd, d MMMM yyyy h:mm:ss tt",
        "dddd, d MMMM yyyy",
    ];

    /// <summary>ISO 8601, with a space or a T, and an optional fraction and offset.</summary>
    public static DateTime? Iso(string? value) =>
        value is not null
        && DateTime.TryParse(value.Trim(), CultureInfo.InvariantCulture, Styles, out var parsed)
            ? Accept(parsed)
            : null;

    /// <summary>"Monday, January 1, 2024 10:00:00 AM", as after "Added on".</summary>
    public static DateTime? Kindle(string? value) =>
        value is not null
        && DateTime.TryParseExact(value.Trim(), KindleFormats, CultureInfo.InvariantCulture, Styles, out var parsed)
            ? Accept(parsed)
            : null;

    private static DateTime? Accept(DateTime utc) =>
        utc.Year >= 1970 && utc <= DateTime.UtcNow.AddDays(1)
            ? DateTime.SpecifyKind(utc, DateTimeKind.Utc)
            : null;
}

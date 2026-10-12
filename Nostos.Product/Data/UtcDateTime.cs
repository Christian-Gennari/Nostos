namespace Nostos.Backend.Data;

/// <summary>
/// The one rule for reading a <see cref="DateTime"/> as UTC, shared by the
/// database converters and the JSON converter so a stored and a serialised
/// timestamp cannot disagree.
/// </summary>
internal static class UtcDateTime
{
    /// <summary>
    /// A local time is converted; a time with no kind is taken to already be
    /// UTC, because that is how every timestamp is written.
    /// </summary>
    public static DateTime Normalize(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
}

using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Nostos.Backend.Data;

public sealed class UtcDateTimeValueConverter()
    : ValueConverter<DateTime, DateTime>(
        value => NormalizeUtc(value),
        value => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    )
{
    private static DateTime NormalizeUtc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
}

public sealed class NullableUtcDateTimeValueConverter()
    : ValueConverter<DateTime?, DateTime?>(
        value => value.HasValue ? NormalizeUtc(value.Value) : null,
        value => value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : null
    )
{
    private static DateTime NormalizeUtc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
}

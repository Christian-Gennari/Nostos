using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Nostos.Backend.Data;

public sealed class UtcDateTimeValueConverter()
    : ValueConverter<DateTime, DateTime>(
        value => UtcDateTime.Normalize(value),
        value => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    );

public sealed class NullableUtcDateTimeValueConverter()
    : ValueConverter<DateTime?, DateTime?>(
        value => value.HasValue ? UtcDateTime.Normalize(value.Value) : null,
        value => value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : null
    );

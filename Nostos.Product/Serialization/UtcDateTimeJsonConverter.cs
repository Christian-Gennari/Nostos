using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nostos.Backend.Serialization;

public sealed class UtcDateTimeJsonConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"Expected string token when parsing {nameof(DateTime)}.");
        }

        var value = reader.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new JsonException($"Cannot parse an empty {nameof(DateTime)} value.");
        }

        return NormalizeUtc(DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(NormalizeUtc(value).ToString("O", CultureInfo.InvariantCulture));
    }

    private static DateTime NormalizeUtc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
}

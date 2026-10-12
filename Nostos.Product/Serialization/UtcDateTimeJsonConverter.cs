using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nostos.Backend.Data;

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

        return UtcDateTime.Normalize(DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(UtcDateTime.Normalize(value).ToString("O", CultureInfo.InvariantCulture));
    }
}

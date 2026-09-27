using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nostos.Backend.Integrations.Assistant;

/// <summary>
/// The canonical identity of one logical assistant mutation, and the shared
/// canonical JSON writer that this identity and
/// <see cref="AssistantToolLoopDetector"/> both use.
///
/// The identity is the logical operation, never its position in the model's
/// tool sequence. A transport retry is free to reorder, add, or drop calls, so
/// a key derived from call position can miss an earlier write (a duplicate) or
/// land on a different command's receipt (a wrong replay). One logical
/// operation — same TurnId, same capability, same canonical effective
/// arguments — reuses one identity, so re-emitting it replays instead of
/// writing again; genuinely different mutations stay distinct.
/// </summary>
public static class AssistantMutationIdentity
{
    /// <summary>
    /// The canonical form of the model's raw tool arguments: object property
    /// names sorted ordinal, arrays kept in order, numbers as their raw text,
    /// null/undefined written as null. Null or blank input canonicalizes to
    /// <c>{}</c>; unparseable input falls back to its trimmed text so two
    /// byte-identical malformed strings still compare equal.
    /// </summary>
    public static string CanonicalizeArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "{}";
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                WriteCanonical(writer, document.RootElement);
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return json.Trim();
        }
    }

    /// <summary>
    /// The canonical form of an already-parsed argument element, using the
    /// same rules as <see cref="CanonicalizeArguments(string?)"/>.
    /// </summary>
    public static string CanonicalizeArguments(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(writer, element);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// The receipt key for one logical mutation: TurnId + capability + the
    /// canonical effective arguments, reduced to a fixed 64-character
    /// lowercase hex SHA-256 digest.
    ///
    /// The digest is required because the canonical services cap a receipt key
    /// at 128 characters and a TurnId is client-supplied and unbounded, so a
    /// raw composite could be refused. It is fixed-length by construction — no
    /// length-dependent branches — and, being hex, can never collide with the
    /// retired ":"-separated format.
    ///
    /// "Effective" means the arguments the capability actually receives. For
    /// <c>notes_capture</c> that is the server-prepared capture arguments, so
    /// model-chosen book or anchor noise cannot change the identity.
    /// </summary>
    public static string Key(string turnId, string capability, JsonElement effectiveArguments)
    {
        var canonicalJson = CanonicalizeArguments(effectiveArguments);
        var material = $"{turnId}\n{capability}\n{canonicalJson}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                             .OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }
                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;

            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText());
                break;

            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;

            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;

            default:
                writer.WriteRawValue(element.GetRawText());
                break;
        }
    }
}

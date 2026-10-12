using System.Text.Json;

namespace Nostos.Backend.Services.Ai;

/// <summary>
/// Reads the human-readable part of a failed OpenAI-compatible response, for
/// the providers that only need it to make an error or a log line useful.
/// </summary>
internal static class ProviderErrorDetail
{
    private const int MaxBodyLength = 300;

    /// <summary>
    /// Best-effort extraction of the gateway's <c>{"error":{"message":…}}</c>
    /// body. A body we cannot read is not itself an error; the status code is
    /// the signal, and the detail is only there to make a log line useful.
    /// </summary>
    public static async Task<string?> ReadAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                return message.GetString();
            }

            return body.Length <= MaxBodyLength ? body : body[..MaxBodyLength];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

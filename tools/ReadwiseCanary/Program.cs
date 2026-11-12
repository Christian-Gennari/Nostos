// The Readwise export-format canary (issue #1525).
//
// Calls the official export API once, first page only, with the repository's
// READWISE_CANARY_TOKEN, and runs the response through the same schema check
// the mapper uses. It fails when a field we read disappears or changes type.
//
// It never prints response content: on failure it writes only the JSON path and
// the expected/actual types. That is the whole early-warning contract: a change
// in Readwise's export shows up as a red weekly job, not as a silent bad import.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Nostos.Backend.Services.Notes.Imports;

const string ExportEndpoint = "https://readwise.io/api/v2/export/";
const int MaxAttempts = 4;

var token = Environment.GetEnvironmentVariable("READWISE_CANARY_TOKEN");
if (string.IsNullOrWhiteSpace(token))
{
    Console.Error.WriteLine("READWISE_CANARY_TOKEN is not set. Add the repository secret before running the canary.");
    return 2;
}

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Token " + token);
http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

for (var attempt = 0; attempt < MaxAttempts; attempt++)
{
    using var request = new HttpRequestMessage(HttpMethod.Get, ExportEndpoint);
    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

    if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < MaxAttempts - 1)
    {
        var delay = RetryAfter(response) ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
        Console.WriteLine(
            "Readwise is rate limiting; waiting "
            + delay.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)
            + "s before retrying.");
        await Task.Delay(delay);
        continue;
    }

    if (response.StatusCode == HttpStatusCode.Unauthorized)
    {
        Console.Error.WriteLine("Readwise rejected READWISE_CANARY_TOKEN (401).");
        return 1;
    }

    if (!response.IsSuccessStatusCode)
    {
        Console.Error.WriteLine($"Readwise answered {(int)response.StatusCode} for the export.");
        return 1;
    }

    JsonDocument document;
    try
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        document = await JsonDocument.ParseAsync(stream);
    }
    catch (JsonException)
    {
        Console.Error.WriteLine("SCHEMA: $ expected JSON, found unreadable body.");
        return 1;
    }

    using (document)
    {
        var issues = ReadwiseExportSchema.Check(document.RootElement);
        if (issues.Count > 0)
        {
            foreach (var issue in issues)
            {
                // Paths and types only; never a title, an author or a highlight.
                Console.Error.WriteLine(
                    $"SCHEMA: {issue.Path} expected {issue.Expected}, found {issue.Actual}");
            }

            return 1;
        }
    }

    Console.WriteLine("Readwise export schema OK (first page).");
    return 0;
}

Console.Error.WriteLine("Readwise did not answer after the rate-limit retries.");
return 1;

static TimeSpan? RetryAfter(HttpResponseMessage response)
{
    var header = response.Headers.RetryAfter;
    if (header is null)
        return null;

    var delay = header.Delta
        ?? (header.Date is { } date ? date - DateTimeOffset.UtcNow : null);
    return delay is null
        ? null
        : TimeSpan.FromSeconds(Math.Clamp(delay.Value.TotalSeconds, 0, 60));
}

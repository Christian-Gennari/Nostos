using Nostos.Backend.Providers.Contracts;

namespace Nostos.Backend.Providers;

/// <summary>
/// Shared HTTP request and stream-read wrapper for content providers. Maps
/// connectivity, stream I/O, and client-side timeout failures to
/// <see cref="ProviderException"/> (<see cref="ProviderException.Unavailable"/>)
/// while letting caller cancellation propagate as
/// <see cref="OperationCanceledException"/>.
/// </summary>
internal static class ProviderHttp
{
    public static Task<HttpResponseMessage> GetHeadersAsync(
        HttpClient client,
        string providerId,
        string requestUri,
        CancellationToken ct) =>
        ExecuteAsync(
            providerId,
            ct,
            token => client.GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, token));

    public static Task<HttpResponseMessage> SendHeadersAsync(
        HttpClient client,
        string providerId,
        HttpRequestMessage request,
        CancellationToken ct) =>
        ExecuteAsync(
            providerId,
            ct,
            token => client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token));

    public static async Task<T> ExecuteAsync<T>(
        string providerId,
        CancellationToken ct,
        Func<CancellationToken, Task<T>> action)
    {
        try
        {
            return await action(ct);
        }
        catch (HttpRequestException ex)
        {
            throw ProviderException.UnavailableFor(providerId, ex.Message);
        }
        catch (IOException ex)
        {
            throw ProviderException.UnavailableFor(providerId, ex.Message);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // HttpClient.Timeout surfaces as an OperationCanceledException (typically
            // TaskCanceledException) unrelated to the caller's token.
            throw ProviderException.UnavailableFor(providerId, ex.Message);
        }
    }
}

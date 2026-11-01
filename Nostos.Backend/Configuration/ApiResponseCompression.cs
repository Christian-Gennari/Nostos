using System.IO.Compression;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Nostos.Backend.Configuration;

/// <summary>
/// Compresses API JSON for a SelfHosted server, which has nothing in front of
/// it that would. The private Cloud host composes its own pipeline and is not
/// touched: Cloudflare compresses there, and this would only add origin CPU.
/// </summary>
public static class ApiResponseCompression
{
    public static IServiceCollection AddApiResponseCompression(this IServiceCollection services)
    {
        services.AddSingleton<IResponseCompressionProvider, ApiJsonCompressionProvider>();
        services.Configure<GzipCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);
        return services.AddResponseCompression(options =>
        {
            // An exact list, with no text/*: the library change feed and the
            // import streams are text/event-stream, the assistant stream is
            // application/x-ndjson, and each must reach the client line by line.
            options.MimeTypes = ["application/json", "application/problem+json"];
            // Gzip alone: at the fastest level brotli took the same time and
            // left /api/books 30% larger (24.7 kB against 19.0 kB).
            options.Providers.Add<GzipCompressionProvider>();
            // Behind a TLS proxy the forwarded scheme is https. SelfHosted has
            // no accounts or session cookies, so there is no secret for a
            // compression side channel (BREACH) to recover.
            options.EnableForHttps = true;
        });
    }
}

/// <summary>
/// Narrows the default provider to <c>/api</c> and keeps it away from anything
/// addressed by byte range, whatever its content type.
/// </summary>
public sealed class ApiJsonCompressionProvider(
    IServiceProvider services,
    IOptions<ResponseCompressionOptions> options) : IResponseCompressionProvider
{
    private readonly ResponseCompressionProvider _inner = new(services, options);

    public bool CheckRequestAcceptsCompression(HttpContext context) =>
        context.Request.Path.StartsWithSegments("/api")
        && !context.Request.Headers.ContainsKey(HeaderNames.Range)
        && _inner.CheckRequestAcceptsCompression(context);

    public ICompressionProvider? GetCompressionProvider(HttpContext context) =>
        _inner.GetCompressionProvider(context);

    public bool ShouldCompressResponse(HttpContext context) =>
        !context.Response.Headers.ContainsKey(HeaderNames.AcceptRanges)
        && _inner.ShouldCompressResponse(context);
}

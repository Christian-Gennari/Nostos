using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Nostos.Product.Http;

namespace Nostos.Backend.Middleware;

/// <summary>
/// Picks the precompressed sibling of a frontend file. <c>dotnet publish</c>
/// writes a <c>.br</c> and a <c>.gz</c> beside each file in <c>wwwroot</c>, so
/// serving one costs no CPU at request time.
/// </summary>
public static class PrecompressedFrontendFiles
{
    // Brotli first: on the main bundle it is 14% smaller than gzip. Browsers
    // only offer it over HTTPS and on localhost, so a plain-HTTP LAN gets gzip.
    private static readonly (string Encoding, string Suffix)[] Encodings =
    [
        ("br", ".br"),
        ("gzip", ".gz"),
    ];

    /// <summary>
    /// The sibling to send for <paramref name="path"/>, or false to send the
    /// file itself. Marks the response <c>Vary: Accept-Encoding</c> whenever a
    /// sibling exists, whichever is sent, so a shared cache in front of the
    /// server cannot hand one client's encoding to another.
    /// </summary>
    public static bool TrySelect(
        HttpContext context,
        IFileProvider files,
        string path,
        [NotNullWhen(true)] out IFileInfo? sibling,
        [NotNullWhen(true)] out string? encoding)
    {
        sibling = null;
        encoding = null;

        // The asset is already compressed: the OCR language data is a real
        // .gz file that its client decompresses itself.
        if (Encodings.Any(known => path.EndsWith(known.Suffix, StringComparison.OrdinalIgnoreCase)))
            return false;

        var original = files.GetFileInfo(path);
        if (!original.Exists || original.IsDirectory)
            return false;

        var candidates = Encodings
            .Select(known => (known.Encoding, File: files.GetFileInfo(path + known.Suffix)))
            // An older sibling was compressed from other bytes. Serving it
            // would fail the service worker's hash check of this file.
            .Where(candidate => candidate.File.Exists && candidate.File.LastModified >= original.LastModified)
            .ToList();
        if (candidates.Count == 0)
            return false;

        context.Response.Headers.Append(HeaderNames.Vary, HeaderNames.AcceptEncoding);

        // A byte range addresses the file itself. Offsets into a compressed
        // sibling would mean nothing to the client.
        if (context.Request.Headers.ContainsKey(HeaderNames.Range))
            return false;

        var accepted = context.Request.GetTypedHeaders().AcceptEncoding;
        foreach (var candidate in candidates)
        {
            if (accepted.Any(value =>
                    value.Quality is not <= 0
                    && string.Equals(value.Value.Value, candidate.Encoding, StringComparison.OrdinalIgnoreCase)))
            {
                sibling = candidate.File;
                encoding = candidate.Encoding;
                return true;
            }
        }

        return false;
    }

    internal static string SuffixFor(string encoding) =>
        Encodings.Single(known => known.Encoding == encoding).Suffix;

    internal static string WithoutSuffix(string path, string encoding) =>
        path[..^SuffixFor(encoding).Length];
}

/// <summary>
/// Runs before the ordinary static-file layer and answers from a precompressed
/// sibling when the client accepts one. Everything the static-file layer does
/// for the original (ETag, conditional requests, HEAD) it does here for the
/// sibling, under the original's content type and cache policy. The sibling has
/// its own length and date, so its ETag differs from the original's.
/// SelfHosted only: the Cloud image copies the frontend in after publishing, so
/// it has no siblings, and Cloudflare compresses at the edge.
/// </summary>
public sealed class PrecompressedFrontendFilesMiddleware
{
    private const string EncodingItem = "Nostos.PrecompressedEncoding";

    private readonly RequestDelegate _next;
    private readonly IFileProvider _files;
    private readonly StaticFileMiddleware _siblings;

    public PrecompressedFrontendFilesMiddleware(
        RequestDelegate next,
        IWebHostEnvironment environment,
        ILoggerFactory loggers)
    {
        _next = next;
        _files = environment.WebRootFileProvider;
        var contentTypes = NostosFrontendStaticFiles.CreateOptions().ContentTypeProvider!;
        _siblings = new StaticFileMiddleware(
            NextWithOriginalPath,
            environment,
            Options.Create(new StaticFileOptions
            {
                ContentTypeProvider = new OriginalContentTypes(contentTypes),
                OnPrepareResponse = PrepareResponse,
            }),
            loggers);
    }

    public Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;

        // A matched endpoint is an API or client route, not a frontend file.
        if (context.GetEndpoint() is not null
            || !(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
            || !Path.HasExtension(request.Path.Value))
        {
            return _next(context);
        }

        if (!PrecompressedFrontendFiles.TrySelect(context, _files, request.Path.Value!, out _, out var encoding))
            return _next(context);

        context.Items[EncodingItem] = encoding;
        request.Path += PrecompressedFrontendFiles.SuffixFor(encoding);
        return _siblings.Invoke(context);
    }

    private Task NextWithOriginalPath(HttpContext context)
    {
        var encoding = (string)context.Items[EncodingItem]!;
        context.Request.Path = PrecompressedFrontendFiles.WithoutSuffix(context.Request.Path.Value!, encoding);
        return _next(context);
    }

    private static void PrepareResponse(StaticFileResponseContext context)
    {
        var encoding = (string)context.Context.Items[EncodingItem]!;
        var response = context.Context.Response;
        response.Headers.ContentEncoding = encoding;
        if (NostosFrontendStaticFiles.CacheControlFor(
                PrecompressedFrontendFiles.WithoutSuffix(context.File.Name, encoding)) is { } cacheControl)
        {
            response.Headers.CacheControl = cacheControl;
        }
    }

    /// <summary>
    /// Only ever sees the sibling paths this middleware rewrote to, so the last
    /// extension is always the encoding and the one before it the real type.
    /// </summary>
    private sealed class OriginalContentTypes(IContentTypeProvider inner) : IContentTypeProvider
    {
        public bool TryGetContentType(string subpath, [MaybeNullWhen(false)] out string contentType) =>
            inner.TryGetContentType(Path.ChangeExtension(subpath, null), out contentType);
    }
}

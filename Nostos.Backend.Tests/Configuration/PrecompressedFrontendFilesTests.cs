using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Nostos.Backend.Middleware;
using Nostos.Product.Http;
using Xunit;

namespace Nostos.Backend.Tests.Configuration;

/// <summary>
/// A published SelfHosted image has a .br and a .gz beside each frontend file.
/// These drive the middleware over HTTP against real files, in the order the
/// host runs it, and check everything a client or cache depends on.
/// </summary>
public sealed class PrecompressedFrontendFilesTests : IAsyncLifetime, IDisposable
{
    private const string Bundle = "main-UG2TJAJM.js";
    private static readonly byte[] BundleBytes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("console.log('nostos');\n", 200)));

    private readonly string _root = Directory.CreateTempSubdirectory("nostos-precompressed-").FullName;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await WritePublishedAsync(Bundle, BundleBytes);
        await WritePublishedAsync("ngsw.json", Encoding.UTF8.GetBytes("{\"hashTable\":{}}"));

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { WebRootPath = _root });
        builder.WebHost.UseTestServer();
        _app = builder.Build();
        _app.UseMiddleware<PrecompressedFrontendFilesMiddleware>();
        _app.UseStaticFiles(NostosFrontendStaticFiles.CreateOptions());
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("br, gzip", "br")]
    [InlineData("gzip, deflate", "gzip")]
    [InlineData("br;q=0, gzip", "gzip")]
    [InlineData("gzip;q=0.5, br;q=0.1", "br")]
    public async Task Sends_the_sibling_the_client_accepts(string acceptEncoding, string expected)
    {
        using var response = await SendAsync(Bundle, acceptEncoding);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentEncoding.Should().Equal(expected);
        var compressed = await response.Content.ReadAsByteArrayAsync();
        response.Content.Headers.ContentLength.Should().Be(compressed.Length);
        compressed.Length.Should().BeLessThan(BundleBytes.Length);
    }

    [Theory]
    [InlineData("br")]
    [InlineData("gzip")]
    public async Task Keeps_the_type_cache_policy_and_vary_of_the_original(string encoding)
    {
        using var response = await SendAsync(Bundle, encoding);

        response.Content.Headers.ContentType!.MediaType.Should().Be("text/javascript");
        response.Headers.CacheControl!.ToString().Should().Contain("immutable").And.Contain("max-age=31536000");
        response.Headers.Vary.Should().Contain("Accept-Encoding");
    }

    [Fact]
    public async Task An_entry_file_stays_revalidated_when_compressed()
    {
        using var response = await SendAsync("ngsw.json", "br");

        response.Content.Headers.ContentEncoding.Should().Equal("br");
        response.Headers.CacheControl!.NoCache.Should().BeTrue();
    }

    [Fact]
    public async Task Each_encoding_has_its_own_etag_and_revalidates_against_it()
    {
        using var br = await SendAsync(Bundle, "br");
        using var gzip = await SendAsync(Bundle, "gzip");
        using var identity = await SendAsync(Bundle, acceptEncoding: null);

        new[] { br.Headers.ETag!.Tag, gzip.Headers.ETag!.Tag, identity.Headers.ETag!.Tag }
            .Should().OnlyHaveUniqueItems();

        using var revalidate = new HttpRequestMessage(HttpMethod.Get, "/" + Bundle);
        revalidate.Headers.AcceptEncoding.ParseAdd("br");
        revalidate.Headers.IfNoneMatch.Add(br.Headers.ETag!);
        using var notModified = await _client.SendAsync(revalidate);

        notModified.StatusCode.Should().Be(HttpStatusCode.NotModified);
        notModified.Headers.Vary.Should().Contain("Accept-Encoding");
    }

    [Theory]
    [InlineData("br")]
    [InlineData("gzip")]
    public async Task The_decoded_bytes_are_what_the_service_worker_hashes(string encoding)
    {
        using var response = await SendAsync(Bundle, encoding);

        var decoded = Decode(await response.Content.ReadAsByteArrayAsync(), encoding);

        SHA1.HashData(decoded).Should().Equal(SHA1.HashData(BundleBytes));
    }

    [Fact]
    public async Task A_client_that_accepts_neither_gets_the_file_itself_with_vary()
    {
        using var response = await SendAsync(Bundle, "identity");

        response.Content.Headers.ContentEncoding.Should().BeEmpty();
        response.Headers.Vary.Should().Contain("Accept-Encoding");
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(BundleBytes);
    }

    [Fact]
    public async Task A_range_request_is_answered_from_the_file_itself()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/" + Bundle);
        request.Headers.AcceptEncoding.ParseAdd("br");
        request.Headers.Range = new RangeHeaderValue(10, 19);

        using var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        response.Content.Headers.ContentEncoding.Should().BeEmpty();
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(BundleBytes[10..20]);
    }

    [Fact]
    public async Task A_head_request_describes_the_sibling_without_a_body()
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, "/" + Bundle);
        request.Headers.AcceptEncoding.ParseAdd("gzip");

        using var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentEncoding.Should().Equal("gzip");
    }

    [Fact]
    public async Task A_file_that_is_itself_gzip_is_not_negotiated()
    {
        var path = Path.Combine(_root, "eng.traineddata.gz");
        await File.WriteAllBytesAsync(path, Encode(BundleBytes, "gzip"));

        using var response = await SendAsync("eng.traineddata.gz", "gzip, br");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentEncoding.Should().BeEmpty();
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/x-gzip");
    }

    [Fact]
    public async Task A_sibling_older_than_its_file_is_ignored()
    {
        const string name = "chunk-J553T4TW.js";
        var newer = Encoding.UTF8.GetBytes("console.log('rebuilt');");
        await File.WriteAllBytesAsync(Path.Combine(_root, name), newer);
        var stale = Path.Combine(_root, name + ".br");
        await File.WriteAllBytesAsync(stale, Encode(BundleBytes, "br"));
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-1));

        using var response = await SendAsync(name, "br");

        response.Content.Headers.ContentEncoding.Should().BeEmpty();
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(newer);
    }

    [Fact]
    public async Task A_file_without_siblings_is_untouched()
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, "pwa-192.png"), BundleBytes);

        using var response = await SendAsync("pwa-192.png", "br, gzip");

        response.Content.Headers.ContentEncoding.Should().BeEmpty();
        response.Headers.Vary.Should().BeEmpty();
    }

    private async Task<HttpResponseMessage> SendAsync(string file, string? acceptEncoding)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/" + file);
        if (acceptEncoding is not null)
            request.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);
        return await _client.SendAsync(request);
    }

    private async Task WritePublishedAsync(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        await File.WriteAllBytesAsync(path, bytes);
        await File.WriteAllBytesAsync(path + ".br", Encode(bytes, "br"));
        await File.WriteAllBytesAsync(path + ".gz", Encode(bytes, "gzip"));
    }

    private static byte[] Encode(byte[] bytes, string encoding)
    {
        using var output = new MemoryStream();
        using (Stream compressor = encoding == "br"
            ? new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true)
            : new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            compressor.Write(bytes);
        }

        return output.ToArray();
    }

    private static byte[] Decode(byte[] bytes, string encoding)
    {
        using var input = new MemoryStream(bytes);
        using Stream decompressor = encoding == "br"
            ? new BrotliStream(input, CompressionMode.Decompress)
            : new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        decompressor.CopyTo(output);
        return output.ToArray();
    }
}

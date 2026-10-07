using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Providers;
using Nostos.Backend.Providers.Contracts;
using Nostos.Backend.Providers.Gutenberg;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Providers;

/// <summary>
/// Contract tests for the Gutenberg bulk snapshot source (#776 P0-B). Gutenberg
/// publishes the whole catalogue daily as a zipped tar of RDF files; tests build
/// a tiny archive in memory so no binary fixture is committed.
/// </summary>
public sealed class GutenbergSnapshotSourceTests
{
    private const string SnapshotPath = "/cache/epub/feeds/rdf-files.tar.zip";
    private const string TempFilePattern = "nostos-gutenberg-*.zip";
    private static readonly DateTimeOffset LastModified = new(2026, 10, 6, 14, 41, 27, TimeSpan.Zero);

    private static readonly string PrideRdf = """
        <?xml version="1.0" encoding="utf-8"?>
        <rdf:RDF xml:base="http://www.gutenberg.org/"
          xmlns:dcterms="http://purl.org/dc/terms/"
          xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"
          xmlns:pgterms="http://www.gutenberg.org/2009/pgterms/"
          xmlns:dcam="http://purl.org/dc/dcam/">
          <pgterms:ebook rdf:about="ebooks/1342">
            <dcterms:title>Pride and Prejudice</dcterms:title>
            <dcterms:creator>
              <pgterms:agent rdf:about="2009/agents/68">
                <pgterms:name>Austen, Jane</pgterms:name>
                <pgterms:birthdate rdf:datatype="http://www.w3.org/2001/XMLSchema#integer">1775</pgterms:birthdate>
              </pgterms:agent>
            </dcterms:creator>
            <dcterms:language>
              <rdf:Description rdf:nodeID="Nlanguage">
                <rdf:value rdf:datatype="http://purl.org/dc/terms/RFC4646">en</rdf:value>
              </rdf:Description>
            </dcterms:language>
            <dcterms:subject>
              <rdf:Description rdf:nodeID="Nlcc">
                <dcam:memberOf rdf:resource="http://purl.org/dc/terms/LCC"/>
                <rdf:value>PR</rdf:value>
              </rdf:Description>
            </dcterms:subject>
            <dcterms:subject>
              <rdf:Description rdf:nodeID="Nlcsh">
                <dcam:memberOf rdf:resource="http://purl.org/dc/terms/LCSH"/>
                <rdf:value>England -- Fiction</rdf:value>
              </rdf:Description>
            </dcterms:subject>
            <dcterms:type>
              <rdf:Description rdf:nodeID="Ntype">
                <dcam:memberOf rdf:resource="http://purl.org/dc/terms/DCMIType"/>
                <rdf:value>Text</rdf:value>
              </rdf:Description>
            </dcterms:type>
            <dcterms:rights>Public domain in the USA.</dcterms:rights>
            <dcterms:hasFormat>
              <pgterms:file rdf:about="https://www.gutenberg.org/ebooks/1342.epub.noimages">
                <dcterms:format>
                  <rdf:Description rdf:nodeID="Nepub">
                    <dcam:memberOf rdf:resource="http://purl.org/dc/terms/IMT"/>
                    <rdf:value rdf:datatype="http://purl.org/dc/terms/IMT">application/epub+zip</rdf:value>
                  </rdf:Description>
                </dcterms:format>
              </pgterms:file>
            </dcterms:hasFormat>
          </pgterms:ebook>
        </rdf:RDF>
        """;

    private static readonly string SoundRdf = """
        <?xml version="1.0" encoding="utf-8"?>
        <rdf:RDF xml:base="http://www.gutenberg.org/"
          xmlns:dcterms="http://purl.org/dc/terms/"
          xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"
          xmlns:pgterms="http://www.gutenberg.org/2009/pgterms/"
          xmlns:dcam="http://purl.org/dc/dcam/">
          <pgterms:ebook rdf:about="ebooks/3002">
            <dcterms:title>Society's Child (audiofile)</dcterms:title>
            <dcterms:type>
              <rdf:Description rdf:nodeID="Ntype">
                <rdf:value>Sound</rdf:value>
              </rdf:Description>
            </dcterms:type>
            <dcterms:hasFormat>
              <pgterms:file rdf:about="https://www.gutenberg.org/files/3002/3002-h/mp3/sochi-high.mp3">
                <dcterms:format>
                  <rdf:Description rdf:nodeID="Nmp3">
                    <rdf:value rdf:datatype="http://purl.org/dc/terms/IMT">audio/mpeg</rdf:value>
                  </rdf:Description>
                </dcterms:format>
              </pgterms:file>
            </dcterms:hasFormat>
          </pgterms:ebook>
        </rdf:RDF>
        """;

    private static readonly string TypelessEbookRdf = """
        <?xml version="1.0" encoding="utf-8"?>
        <rdf:RDF xml:base="http://www.gutenberg.org/"
          xmlns:dcterms="http://purl.org/dc/terms/"
          xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"
          xmlns:pgterms="http://www.gutenberg.org/2009/pgterms/"
          xmlns:dcam="http://purl.org/dc/dcam/">
          <pgterms:ebook rdf:about="ebooks/11">
            <dcterms:title>A Record With No DCMI Type</dcterms:title>
            <dcterms:hasFormat>
              <pgterms:file rdf:about="https://www.gutenberg.org/ebooks/11.epub.noimages">
                <dcterms:format>
                  <rdf:Description rdf:nodeID="Nepub">
                    <rdf:value rdf:datatype="http://purl.org/dc/terms/IMT">application/epub+zip</rdf:value>
                  </rdf:Description>
                </dcterms:format>
              </pgterms:file>
            </dcterms:hasFormat>
          </pgterms:ebook>
        </rdf:RDF>
        """;

    private static readonly string TypelessAudioRdf = """
        <?xml version="1.0" encoding="utf-8"?>
        <rdf:RDF xml:base="http://www.gutenberg.org/"
          xmlns:dcterms="http://purl.org/dc/terms/"
          xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"
          xmlns:pgterms="http://www.gutenberg.org/2009/pgterms/">
          <pgterms:ebook rdf:about="ebooks/12">
            <dcterms:title>A Record With No Type At All</dcterms:title>
            <dcterms:hasFormat>
              <pgterms:file rdf:about="https://www.gutenberg.org/files/12/12.mp3">
                <dcterms:format>
                  <rdf:Description rdf:nodeID="Nmp3">
                    <rdf:value rdf:datatype="http://purl.org/dc/terms/IMT">audio/mpeg</rdf:value>
                  </rdf:Description>
                </dcterms:format>
              </pgterms:file>
            </dcterms:hasFormat>
          </pgterms:ebook>
        </rdf:RDF>
        """;

    private static (GutenbergProvider Provider, StubHttpMessageHandler Handler) CreateProvider(
        GutenbergSnapshotLimits? limits = null)
    {
        var handler = new StubHttpMessageHandler();
        var factory = new StubHttpClientFactory(handler);
        return (new GutenbergProvider(factory, NullLogger<GutenbergProvider>.Instance, limits), handler);
    }

    private static byte[] BuildZipArchive(params (string Name, byte[] Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
                using var stream = entry.Open();
                stream.Write(content, 0, content.Length);
            }
        }

        return buffer.ToArray();
    }

    private static byte[] BuildSnapshotTar(params (string Name, string Content)[] entries)
    {
        using var tarBuffer = new MemoryStream();
        using (var tar = new TarWriter(tarBuffer, TarEntryFormat.Pax, leaveOpen: true))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "cache/"));
            foreach (var (name, content) in entries)
            {
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
                });
            }
        }

        return tarBuffer.ToArray();
    }

    private static byte[] BuildSnapshotArchive(params (string Name, string Content)[] entries) =>
        BuildZipArchive(("rdf-files.tar", BuildSnapshotTar(entries)));

    /// <summary>
    /// Cuts the tar inside its end-of-archive zero blocks, so every member is
    /// complete but the next header read cannot be satisfied.
    /// </summary>
    private static byte[] TruncateSnapshotTar(byte[] tar)
    {
        var lastDataByte = Array.FindLastIndex(tar, value => value != 0);
        var contentEnd = (lastDataByte / 512 + 1) * 512;
        return tar[..(contentEnd + 100)];
    }

    /// <summary>
    /// Patches the one deflate entry to compression method 9 (deflate64), which
    /// <see cref="ZipArchive"/> cannot decode; the zip stays structurally valid,
    /// so the failure surfaces when the entry is opened.
    /// </summary>
    private static byte[] BuildZipWithUnsupportedCompression(params (string Name, byte[] Content)[] entries)
    {
        var zip = BuildZipArchive(entries);
        PatchCompressionMethod(zip, [0x50, 0x4B, 0x03, 0x04], methodOffset: 8, fromEnd: false);
        PatchCompressionMethod(zip, [0x50, 0x4B, 0x01, 0x02], methodOffset: 10, fromEnd: true);
        return zip;
    }

    private static void PatchCompressionMethod(byte[] zip, byte[] signature, int methodOffset, bool fromEnd)
    {
        var index = fromEnd ? zip.AsSpan().LastIndexOf(signature) : zip.AsSpan().IndexOf(signature);
        zip[index + methodOffset] = 9;
        zip[index + methodOffset + 1] = 0;
    }

    private static HttpResponseMessage ArchiveResponse(byte[] archive, DateTimeOffset? lastModified = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(archive),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        if (lastModified is not null)
            response.Content.Headers.LastModified = lastModified;

        return response;
    }

    private static async Task<List<ProviderItem>> DrainAsync(
        IAsyncEnumerable<ProviderItem> items,
        CancellationToken ct = default)
    {
        var result = new List<ProviderItem>();
        await foreach (var item in items.WithCancellation(ct))
            result.Add(item);

        return result;
    }

    private static string[] TempSnapshotFiles() =>
        Directory.GetFiles(Path.GetTempPath(), TempFilePattern);

    /// <summary>
    /// A readable, non-seekable stream over fixed bytes, so the response cannot
    /// declare a length and the reader has to enforce its cap while copying.
    /// </summary>
    private sealed class NonSeekableStream(byte[] content) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => content.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(count, content.Length - _position);
            content.AsSpan(_position, read).CopyTo(buffer.AsSpan(offset));
            _position += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = Math.Min(buffer.Length, content.Length - _position);
            content.AsMemory(_position, read).CopyTo(buffer);
            _position += read;
            return ValueTask.FromResult(read);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Read_returns_the_ebook_snapshot_with_card_metadata_cover_and_validators()
    {
        var archive = BuildSnapshotArchive(
            ("cache/epub/1342/pg1342.rdf", PrideRdf),
            ("cache/epub/3002/pg3002.rdf", SoundRdf),
            ("cache/epub/11/pg11.rdf", TypelessEbookRdf),
            ("cache/epub/12/pg12.rdf", TypelessAudioRdf),
            ("cache/epub/99999/notes.txt", "not a book"));
        var (provider, handler) = CreateProvider();
        handler.Register(SnapshotPath, _ => ArchiveResponse(archive, LastModified));

        var snapshot = await provider.ReadAsync(new ProviderSnapshotRequest(), CancellationToken.None);

        snapshot.Status.Should().Be(ProviderSnapshotStatus.Updated);
        snapshot.LastModified.Should().Be(LastModified);
        snapshot.NextCursor.Should().BeNull("the snapshot archive is the whole catalogue");

        var items = await DrainAsync(snapshot.Items);

        // The Sound record and the record without a type that offers no ebook
        // format are not ebooks; the non-RDF tar member is not a book at all.
        items.Select(item => item.ExternalId).Should().Equal("1342", "11");
        items.Should().OnlyContain(item => item.ProviderId == GutenbergProvider.ProviderIdentifier);
        items.Should().OnlyContain(item => item.MediaKind == ProviderMediaKind.Ebook);
        items.Should().OnlyContain(item => item.Assets.Count == 0);

        var pride = items[0];
        pride.Metadata.Title.Should().Be("Pride and Prejudice");
        pride.Metadata.Author.Should().Be("Jane Austen");
        pride.Metadata.Language.Should().Be("English");
        pride.Metadata.Categories.Should().Be("England -- Fiction", "LCC classification codes are not subjects");
        pride.Cover.Should().NotBeNull();
        pride.Cover!.Url.Should().Be(new Uri("https://www.gutenberg.org/cache/epub/1342/pg1342.cover.medium.jpg"));
        pride.Source!.ItemUrl.Should().Be("https://www.gutenberg.org/ebooks/1342");
        pride.Source.RightsStatement.Should().Be("Public domain in the USA.");

        items[1].Metadata.Title.Should().Be("A Record With No DCMI Type");
        items[1].Cover!.Url.Should().Be(new Uri("https://www.gutenberg.org/cache/epub/11/pg11.cover.medium.jpg"));

        var requests = handler.RecordedRequests;
        requests.Should().HaveCount(2, "a conditional probe followed by one lazy download");
        requests.Should().OnlyContain(request => request.RequestUri!.PathAndQuery == SnapshotPath);
        var probe = requests.Should().ContainSingle(request => request.Method == HttpMethod.Head).Which;
        requests.Should().ContainSingle(request => request.Method == HttpMethod.Get);
        probe.Headers.IfNoneMatch.Should().BeEmpty("a full read has no validator to send");
        probe.Headers.IfModifiedSince.Should().BeNull();
    }

    [Fact]
    public async Task Read_with_validators_probes_conditionally_and_reports_not_modified_without_downloading()
    {
        var (provider, handler) = CreateProvider();
        handler.Register(SnapshotPath, _ => new HttpResponseMessage(HttpStatusCode.NotModified));

        var snapshot = await provider.ReadAsync(
            new ProviderSnapshotRequest(ETag: "\"snapshot-1\"", IfModifiedSince: LastModified),
            CancellationToken.None);

        snapshot.Status.Should().Be(ProviderSnapshotStatus.NotModified);
        snapshot.ETag.Should().Be("\"snapshot-1\"");
        snapshot.LastModified.Should().Be(LastModified);
        (await DrainAsync(snapshot.Items)).Should().BeEmpty();

        var request = handler.RecordedRequests.Should().ContainSingle().Which;
        request.Method.Should().Be(HttpMethod.Head);
        request.Headers.IfNoneMatch.Should().ContainSingle().Which.Tag.Should().Be("\"snapshot-1\"");
        request.Headers.IfModifiedSince.Should().Be(LastModified);
    }

    [Fact]
    public async Task Read_reports_a_probe_failure_as_provider_unavailable()
    {
        var (provider, handler) = CreateProvider();
        handler.Register(SnapshotPath, _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var act = () => provider.ReadAsync(new ProviderSnapshotRequest(), CancellationToken.None);

        var error = await act.Should().ThrowAsync<ProviderException>();
        error.Which.Code.Should().Be(ProviderException.Unavailable);
    }

    [Fact]
    public async Task Read_reports_a_failed_download_as_provider_unavailable()
    {
        var archive = BuildSnapshotArchive(("cache/epub/1342/pg1342.rdf", PrideRdf));
        var (provider, handler) = CreateProvider();
        handler.Register(SnapshotPath, request => request.Method == HttpMethod.Head
            ? ArchiveResponse(archive, LastModified)
            : new HttpResponseMessage(HttpStatusCode.BadGateway));
        var before = TempSnapshotFiles();

        var snapshot = await provider.ReadAsync(new ProviderSnapshotRequest(), CancellationToken.None);

        var act = () => DrainAsync(snapshot.Items);
        var error = await act.Should().ThrowAsync<ProviderException>();
        error.Which.Code.Should().Be(ProviderException.Unavailable);
        TempSnapshotFiles().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Read_reports_an_upstream_timeout_as_provider_unavailable()
    {
        var archive = BuildSnapshotArchive(("cache/epub/1342/pg1342.rdf", PrideRdf));
        var (provider, handler) = CreateProvider();
        handler.Register(SnapshotPath, request => request.Method == HttpMethod.Head
            ? ArchiveResponse(archive, LastModified)
            : throw new TaskCanceledException("the caller's token is not cancelled"));
        var before = TempSnapshotFiles();

        var snapshot = await provider.ReadAsync(new ProviderSnapshotRequest(), CancellationToken.None);

        var act = () => DrainAsync(snapshot.Items);
        var error = await act.Should().ThrowAsync<ProviderException>();
        error.Which.Code.Should().Be(ProviderException.Unavailable);
        TempSnapshotFiles().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Read_reports_a_truncated_rdf_member_as_invalid_provider_response()
    {
        var archive = BuildSnapshotArchive(
            ("cache/epub/1342/pg1342.rdf", "<rdf:RDF><pgterms:ebook><dcterms:title>Broken"));
        var (provider, handler) = CreateProvider();
        handler.Register(SnapshotPath, _ => ArchiveResponse(archive, LastModified));
        var before = TempSnapshotFiles();

        var snapshot = await provider.ReadAsync(new ProviderSnapshotRequest(), CancellationToken.None);

        var act = () => DrainAsync(snapshot.Items);
        var error = await act.Should().ThrowAsync<ProviderException>();
        error.Which.Code.Should().Be(ProviderException.ResponseInvalid);
        TempSnapshotFiles().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Read_reports_a_tar_truncated_after_a_complete_member_as_invalid_provider_response()
    {
        var tar = BuildSnapshotTar(
            ("cache/epub/1342/pg1342.rdf", PrideRdf),
            ("cache/epub/11/pg11.rdf", TypelessEbookRdf));
        var archive = BuildZipArchive(("rdf-files.tar", TruncateSnapshotTar(tar)));
        var (provider, handler) = CreateProvider();
        handler.Register(SnapshotPath, _ => ArchiveResponse(archive, LastModified));
        var before = TempSnapshotFiles();

        var snapshot = await provider.ReadAsync(new ProviderSnapshotRequest(), CancellationToken.None);

        var items = new List<ProviderItem>();
        var act = async () =>
        {
            await foreach (var item in snapshot.Items)
                items.Add(item);
        };

        var error = await act.Should().ThrowAsync<ProviderException>();
        error.Which.Code.Should().Be(ProviderException.ResponseInvalid);
        items.Select(item => item.ExternalId).Should().Equal(
            new[] { "1342", "11" },
            "the complete members are delivered before the truncated tail fails");
        TempSnapshotFiles().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Read_reports_an_undecodable_tar_member_as_invalid_provider_response()
    {
        var archive = BuildZipWithUnsupportedCompression(
            ("rdf-files.tar", BuildSnapshotTar(("cache/epub/1342/pg1342.rdf", PrideRdf))));
        var (provider, handler) = CreateProvider();
        handler.Register(SnapshotPath, _ => ArchiveResponse(archive, LastModified));
        var before = TempSnapshotFiles();

        var snapshot = await provider.ReadAsync(new ProviderSnapshotRequest(), CancellationToken.None);

        var act = () => DrainAsync(snapshot.Items);
        var error = await act.Should().ThrowAsync<ProviderException>();
        error.Which.Code.Should().Be(ProviderException.ResponseInvalid);
        TempSnapshotFiles().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Read_reports_an_archive_without_a_tar_member_as_invalid_provider_response()
    {
        var archive = BuildZipArchive(("readme.txt", Encoding.UTF8.GetBytes("not the catalogue")));
        var (provider, handler) = CreateProvider();
        handler.Register(SnapshotPath, _ => ArchiveResponse(archive, LastModified));
        var before = TempSnapshotFiles();

        var snapshot = await provider.ReadAsync(new ProviderSnapshotRequest(), CancellationToken.None);

        var act = () => DrainAsync(snapshot.Items);
        var error = await act.Should().ThrowAsync<ProviderException>();
        error.Which.Code.Should().Be(ProviderException.ResponseInvalid);
        TempSnapshotFiles().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Read_reports_a_body_that_is_not_a_zip_as_invalid_provider_response()
    {
        var (provider, handler) = CreateProvider();
        handler.Register(SnapshotPath, _ => ArchiveResponse(Encoding.UTF8.GetBytes("this is not a zip")));
        var before = TempSnapshotFiles();

        var snapshot = await provider.ReadAsync(new ProviderSnapshotRequest(), CancellationToken.None);

        var act = () => DrainAsync(snapshot.Items);
        var error = await act.Should().ThrowAsync<ProviderException>();
        error.Which.Code.Should().Be(ProviderException.ResponseInvalid);
        TempSnapshotFiles().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Read_rejects_a_download_over_the_archive_limit_as_invalid_provider_response()
    {
        var limits = new GutenbergSnapshotLimits { MaxSnapshotBytes = 8 * 1024, MaxMemberBytes = 1024 * 1024 };
        var (provider, handler) = CreateProvider(limits);
        var body = new NonSeekableStream(new byte[limits.MaxSnapshotBytes * 16]);
        handler.Register(SnapshotPath, request => request.Method == HttpMethod.Head
            ? new HttpResponseMessage(HttpStatusCode.OK)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
        var before = TempSnapshotFiles();

        var snapshot = await provider.ReadAsync(new ProviderSnapshotRequest(), CancellationToken.None);

        var act = () => DrainAsync(snapshot.Items);
        var error = await act.Should().ThrowAsync<ProviderException>();
        error.Which.Code.Should().Be(ProviderException.ResponseInvalid);
        error.Which.Message.Should().Contain("byte limit", "the archive cap is what stopped the read");
        body.Position.Should().BeLessThan(body.Length, "the download must stop at the cap, not the body's end");
        TempSnapshotFiles().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Read_rejects_a_tar_member_over_the_member_limit_as_invalid_provider_response()
    {
        var limits = new GutenbergSnapshotLimits { MaxSnapshotBytes = 4 * 1024 * 1024, MaxMemberBytes = 4 * 1024 };

        // Valid RDF, just over the injected cap: without the cap this parses
        // into an item, so the test only passes because the member is refused.
        var oversizedRdf = PrideRdf.Replace(
            "</rdf:RDF>",
            "<!-- " + new string('x', (int)limits.MaxMemberBytes) + " --></rdf:RDF>");
        var archive = BuildSnapshotArchive(("cache/epub/1342/pg1342.rdf", oversizedRdf));
        var (provider, handler) = CreateProvider(limits);
        handler.Register(SnapshotPath, _ => ArchiveResponse(archive, LastModified));
        var before = TempSnapshotFiles();

        var snapshot = await provider.ReadAsync(new ProviderSnapshotRequest(), CancellationToken.None);

        var act = () => DrainAsync(snapshot.Items);
        var error = await act.Should().ThrowAsync<ProviderException>();
        error.Which.Code.Should().Be(ProviderException.ResponseInvalid);
        error.Which.Message.Should().Contain("member limit");
        TempSnapshotFiles().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Enumeration_deletes_the_snapshot_temp_file_on_completion()
    {
        var archive = BuildSnapshotArchive(("cache/epub/1342/pg1342.rdf", PrideRdf));
        var (provider, handler) = CreateProvider();
        handler.Register(SnapshotPath, _ => ArchiveResponse(archive, LastModified));
        var before = TempSnapshotFiles();

        var snapshot = await provider.ReadAsync(new ProviderSnapshotRequest(), CancellationToken.None);
        await DrainAsync(snapshot.Items);

        TempSnapshotFiles().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Enumeration_deletes_the_snapshot_temp_file_when_the_consumer_stops_early()
    {
        var archive = BuildSnapshotArchive(
            ("cache/epub/1342/pg1342.rdf", PrideRdf),
            ("cache/epub/11/pg11.rdf", TypelessEbookRdf));
        var (provider, handler) = CreateProvider();
        handler.Register(SnapshotPath, _ => ArchiveResponse(archive, LastModified));
        var before = TempSnapshotFiles();

        var snapshot = await provider.ReadAsync(new ProviderSnapshotRequest(), CancellationToken.None);
        await foreach (var item in snapshot.Items)
            break;

        TempSnapshotFiles().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Enumeration_honours_the_consumer_cancellation_token_and_cleans_up()
    {
        var archive = BuildSnapshotArchive(
            ("cache/epub/1342/pg1342.rdf", PrideRdf),
            ("cache/epub/11/pg11.rdf", TypelessEbookRdf));
        var (provider, handler) = CreateProvider();
        handler.Register(SnapshotPath, _ => ArchiveResponse(archive, LastModified));
        var before = TempSnapshotFiles();

        var snapshot = await provider.ReadAsync(new ProviderSnapshotRequest(), CancellationToken.None);
        using var cts = new CancellationTokenSource();

        var act = async () =>
        {
            await foreach (var item in snapshot.Items.WithCancellation(cts.Token))
                cts.Cancel();
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
        TempSnapshotFiles().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task An_unenumerated_snapshot_downloads_nothing_and_leaves_no_temp_file()
    {
        var archive = BuildSnapshotArchive(("cache/epub/1342/pg1342.rdf", PrideRdf));
        var (provider, handler) = CreateProvider();
        handler.Register(SnapshotPath, _ => ArchiveResponse(archive, LastModified));
        var before = TempSnapshotFiles();

        var snapshot = await provider.ReadAsync(new ProviderSnapshotRequest(), CancellationToken.None);

        snapshot.Status.Should().Be(ProviderSnapshotStatus.Updated);
        handler.RecordedRequests.Should().ContainSingle().Which.Method.Should().Be(HttpMethod.Head);
        TempSnapshotFiles().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Snapshot_reads_use_the_dedicated_snapshot_client_not_the_search_client()
    {
        var searchHandler = new StubHttpMessageHandler();
        var snapshotHandler = new StubHttpMessageHandler();
        snapshotHandler.Register(SnapshotPath, _ => new HttpResponseMessage(HttpStatusCode.NotModified));
        var factory = new StubHttpClientFactory(searchHandler)
            .RegisterHandler(GutenbergProvider.SnapshotHttpClientName, snapshotHandler);
        var provider = new GutenbergProvider(factory, NullLogger<GutenbergProvider>.Instance);

        var snapshot = await provider.ReadAsync(new ProviderSnapshotRequest(), CancellationToken.None);

        snapshot.Status.Should().Be(ProviderSnapshotStatus.NotModified);
        factory.RequestedClientNames.Should().Contain(GutenbergProvider.SnapshotHttpClientName);
        snapshotHandler.RecordedRequests.Should().ContainSingle(
            "the 20 s search client must never carry the bulk read");
        searchHandler.RecordedRequests.Should().BeEmpty();
    }

    [Fact]
    public void Snapshot_source_is_reachable_through_the_provider_registry()
    {
        var (provider, _) = CreateProvider();

        var registration = new ProviderRegistry(new IContentProvider[] { provider })
            .Find(GutenbergProvider.ProviderIdentifier);

        registration.Should().NotBeNull();
        registration!.Provider.Should().BeSameAs(provider);
        registration.Provider.Should().BeAssignableTo<IProviderSnapshotSource>(
            "a host reaches the bulk source through the registered provider");
    }
}

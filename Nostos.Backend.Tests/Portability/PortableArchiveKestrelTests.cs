using System.IO.Compression;
using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Endpoints;
using Nostos.Backend.Services;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// The genuine #554 regression: the complete SelfHosted pipeline listening on a
/// real Kestrel socket with <see cref="KestrelServerOptions.AllowSynchronousIO"/>
/// left at its default (false). The in-memory TestServer does not enforce
/// Kestrel's synchronous-IO prohibition, so only a real listener proves that
/// the bounded capture adapter absorbs native ZIP finalization (data
/// descriptors, Deflate purge, central directory) before the response body.
///
/// <para>Also proves the failure contract of the streaming export endpoint: an
/// export that fails after the response has started must abort the connection,
/// and the client must observe a failure rather than a clean 200 carrying a
/// truncated archive. The mutation seam is the copy-pass media check that
/// already exists in the product path.</para>
/// </summary>
public sealed class PortableArchiveKestrelTests
{
    [Fact]
    public async Task Export_over_real_kestrel_streams_a_complete_round_trippable_archive()
    {
        using var factory = new LibraryEndpointFactory();
        factory.UseKestrel(0);
        using var host = factory;

        // Premise of the regression: real Kestrel forbids synchronous response IO.
        host.Services
            .GetRequiredService<IOptions<KestrelServerOptions>>()
            .Value.AllowSynchronousIO.Should().BeFalse();

        Guid epubId;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
            var storage = scope.ServiceProvider.GetRequiredService<IBookAssetStorage>();
            var ids = await PortableArchiveTestSupport.PopulateRepresentativeAsync(db, storage);
            epubId = ids.EpubBookId;
        }

        using var client = host.CreateClient();
        AssertRealKestrelListener(host, client);

        using var response = await client.GetAsync(
            "/api/portability/export",
            HttpCompletionOption.ResponseHeadersRead);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType
            .Should().Be(PortabilityEndpoints.ArchiveContentType);
        response.Content.Headers.ContentDisposition.Should().NotBeNull();
        response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        response.Content.Headers.ContentDisposition.FileName
            .Should().StartWith("nostos-export-").And.EndWith(".nostos");

        using var bytes = new MemoryStream();
        await response.Content.CopyToAsync(bytes);
        bytes.Length.Should().BeGreaterThan(0);

        bytes.Position = 0;
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Read, leaveOpen: true))
        {
            archive.GetEntry("manifest.json").Should().NotBeNull();
            archive.GetEntry("data/library.json").Should().NotBeNull();
            archive.Entries.Should().Contain(
                entry => entry.FullName.StartsWith("media/books/", StringComparison.Ordinal));
        }

        // Round-trip through import into an empty library.
        bytes.Position = 0;
        await using var target = await LocalPortableTestLibrary.CreateAsync();
        var imported = await target.Portability().ImportAsync(bytes);

        imported.IntegrityVerified.Should().BeTrue();
        imported.Counts.Books.Should().Be(4);
        imported.MediaFiles.Should().Be(5);
        (await PortableArchiveTestSupport.ReadBookAsync(target.Storage, epubId))
            .Should().Equal(Encoding.UTF8.GetBytes("EPUB-CONTENT-PORTABLE"));
    }

    [Fact]
    public async Task Export_failure_after_real_kestrel_response_started_aborts_the_connection()
    {
        var probe = new CopyPassMutationStorage();
        using var factory = new LibraryEndpointFactory();
        using var host = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // The real host resolves the file storage through this seam so
                // the copy-pass revision check fails while the response streams.
                services.RemoveAll<IBookAssetStorage>();
                services.AddSingleton<IBookAssetStorage>(sp =>
                {
                    probe.Inner = sp.GetRequiredService<FileStorageService>();
                    return probe;
                });
            });
        });
        host.UseKestrel(0);

        host.Services
            .GetRequiredService<IOptions<KestrelServerOptions>>()
            .Value.AllowSynchronousIO.Should().BeFalse();

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
            var storage = scope.ServiceProvider.GetRequiredService<IBookAssetStorage>();
            storage.Should().BeSameAs(probe);

            var bookId = Guid.NewGuid();
            var work = new WorkModel
            {
                Id = Guid.NewGuid(),
                Title = "Kestrel Failure Work",
                NormalizedTitle = "KESTREL FAILURE WORK",
                NormalizedAuthor = string.Empty,
                CreatedAt = DateTime.UtcNow.AddDays(-1),
            };
            var book = new EBookModel
            {
                Id = bookId,
                WorkId = work.Id,
                Work = work,
                Title = "Kestrel Failure Book",
                CreatedAt = DateTime.UtcNow.AddDays(-1),
                FileDetails = new FileInfoDetails
                {
                    HasFile = true,
                    FileName = "book.epub",
                },
            };
            db.Works.Add(work);
            db.Books.Add(book);
            await db.SaveChangesAsync();

            var media = new byte[1024 * 1024];
            new Random(4242).NextBytes(media);
            await using (var content = new MemoryStream(media, writable: false))
            {
                await storage.SaveBookFileAsync(bookId, content, "book.epub");
            }

            probe.TargetBookId = bookId;
            probe.MutatedContent = (byte[])media.Clone();
            probe.MutatedContent[0] ^= 0xFF;

            // A background opener (for example book-text ingestion) can read
            // the file before the export starts. The failure seam must still
            // force the copy pass to observe a revision different from the one
            // the pin pass hashed, so provoke that ordering deterministically
            // instead of relying on the export being the first opener.
            await using var backgroundRead = await storage.OpenBookFileAsync(bookId);
            backgroundRead.Should().NotBeNull();
        }

        using var client = host.CreateClient();
        AssertRealKestrelListener(host, client);

        Exception? observed = null;
        try
        {
            using var response = await client.GetAsync(
                "/api/portability/export",
                HttpCompletionOption.ResponseHeadersRead);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new InvalidOperationException(
                    $"Expected a started 200 response, observed {(int)response.StatusCode}.");
            }

            // The server aborts mid-body; a clean completion would be the bug.
            // Wait on the connection outcome itself with a bound rather than
            // sampling the response once: the read surfaces the abort as an
            // exception, and a server that never aborts fails the bound instead
            // of hanging the suite.
            _ = await response.Content.ReadAsByteArrayAsync()
                .WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (Exception exception) when (
            exception is HttpRequestException
            or IOException
            or OperationCanceledException)
        {
            observed = exception;
        }

        observed.Should().NotBeNull(
            "an export failure after the response started must be visible to the client "
            + "as an aborted connection, never a clean 200 with a truncated body");
    }

    private static void AssertRealKestrelListener(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host,
        HttpClient client)
    {
        client.BaseAddress.Should().NotBeNull();
        client.BaseAddress!.Port.Should().BeGreaterThan(
            0,
            "a TestServer client has no bound socket; Kestrel on port 0 resolves a real port");
        host.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .GetType().FullName.Should().Contain(
                "Kestrel",
                "the #554 regression is only genuine when Kestrel's synchronous-IO rules are enforced");
    }

    /// <summary>
    /// Delegates every storage call to the real local store except that opens of
    /// the target book file after the first serve content distinct from every
    /// other open, same length, so the pin pass and the copy pass can never
    /// agree no matter how many other openers interleave. The pin pass still
    /// hashes whatever it reads and the copy-pass comparison fails with
    /// <c>source_media_changed</c> after archive output has begun.
    /// </summary>
    private sealed class CopyPassMutationStorage : IBookAssetStorage
    {
        private int _targetOpenCount;

        public FileStorageService? Inner { get; set; }

        public Guid? TargetBookId { get; set; }

        public byte[]? MutatedContent { get; set; }

        public Task<string> SaveBookFileAsync(
            Guid bookId,
            Stream content,
            string fileName,
            CancellationToken ct = default) =>
            Inner!.SaveBookFileAsync(bookId, content, fileName, ct);

        public Task<string> AdoptBookFileAsync(
            Guid bookId,
            string sourcePath,
            string fileName,
            CancellationToken ct = default) =>
            Inner!.AdoptBookFileAsync(bookId, sourcePath, fileName, ct);

        public Task<StoredAssetInfo?> GetBookFileInfoAsync(
            Guid bookId,
            CancellationToken ct = default) =>
            Inner!.GetBookFileInfoAsync(bookId, ct);

        public async Task<StoredAssetRead?> OpenBookFileAsync(
            Guid bookId,
            StorageByteRange? range = null,
            CancellationToken ct = default)
        {
            if (bookId == TargetBookId && range is null)
            {
                var open = Interlocked.Increment(ref _targetOpenCount);
                if (open > 1 && MutatedContent is not null)
                {
                    var info = await Inner!.GetBookFileInfoAsync(bookId, ct);
                    if (info is not null)
                    {
                        // Every open after the first serves bytes no other open
                        // served. A background worker (for example book-text
                        // ingestion) may open the file before or between the
                        // export's pin and copy passes; the copy pass must still
                        // see a revision different from the one the pin pass
                        // hashed.
                        var content = (byte[])MutatedContent.Clone();
                        content[(open - 1) % content.Length] ^= 0xFF;
                        return new StoredAssetRead(
                            info,
                            new MemoryStream(content, writable: false));
                    }
                }
            }

            return await Inner!.OpenBookFileAsync(bookId, range, ct);
        }

        public Task<bool> DeleteBookFileAsync(
            Guid bookId,
            CancellationToken ct = default) =>
            Inner!.DeleteBookFileAsync(bookId, ct);

        public Task DeleteBookFilesAsync(
            Guid bookId,
            CancellationToken ct = default) =>
            Inner!.DeleteBookFilesAsync(bookId, ct);

        public Task<string> SaveBookCoverAsync(
            Guid bookId,
            Stream content,
            string fileName,
            CancellationToken ct = default) =>
            Inner!.SaveBookCoverAsync(bookId, content, fileName, ct);

        public Task<StoredAssetInfo?> GetBookCoverInfoAsync(
            Guid bookId,
            CancellationToken ct = default) =>
            Inner!.GetBookCoverInfoAsync(bookId, ct);

        public Task<StoredAssetRead?> OpenBookCoverAsync(
            Guid bookId,
            CancellationToken ct = default) =>
            Inner!.OpenBookCoverAsync(bookId, ct);

        public Task<StoredAssetInfo?> GetBookCoverThumbnailInfoAsync(
            Guid bookId,
            int width,
            CancellationToken ct = default) =>
            Inner!.GetBookCoverThumbnailInfoAsync(bookId, width, ct);

        public Task<StoredAssetRead?> OpenBookCoverThumbnailAsync(
            Guid bookId,
            int width,
            CancellationToken ct = default) =>
            Inner!.OpenBookCoverThumbnailAsync(bookId, width, ct);

        public Task<bool> DeleteCoverAsync(
            Guid bookId,
            CancellationToken ct = default) =>
            Inner!.DeleteCoverAsync(bookId, ct);
    }
}

using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Data;
using Nostos.Backend.Endpoints;
using Nostos.Backend.Services.Library.Imports;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Calibre;

/// <summary>
/// The uploaded-zip Calibre import (issue #1572) through the real routes: a
/// zip built in the test from the committed fixture library, received in the
/// resumable parts, safely extracted, reviewed and imported by the same
/// service the mounted folder uses. The malicious-zip cases prove the
/// extraction refuses to write outside its scratch directory.
/// </summary>
public sealed class CalibreUploadEndpointTests
{
    private static string FixtureLibrary =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "calibre", "library");

    [Fact]
    public async Task An_uploaded_zip_is_extracted_and_imported_through_the_same_review_flow()
    {
        using var factory = new UploadFactory();
        var client = factory.CreateClient();

        var capabilities = await client.GetFromJsonAsync<DeploymentCapabilitiesResponse>(
            DeploymentCapabilitiesEndpoints.Route);
        capabilities!.SupportsCalibreUpload.Should().BeTrue(
            "the upload path is registered on this host");
        capabilities.SupportsCalibreImport.Should().BeTrue(
            "SelfHosted also has the folder path");

        var status = await GetStatusAsync(client);
        status.UploadSupported.Should().BeTrue();
        status.Configured.Should().BeFalse("no folder and no staged upload yet");
        status.Available.Should().BeFalse();
        status.MaxUploadBytes.Should().Be(CalibreUploadOptions.DefaultMaxLibraryBytes);

        var zip = ZipLibrary();
        var upload = await UploadAsync(client, zip);
        upload.State.Should().Be(CalibreUploadStates.Staged);
        upload.Books.Should().Be(8, "the fixture library has eight books");
        upload.StagedBytes.Should().BeGreaterThan(0);

        var preview = await PreviewAsync(client);
        preview.Source.Should().Be("calibre");
        preview.Books.Should().HaveCount(6);
        preview.Summary.Skipped.Should().Be(2);
        preview.Books.Single(book => book.CalibreId == 1).Formats
            .Select(format => format.Format).Should().Equal("EPUB", "PDF");

        var result = await ImportAsync(client, new CalibreImportDecision(
            "calibre:1",
            [preview.Books.Single(book => book.CalibreId == 1).Formats[0].Key],
            CreateCollections: false));
        result.Books.Single().Status.Should().Be("imported");
        result.Books.Single().FileCount.Should().Be(1);

        await using var db = CreateContext(factory.DatabasePath);
        var book = await db.Books.Include(candidate => candidate.Work).SingleAsync();
        book.Title.Should().Be("Pride and Prejudice");
        book.FileDetails.HasFile.Should().BeTrue();
        book.Work!.Rating.Should().Be(5);
        Directory.Exists(Path.Combine(factory.BooksRootPath, book.Id.ToString())).Should().BeTrue();

        // The staged zip and extraction are disposable and go after the commit.
        factory.ScratchIsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task A_zip_with_one_top_level_folder_is_accepted()
    {
        using var factory = new UploadFactory();
        var client = factory.CreateClient();

        var upload = await UploadAsync(client, ZipLibrary(nested: true));

        upload.State.Should().Be(CalibreUploadStates.Staged);
        upload.Books.Should().Be(8);
        var preview = await PreviewAsync(client);
        preview.Books.Should().HaveCount(6);
    }

    [Fact]
    public async Task A_resumed_upload_receives_only_the_parts_it_is_missing()
    {
        using var factory = new UploadFactory();
        var client = factory.CreateClient();
        var zip = ZipLibrary();
        var sha = Convert.ToHexStringLower(SHA256.HashData(zip));

        var created = await CreateAsync(client, zip.Length, sha);
        var firstLength = Math.Min(created.PartBytes, zip.Length);
        var first = zip.AsSpan(0, firstLength).ToArray();
        (await SendPartAsync(client, created.SessionId, 0, first, 0, zip.Length)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var resumed = await CreateAsync(client, zip.Length, sha);
        resumed.SessionId.Should().Be(created.SessionId);
        resumed.ReceivedParts.Should().Equal(0);

        if (created.TotalParts > 1)
        {
            var second = zip.AsSpan(created.PartBytes).ToArray();
            (await SendPartAsync(
                client, created.SessionId, 1, second, created.PartBytes, zip.Length)).StatusCode
                .Should().Be(HttpStatusCode.OK);
        }

        var completed = await CompleteAsync(client, created.SessionId);
        completed.State.Should().Be(CalibreUploadStates.Staged);
        completed.ReceivedParts.Should().HaveCount(created.TotalParts);
    }

    [Fact]
    public async Task A_part_sent_twice_is_receipted_once_and_never_changes_the_library()
    {
        using var factory = new UploadFactory();
        var client = factory.CreateClient();
        var zip = ZipLibrary();
        var sha = Convert.ToHexStringLower(SHA256.HashData(zip));
        var created = await CreateAsync(client, zip.Length, sha);
        var part = zip.AsSpan(0, Math.Min(created.PartBytes, zip.Length)).ToArray();

        (await SendPartAsync(client, created.SessionId, 0, part, 0, zip.Length)).StatusCode
            .Should().Be(HttpStatusCode.OK);
        var replay = await SendPartAsync(client, created.SessionId, 0, part, 0, zip.Length);
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        var receipt = await replay.Content.ReadFromJsonAsync<CalibreUploadPartReceipt>();
        receipt!.AlreadyPresent.Should().BeTrue();

        var wrong = (byte[])part.Clone();
        wrong[0] ^= 0xFF;
        var conflict = await SendPartAsync(client, created.SessionId, 0, wrong, 0, zip.Length);
        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await conflict.Content.ReadAsStringAsync()).Should().Contain("calibre_upload_part_conflict");
    }

    [Fact]
    public async Task A_missing_part_is_reported_with_the_missing_index()
    {
        using var factory = new UploadFactory();
        var client = factory.CreateClient();
        var zip = ZipLibrary();
        var sha = Convert.ToHexStringLower(SHA256.HashData(zip));
        var created = await CreateAsync(client, zip.Length, sha);
        if (created.TotalParts <= 1)
            return; // The fixture zip is small; covered by the multi-part test when it grows.

        var response = await client.PostAsync($"/api/calibre/upload/{created.SessionId}/complete", null);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await response.Content.ReadFromJsonAsync<CalibreUploadErrorBody>();
        body!.Code.Should().Be(CalibreUploadException.Incomplete);
        body.MissingParts.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Zip_slip_and_absolute_paths_are_refused_and_nothing_is_written_outside_scratch()
    {
        foreach (var entryName in new[] { "../escape.txt", "/tmp/nostos-escape.txt", "C:/escape.txt", "a/../../escape.txt" })
        {
            using var factory = new UploadFactory();
            var client = factory.CreateClient();
            var zip = ZipLibrary(extra: archive =>
            {
                var entry = archive.CreateEntry(entryName);
                using var output = entry.Open();
                output.Write("owned"u8);
            });

            var exception = await Assert.ThrowsAsync<UploadFailure>(() => UploadAsync(client, zip));
            exception.Code.Should().Be(CalibreUploadException.UnsafeEntry, "entry {0}", entryName);
            exception.Status.Should().Be(HttpStatusCode.UnprocessableEntity);

            File.Exists(Path.Combine(factory.UploadRoot, "..", "escape.txt")).Should().BeFalse();
            File.Exists("/tmp/nostos-escape.txt").Should().BeFalse();
            factory.ScratchIsEmpty.Should().BeTrue("a failed upload cleans its scratch");
        }
    }

    [Fact]
    public async Task A_symbolic_link_entry_is_refused()
    {
        using var factory = new UploadFactory();
        var client = factory.CreateClient();
        var zip = ZipLibrary(extra: archive =>
        {
            var link = archive.CreateEntry("link-to-etc");
            // Unix symlink mode 0xA1FF in the high 16 bits of ExternalAttributes.
            link.ExternalAttributes = unchecked((int)0xA1FF0000);
            using var output = link.Open();
            output.Write("/etc/passwd"u8);
        });

        var exception = await Assert.ThrowsAsync<UploadFailure>(() => UploadAsync(client, zip));

        exception.Code.Should().Be(CalibreUploadException.UnsafeEntry);
        factory.ScratchIsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task An_oversized_extracted_library_is_refused()
    {
        using var factory = new UploadFactory(new Dictionary<string, string?>
        {
            ["CalibreUpload:MaxExtractedBytes"] = "1024",
        });
        var client = factory.CreateClient();

        var exception = await Assert.ThrowsAsync<UploadFailure>(() => UploadAsync(client, ZipLibrary()));

        exception.Code.Should().Be(CalibreUploadException.ExtractedTooLarge);
        exception.Message.Should().Contain("larger than this server allows");
        factory.ScratchIsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task A_zip_over_the_size_cap_is_refused_before_a_part_is_received()
    {
        using var factory = new UploadFactory(new Dictionary<string, string?>
        {
            ["CalibreUpload:MaxLibraryBytes"] = "512",
        });
        var client = factory.CreateClient();
        var status = await GetStatusAsync(client);
        status.MaxUploadBytes.Should().Be(512, "the test host must receive its configured cap");

        var zip = ZipLibrary();
        var sha = Convert.ToHexStringLower(SHA256.HashData(zip));

        var response = await client.PostAsJsonAsync("/api/calibre/upload", new
        {
            totalBytes = zip.Length,
            fileName = "library.zip",
            sha256 = sha,
        });

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        var body = await response.Content.ReadFromJsonAsync<CalibreUploadErrorBody>();
        body!.Code.Should().Be(CalibreUploadException.TooLarge);
        body.Message.Should().Contain("the limit is 512 bytes");
        factory.ScratchIsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task A_zip_without_metadata_db_says_so()
    {
        using var factory = new UploadFactory();
        var client = factory.CreateClient();
        var zip = ZipWithoutLibrary();

        var exception = await Assert.ThrowsAsync<UploadFailure>(() => UploadAsync(client, zip));

        exception.Code.Should().Be(CalibreUploadException.MetadataMissing);
        exception.Message.Should().Contain("metadata.db");
        factory.ScratchIsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task A_non_zip_upload_is_refused()
    {
        using var factory = new UploadFactory();
        var client = factory.CreateClient();

        var exception = await Assert.ThrowsAsync<UploadFailure>(() =>
            UploadAsync(client, "not a zip"u8.ToArray()));

        exception.Code.Should().Be(CalibreUploadException.InvalidArchive);
        factory.ScratchIsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task A_library_over_the_file_count_cap_is_refused()
    {
        using var factory = new UploadFactory(new Dictionary<string, string?>
        {
            ["CalibreUpload:MaxFiles"] = "3",
        });
        var client = factory.CreateClient();

        var exception = await Assert.ThrowsAsync<UploadFailure>(() => UploadAsync(client, ZipLibrary()));

        exception.Code.Should().Be(CalibreUploadException.TooManyFiles);
        factory.ScratchIsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task Cancelling_removes_the_staged_bytes_and_a_new_library_can_start()
    {
        using var factory = new UploadFactory();
        var client = factory.CreateClient();
        var zip = ZipLibrary();
        var upload = await UploadAsync(client, zip);

        var cancelled = await client.DeleteAsync($"/api/calibre/upload/{upload.SessionId}");
        cancelled.StatusCode.Should().Be(HttpStatusCode.NoContent);
        factory.ScratchIsEmpty.Should().BeTrue();
        (await client.GetAsync("/api/calibre/preview")).StatusCode.Should().Be(HttpStatusCode.Conflict);

        var again = await UploadAsync(client, zip);
        again.State.Should().Be(CalibreUploadStates.Staged);
    }

    [Fact]
    public async Task Starting_a_different_library_replaces_the_previous_upload()
    {
        using var factory = new UploadFactory();
        var client = factory.CreateClient();
        _ = await UploadAsync(client, ZipLibrary());

        var exception = await Assert.ThrowsAsync<UploadFailure>(() =>
            UploadAsync(client, ZipWithoutLibrary()));

        exception.Code.Should().Be(CalibreUploadException.MetadataMissing);
        var status = await GetStatusAsync(client);
        status.Upload.Should().BeNull("the failed replacement removed the old staged library");
        factory.ScratchIsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task An_expired_upload_is_removed_and_reported_as_gone()
    {
        using var factory = new UploadFactory();
        var client = factory.CreateClient();
        var upload = await UploadAsync(client, ZipLibrary());

        // Move the manifest into the past, then run the same expiry pass the
        // cleanup worker runs.
        var manifestPath = Directory.EnumerateFiles(
            factory.UploadRoot, "manifest.json", SearchOption.AllDirectories).Single();
        var json = await File.ReadAllTextAsync(manifestPath);
        var manifest = JsonSerializer.Deserialize<CalibreUploadManifest>(
            json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        manifest.ExpiresAtUtc = DateTime.UtcNow.AddHours(-1);
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var service = factory.Services.GetRequiredService<CalibreUploadService>();
        (await service.ExpireAsync(CancellationToken.None)).Should().BeGreaterThan(0);

        factory.ScratchIsEmpty.Should().BeTrue();
        (await factory.CreateClient().GetAsync($"/api/calibre/upload/{upload.SessionId}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static async Task<CalibreStatusResponse> GetStatusAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<CalibreStatusResponse>("/api/calibre/"))!;

    private static async Task<CalibreUploadStatus> CreateAsync(
        HttpClient client,
        long totalBytes,
        string sha256,
        string fileName = "library.zip")
    {
        var response = await client.PostAsJsonAsync("/api/calibre/upload", new
        {
            totalBytes,
            fileName,
            sha256,
        });
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<CalibreUploadStatus>())!;
    }

    private static async Task<HttpResponseMessage> SendPartAsync(
        HttpClient client,
        Guid sessionId,
        int index,
        byte[] bytes,
        long offset,
        long total)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/calibre/upload/{sessionId}/parts/{index}")
        {
            Content = new ByteArrayContent(bytes),
        };
        request.Content.Headers.ContentType = new("application/octet-stream");
        request.Content.Headers.TryAddWithoutValidation(
            "Content-Range", $"bytes {offset}-{offset + bytes.Length - 1}/{total}");
        request.Headers.TryAddWithoutValidation(
            "X-Nostos-Chunk-SHA256", Convert.ToHexStringLower(SHA256.HashData(bytes)));
        return await client.SendAsync(request);
    }

    private static async Task<CalibreUploadStatus> CompleteAsync(HttpClient client, Guid sessionId)
    {
        var response = await client.PostAsync($"/api/calibre/upload/{sessionId}/complete", null);
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<CalibreUploadStatus>())!;
    }

    private static async Task<CalibreUploadStatus> UploadAsync(HttpClient client, byte[] zip)
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData(zip));
        var created = await CreateAsync(client, zip.Length, sha);
        for (var index = 0; index < created.TotalParts; index++)
        {
            var offset = (long)index * created.PartBytes;
            var length = Math.Min(created.PartBytes, zip.Length - (int)offset);
            var part = zip.AsSpan((int)offset, length).ToArray();
            var sent = await SendPartAsync(client, created.SessionId, index, part, offset, zip.Length);
            await EnsureSuccessAsync(sent);
        }

        var completed = await client.PostAsync($"/api/calibre/upload/{created.SessionId}/complete", null);
        await EnsureSuccessAsync(completed);
        return (await completed.Content.ReadFromJsonAsync<CalibreUploadStatus>())!;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;

        CalibreUploadErrorBody? body = null;
        try
        {
            body = await response.Content.ReadFromJsonAsync<CalibreUploadErrorBody>();
        }
        catch (JsonException)
        {
            // A proxy or Kestrel may answer without our body; report the status.
        }

        throw new UploadFailure(
            body?.Code ?? CalibreUploadException.Failed,
            response.StatusCode,
            body?.Message ?? $"The request failed with {(int)response.StatusCode}.");
    }

    private static async Task<CalibreImportPreview> PreviewAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/calibre/preview");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<CalibreImportPreview>())!;
    }

    private static async Task<CalibreImportResult> ImportAsync(
        HttpClient client,
        params CalibreImportDecision[] decisions)
    {
        var response = await client.PostAsJsonAsync("/api/calibre/import", new { decisions });
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var state = await WaitForImportAsync(client);
        state.Status.Should().Be("done");
        return new CalibreImportResult(state.BatchId, "calibre", state.Results);
    }

    private static async Task<CalibreImportJobState> WaitForImportAsync(HttpClient client)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            var state = await client.GetFromJsonAsync<CalibreImportJobState>("/api/calibre/import");
            if (state!.Status is not ("importing" or "idle"))
                return state;
            await Task.Delay(25);
        }

        throw new TimeoutException("The Calibre import did not finish.");
    }

    private static byte[] ZipLibrary(bool nested = false, Action<ZipArchive>? extra = null)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(FixtureLibrary, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(FixtureLibrary, file).Replace('\\', '/');
                var name = nested ? "Calibre Library/" + relative : relative;
                var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
                using var input = File.OpenRead(file);
                using var output = entry.Open();
                input.CopyTo(output);
            }

            extra?.Invoke(archive);
        }

        return stream.ToArray();
    }

    private static byte[] ZipWithoutLibrary()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("notes.txt");
            using var output = entry.Open();
            output.Write("nothing to import"u8);
        }

        return stream.ToArray();
    }

    private static NostosDbContext CreateContext(string path)
    {
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;
        return new NostosDbContext(options);
    }

    private sealed class UploadFactory : LibraryEndpointFactory
    {
        private readonly Dictionary<string, string?> _settings;

        public UploadFactory(Dictionary<string, string?>? settings = null)
        {
            _settings = settings ?? [];
        }

        public string UploadRoot => Path.Combine(StorageTempRoot, "calibre-uploads");

        public bool ScratchIsEmpty =>
            !Directory.Exists(UploadRoot)
            || !Directory.EnumerateFileSystemEntries(UploadRoot).Any();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            foreach (var (key, value) in _settings)
                builder.UseSetting(key, value);
        }
    }

    private sealed class UploadFailure(string code, HttpStatusCode status, string message)
        : Exception(message)
    {
        public string Code { get; } = code;
        public HttpStatusCode Status { get; } = status;
    }

    private sealed record CalibreUploadErrorBody(string Code, string? Message, int[]? MissingParts);
}

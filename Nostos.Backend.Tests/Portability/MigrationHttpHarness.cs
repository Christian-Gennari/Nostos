using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nostos.Backend.Data;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Migration;
using Nostos.Backend.Services.Portability.Transfers;
using Nostos.Backend.Tests.Support;

using static Nostos.Backend.Tests.Portability.PortableArchiveTestSupport;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Real in-process SelfHosted host (full <c>Program</c> pipeline, real Kestrel)
/// over a disposable SQLite file and transfer root, with a manual clock so job
/// progress is driven by explicit time advances instead of wall-clock sleeps.
/// </summary>
internal sealed class MigrationHttpHarness : IAsyncDisposable
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    internal string Root { get; }
    internal string DatabasePath { get; }
    internal string TransferPath { get; }
    internal EngineClock Clock { get; } = new();
    internal FakeTransferVolume Volume { get; } = new()
    {
        AvailableFreeSpaceBytes = 20L * 1024 * 1024 * 1024,
        TotalSizeBytes = 20L * 1024 * 1024 * 1024,
    };
    internal MigrationHttpProbe Probe { get; } = new();
    internal bool PhasesAvailable { get; set; } = true;

    /// <summary>
    /// Overrides <c>LibraryMigration:Enabled</c> for the host. Null keeps the
    /// production default (enabled).
    /// </summary>
    internal bool? LibraryMigrationEnabled { get; set; }
    internal bool UseRealPhaseHandlers { get; set; }
    internal Action<IServiceCollection>? ConfigureServices { get; set; }

    private WebApplicationFactory<Program>? _factory;

    internal HttpClient Client { get; private set; } = null!;

    internal MigrationHttpHarness()
    {
        Root = Path.Combine(Path.GetTempPath(), "nostos-migration-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        DatabasePath = Path.Combine(Root, "nostos.db");
        TransferPath = Path.Combine(Root, "transfers");
        LibraryEndpointBootstrap.EnsureSchemaAndHistory(DatabasePath);
    }

    internal MigrationHttpHarness Start()
    {
        _factory = new HostFactory(this);
        _factory.UseKestrel(0);
        Client = _factory.CreateClient();
        return this;
    }

    internal async Task RestartAsync()
    {
        Client.Dispose();
        await _factory!.DisposeAsync();
        Start();
    }

    internal async Task WithDb(Func<NostosDbContext, Task> action)
    {
        await using var scope = _factory!.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<NostosDbContext>());
    }

    internal async Task<T> WithDb<T>(Func<NostosDbContext, Task<T>> action)
    {
        await using var scope = _factory!.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<NostosDbContext>());
    }

    internal T GetService<T>() where T : notnull =>
        _factory!.Services.GetRequiredService<T>();

    internal async Task<T> WithCapacityAsync<T>(Func<ITransferStorageCapacity, Task<T>> action)
    {
        await using var scope = _factory!.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<ITransferStorageCapacity>());
    }

    internal static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(payload);
    }

    internal async Task<(HttpStatusCode Status, JsonDocument Body)> SendAsync(
        HttpMethod method,
        string path,
        HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        return (response.StatusCode, await ReadJsonAsync(response));
    }

    internal async Task<(HttpStatusCode Status, JsonDocument Body)> PostJsonAsync<T>(
        string path,
        T payload)
    {
        using var content = JsonContent.Create(payload, options: Json);
        return await SendAsync(HttpMethod.Post, path, content);
    }

    internal async Task<(HttpStatusCode Status, JsonDocument Body)> PreflightAsync(
        long archiveBytes,
        long mediaBytes = 0,
        string? clientRevision = null,
        bool operationalBackup = false)
    {
        var request = new
        {
            incomingCounts = new
            {
                works = 1,
                books = 1,
                notes = 0,
                topics = 0,
                noteTopics = 0,
                writings = 0,
                writingNotes = 0,
                collections = 0,
                collectionMemberships = 0,
                acquisitions = 0,
                assistantSettings = 0,
                noteImportBookLinks = 0,
                mediaEntries = 1,
            },
            declaredArchiveBytes = archiveBytes,
            declaredMediaBytes = mediaBytes,
            maxSingleEntryBytes = 1024,
            declaredFormatVersion = 1,
            declaredDataVersion = 1,
            declaredFormatName = "nostos-portable",
            clientDestinationRevision = clientRevision,
            isOperationalBackup = operationalBackup,
        };
        return await PostJsonAsync("/api/portability/migration/preflight", request);
    }

    internal async Task<(HttpStatusCode Status, JsonDocument Body)> CreateJobAsync(
        string direction,
        string key,
        Guid? reservationId)
    {
        var (status, _, body) = await CreateJobWithLocationAsync(direction, key, reservationId);
        return (status, body);
    }

    internal async Task<(HttpStatusCode Status, Uri? Location, JsonDocument Body)> CreateJobWithLocationAsync(
        string direction,
        string key,
        Guid? reservationId)
    {
        using var content = JsonContent.Create(new
        {
            direction,
            idempotencyKey = key,
            reservationId,
        }, options: Json);
        using var response = await Client.SendAsync(new HttpRequestMessage(
            HttpMethod.Post,
            "/api/portability/migration/jobs")
        {
            Content = content,
        });
        return (response.StatusCode, response.Headers.Location, await ReadJsonAsync(response));
    }

    /// <summary>Preflight for an allowed transfer and returns its reservation id.</summary>
    internal async Task<Guid> ReserveAsync(string key)
    {
        _ = key;
        // Generous declared size so any test file's host peak fits the claim.
        var (status, body) = await PreflightAsync(archiveBytes: 64L * 1024 * 1024);
        if (status != HttpStatusCode.OK)
            throw new InvalidOperationException($"Preflight returned {(int)status}.");
        return body.RootElement.GetProperty("reservationId").GetGuid();
    }

    internal async Task<Guid> CreateImportJobAsync(string key)
    {
        var reservation = await ReserveAsync(key);
        var (status, body) = await CreateJobAsync("Import", key, reservation);
        if (status != HttpStatusCode.Created)
        {
            body.Dispose();
            throw new InvalidOperationException($"Creating import job '{key}' returned {(int)status}.");
        }

        var jobId = body.RootElement.GetProperty("job").GetProperty("id").GetGuid();
        body.Dispose();
        return jobId;
    }

    internal async Task<(HttpStatusCode Status, JsonDocument Body, byte[] File)> CreateSessionAsync(
        Guid jobId,
        byte[] file,
        int chunkSize = 4 * 1024 * 1024,
        string key = "session-key",
        string? identityHash = null)
    {
        var payload = new
        {
            purpose = "Import",
            totalBytes = file.LongLength,
            chunkSize,
            totalChunks = ChunkCount(file.LongLength, chunkSize),
            fileIdentity = new
            {
                totalSizeBytes = file.LongLength,
                sha256Checksum = identityHash ?? PortableArchiveTestSupport.Sha256Hex(file),
                clientFingerprint = "test-file",
            },
            idempotencyKey = key,
        };
        var result = await PostJsonAsync($"/api/portability/migration/jobs/{jobId}/upload-session", payload);
        return (result.Status, result.Body, file);
    }

    internal async Task<(HttpStatusCode Status, JsonDocument Body)> CreateSessionRawAsync(
        Guid jobId,
        long totalBytes,
        int chunkSize,
        string identityHash,
        string key = "session-key")
    {
        return await PostJsonAsync($"/api/portability/migration/jobs/{jobId}/upload-session", new
        {
            purpose = "Import",
            totalBytes,
            chunkSize,
            totalChunks = ChunkCount(totalBytes, chunkSize),
            fileIdentity = new
            {
                totalSizeBytes = totalBytes,
                sha256Checksum = identityHash,
                clientFingerprint = "test-file",
            },
            idempotencyKey = key,
        });
    }

    internal async Task<(HttpStatusCode Status, JsonDocument Body)> UploadChunkAsync(
        Guid jobId,
        byte[] file,
        int index,
        int chunkSize = 4 * 1024 * 1024,
        Func<byte[], byte[]>? mutateBody = null,
        string? declaredHash = null,
        string? contentRange = null)
    {
        var offset = (long)index * chunkSize;
        var length = (int)Math.Min(chunkSize, file.LongLength - offset);
        var body = file.AsSpan((int)offset, length).ToArray();
        var sentBytes = mutateBody?.Invoke(body) ?? body;
        var hash = declaredHash ?? Sha256Hex(sentBytes);
        using var content = new ByteArrayContent(sentBytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        if (contentRange is not null)
            content.Headers.TryAddWithoutValidation("Content-Range", contentRange);
        else
            content.Headers.TryAddWithoutValidation(
                "Content-Range",
                $"bytes {offset}-{offset + length - 1}/{file.LongLength}");
        content.Headers.TryAddWithoutValidation("X-Nostos-Chunk-SHA256", hash);
        return await SendAsync(
            HttpMethod.Put,
            $"/api/portability/migration/jobs/{jobId}/upload-session/chunks/{index}",
            content);
    }

    internal async Task<(HttpStatusCode Status, JsonDocument Body)> SendChunkRawAsync(
        Guid jobId,
        int index,
        byte[] body,
        string contentRange,
        string hash)
    {
        using var content = new ByteArrayContent(body);
        content.Headers.TryAddWithoutValidation("Content-Range", contentRange);
        content.Headers.TryAddWithoutValidation("X-Nostos-Chunk-SHA256", hash);
        return await SendAsync(
            HttpMethod.Put,
            $"/api/portability/migration/jobs/{jobId}/upload-session/chunks/{index}",
            content);
    }

    internal async Task<(HttpStatusCode Status, JsonDocument Body)> CompleteAsync(Guid jobId) =>
        await SendAsync(
            HttpMethod.Post,
            $"/api/portability/migration/jobs/{jobId}/upload-session/complete",
            new ByteArrayContent([]));

    internal async Task<JsonDocument> JobStatusAsync(Guid jobId)
    {
        var (status, body) = await SendAsync(
            HttpMethod.Get,
            $"/api/portability/migration/jobs/{jobId}");
        if (status != HttpStatusCode.OK)
        {
            body.Dispose();
            throw new InvalidOperationException($"Job status returned {(int)status}.");
        }

        return body;
    }

    internal async Task<JsonDocument> WaitForJobStateAsync(
        Guid jobId,
        string expectedState,
        TimeSpan? timeout = null)
    {
        var result = await PortabilityTestPolling.PollUntilAsync(
            () => JobStatusAsync(jobId),
            body => body.RootElement.GetProperty("job").GetProperty("state").GetString()
                == expectedState,
            body => body.RootElement.GetProperty("job").GetProperty("state").GetString()
                ?? "<null>",
            timeout ?? TimeSpan.FromSeconds(15),
            TimeSpan.FromMilliseconds(20),
            disposeUnmatched: body => body.Dispose(),
            afterUnmatchedPoll: () => Clock.Advance(TimeSpan.FromSeconds(1)));

        if (result.Matched)
            return result.Value!;

        throw new Xunit.Sdk.XunitException(
            $"Job {jobId} did not reach {expectedState}; last state was {result.LastObservation}.");
    }

    internal void Advance(TimeSpan delta) => Clock.Advance(delta);

    internal static int ChunkCount(long totalBytes, int chunkSize) =>
        checked((int)((totalBytes - 1) / chunkSize + 1));


    internal static byte[] DeterministicBytes(int count, int seed = 7)
    {
        var bytes = new byte[count];
        var random = new Random(seed);
        random.NextBytes(bytes);
        return bytes;
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class HostFactory(MigrationHttpHarness harness) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseContentRoot(harness.Root);
            builder.UseSetting("Persistence:DatabasePath", harness.DatabasePath);
            builder.UseSetting("Storage:BooksRoot", Path.Combine(harness.Root, "books"));
            builder.UseSetting("Storage:TransferPath", harness.TransferPath);
            builder.UseSetting("Storage:BackupsRoot", Path.Combine(harness.Root, "backups"));
            builder.UseSetting("Storage:ChunkBytes", (4 * 1024 * 1024).ToString());
            builder.UseSetting("Storage:MinChunkBytes", (4 * 1024 * 1024).ToString());
            builder.UseSetting("Storage:MaxChunkBytes", (4 * 1024 * 1024).ToString());
            builder.UseSetting("Storage:DiskSafetyMarginBytes", "0");
            builder.UseSetting("Storage:DiskSafetyMarginPercent", "0");
            builder.UseSetting("Mcp:Enabled", "false");
            if (harness.LibraryMigrationEnabled is { } enabled)
            {
                builder.UseSetting(
                    LibraryMigrationOptions.SectionName + ":Enabled", enabled ? "true" : "false");
            }

            builder.ConfigureServices(services =>
            {
                // Only the migration workers run; every other hosted service is
                // irrelevant to this transport and would introduce wall-clock IO.
                foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService)).ToArray())
                    services.Remove(descriptor);

                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(harness.Clock);
                services.RemoveAll<ITransferVolume>();
                services.AddSingleton<ITransferVolume>(harness.Volume);
                services.RemoveAll<IMigrationPhaseAvailability>();
                services.AddSingleton<IMigrationPhaseAvailability>(sp => new ProbePhaseAvailability(
                    harness, sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<LibraryMigrationOptions>>().Value));
                if (!harness.UseRealPhaseHandlers)
                {
                    services.RemoveAll<IMigrationPhaseHandler>();
                    services.AddScoped<IMigrationPhaseHandler, SucceedingPhaseHandler>();
                }
                services.RemoveAll<ISelfHostedMigrationUploads>();
                services.AddScoped<ISelfHostedMigrationUploads>(sp => new ProbeUploads(
                    sp.GetRequiredService<SelfHostedMigrationTransferService>(),
                    harness.Probe));

                services.AddHostedService(sp => new MigrationJobWorker(
                    sp.GetRequiredService<IServiceScopeFactory>(),
                    sp.GetRequiredService<TimeProvider>(),
                    sp.GetRequiredService<MigrationJobCancellationRegistry>(),
                    sp.GetRequiredService<MigrationProcessingSlots>(),
                    sp.GetRequiredService<ILogger<MigrationJobWorker>>(),
                    sp.GetRequiredService<IMigrationMaintenanceGate>()));
                services.AddHostedService<MigrationTransferCleanupWorker>();

                harness.ConfigureServices?.Invoke(services);
            });
        }
    }
}

internal sealed class ProbePhaseAvailability(
    MigrationHttpHarness harness,
    LibraryMigrationOptions options) : IMigrationPhaseAvailability
{
    public bool IsAvailable(MigrationDirection direction) =>
        harness.PhasesAvailable && options.Enabled;
}

internal sealed class SucceedingPhaseHandler : IMigrationPhaseHandler
{
    public bool CanHandle(MigrationDirection direction, MigrationJobState state) => true;

    public Task ExecuteAsync(MigrationPhaseContext context, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Observation seam for the chunk route: records what kind of stream the
/// endpoint handed the upload engine and how the engine read it.
/// </summary>
internal sealed class MigrationHttpProbe
{
    public bool? ChunkStreamCanSeek { get; private set; }
    public string? ChunkStreamType { get; private set; }
    public int ChunkReadCount { get; private set; }
    public int ChunkMaxReadBytes { get; private set; }
    public bool SynchronousReadAttempted { get; private set; }

    internal void ObserveChunkStream(Stream content)
    {
        ChunkStreamCanSeek = content.CanSeek;
        ChunkStreamType = content.GetType().Name;
        ChunkReadCount = 0;
        ChunkMaxReadBytes = 0;
        SynchronousReadAttempted = false;
    }

    internal void ObserveRead(int count)
    {
        ChunkReadCount++;
        ChunkMaxReadBytes = Math.Max(ChunkMaxReadBytes, count);
    }

    internal void ObserveSynchronousRead() => SynchronousReadAttempted = true;
}

/// <summary>Delegating upload seam that wires the probe into the real engine.</summary>
internal sealed class ProbeUploads(
    ISelfHostedMigrationUploads inner,
    MigrationHttpProbe probe) : ISelfHostedMigrationUploads
{
    public Task<MigrationIdempotencyResult<MigrationSessionStatus>> CreateSessionAsync(
        Guid jobId, MigrationSessionRequest request, CancellationToken ct) =>
        inner.CreateSessionAsync(jobId, request, ct);

    public Task<MigrationSessionStatus> GetSessionAsync(Guid jobId, Guid sessionId, CancellationToken ct) =>
        inner.GetSessionAsync(jobId, sessionId, ct);

    public Task<MigrationChunkUploadResult> UploadChunkAsync(
        Guid jobId, Guid sessionId, int chunkIndex, Stream content, CancellationToken ct) =>
        inner.UploadChunkAsync(jobId, sessionId, chunkIndex, content, ct);

    public Task<MigrationChunkUploadResult> UploadChunkAsync(
        Guid jobId,
        Guid sessionId,
        int chunkIndex,
        MigrationChunkMetadata metadata,
        Stream content,
        CancellationToken ct)
    {
        probe.ObserveChunkStream(content);
        var observed = new ObservingReadStream(content, probe);
        return inner.UploadChunkAsync(jobId, sessionId, chunkIndex, metadata, observed, ct);
    }

    public Task<MigrationSessionStatus> CompleteSessionAsync(
        Guid jobId, Guid sessionId, CancellationToken ct) =>
        inner.CompleteSessionAsync(jobId, sessionId, ct);

    public Task<MigrationActivationPreparation> PrepareActivationAsync(Guid jobId, CancellationToken ct) =>
        inner.PrepareActivationAsync(jobId, ct);

    public Task CancelAsync(Guid jobId, MigrationCancelRequest request, CancellationToken ct) =>
        inner.CancelAsync(jobId, request, ct);

    public Task DeleteSessionAsync(Guid jobId, Guid sessionId, CancellationToken ct) =>
        inner.DeleteSessionAsync(jobId, sessionId, ct);
}

/// <summary>Async-only request-body reader; a synchronous read is a defect.</summary>
internal sealed class ObservingReadStream(Stream inner, MigrationHttpProbe probe) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var read = await inner.ReadAsync(buffer, ct);
        probe.ObserveRead(read);
        return read;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        probe.ObserveSynchronousRead();
        throw new InvalidOperationException("The migration endpoint must not read the request body synchronously.");
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Injects synthetic ENOSPC without touching the host disk.</summary>
internal sealed class FailingChunkUploadStore(TransferPathResolver paths) : FileMigrationUploadStore(paths)
{
    protected override FileStream CreateChunkFile(string path) =>
        throw new IOException("Synthetic ENOSPC for the migration HTTP test.");
}

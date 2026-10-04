using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Migration;
using Nostos.Backend.Services.Portability.Transfers;

namespace Nostos.Backend.Tests.Portability;

internal sealed class MigrationEngineHarness : IAsyncDisposable
{
    internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "nostos-engine-" + Guid.NewGuid().ToString("N"));
    internal EngineClock Clock { get; } = new();
    internal TransferStorageOptions Settings { get; } = new() { DiskSafetyMarginBytes = 0, DiskSafetyMarginPercent = 0 };
    internal TestVolume Volume { get; } = new();
    internal ServiceProvider Provider { get; private set; } = null!;
    internal TransferPathResolver Paths => Provider.GetRequiredService<TransferPathResolver>();
    internal LibraryMaintenanceCoordinator Maintenance => Provider.GetRequiredService<LibraryMaintenanceCoordinator>();
    internal MigrationTransferCleanupWorker CleanupWorker => Provider.GetServices<IHostedService>().OfType<MigrationTransferCleanupWorker>().Single();
    internal MigrationJobWorker Worker => Provider.GetServices<IHostedService>().OfType<MigrationJobWorker>().Single();
    internal Action<IServiceCollection>? Configure { get; set; }
    internal string ConnectionString => $"Data Source={Path.Combine(DirectoryPath, "jobs.db")};Pooling=False;Default Timeout=5";

    internal async Task InitializeAsync()
    {
        Directory.CreateDirectory(DirectoryPath);
        Provider = BuildProvider();
        await WithDb(db => db.Database.EnsureCreatedAsync());
    }
    internal async Task RestartAsync()
    {
        await Provider.DisposeAsync();
        Provider = BuildProvider();
    }
    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new LibraryMaintenanceCoordinator(clock: Clock));
        services.AddSingleton<ILibraryMaintenanceCoordinator>(s => s.GetRequiredService<LibraryMaintenanceCoordinator>());
        services.AddSingleton<IMigrationMaintenanceGate, MigrationMaintenanceGate>();
        services.AddDbContext<NostosDbContext>(o => o.UseSqlite(ConnectionString));
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSingleton(Options.Create(Settings));
        services.AddSingleton(new TransferPathResolver(TransferPathResolver.EnsureRootDirectory(Path.Combine(DirectoryPath, "transfers"))));
        services.AddSingleton<ITransferVolume>(Volume);
        services.AddSingleton<IBookAssetStorage, NullBookAssetStorage>();
        services.AddScoped<ITransferStorageCapacity, TransferStorageCapacity>();
        services.AddScoped<IMigrationJobStore, EfMigrationJobStore>();
        services.AddScoped<IPortableImportStaging>(s => new FilePortableImportStaging(
            s.GetRequiredService<TransferPathResolver>()));
        services.AddScoped<IPortableArchiveService>(s => new PortableArchiveService(
            s.GetRequiredService<NostosDbContext>(),
            s.GetRequiredService<IBookAssetStorage>(),
            NullLogger<PortableArchiveService>.Instance,
            bookTextScheduler: null,
            timeProvider: s.GetRequiredService<TimeProvider>()));
        services.AddSelfHostedMigrationEngine();
        Configure?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }
    internal async Task<T> WithDb<T>(Func<NostosDbContext, Task<T>> action)
    { await using var scope = Provider.CreateAsyncScope(); return await action(scope.ServiceProvider.GetRequiredService<NostosDbContext>()); }
    internal async Task WithDb(Func<NostosDbContext, Task> action)
    { await using var scope = Provider.CreateAsyncScope(); await action(scope.ServiceProvider.GetRequiredService<NostosDbContext>()); }
    internal async Task<T> WithUploads<T>(Func<ISelfHostedMigrationUploads, Task<T>> action)
    { await using var scope = Provider.CreateAsyncScope(); return await action(scope.ServiceProvider.GetRequiredService<ISelfHostedMigrationUploads>()); }
    internal async Task WithUploads(Func<ISelfHostedMigrationUploads, Task> action)
    { await using var scope = Provider.CreateAsyncScope(); await action(scope.ServiceProvider.GetRequiredService<ISelfHostedMigrationUploads>()); }
    internal async Task<T> WithJobs<T>(Func<IMigrationJobStore, Task<T>> action)
    { await using var scope = Provider.CreateAsyncScope(); return await action(scope.ServiceProvider.GetRequiredService<IMigrationJobStore>()); }
    internal async Task WithJobs(Func<IMigrationJobStore, Task> action)
    { await using var scope = Provider.CreateAsyncScope(); await action(scope.ServiceProvider.GetRequiredService<IMigrationJobStore>()); }
    internal async Task<Guid> NewJobAsync(MigrationDirection direction = MigrationDirection.Import) =>
        (await WithJobs(s => s.CreateAsync(direction, Guid.NewGuid().ToString("N"), default))).Resource!.Id;
    internal async Task<MigrationSessionStatus> StartAsync(Guid id, byte[] bytes, string? hash = null) =>
        (await WithUploads(s => s.CreateSessionAsync(id, Request(bytes, hash), default))).Resource!;
    internal static MigrationSessionRequest Request(byte[] bytes, string? hash = null) => new(MigrationSessionPurpose.Import,
        bytes.LongLength, MigrationContractLimits.MinChunkBytes, (bytes.Length - 1) / MigrationContractLimits.MinChunkBytes + 1,
        new(bytes.Length, hash ?? Hash(bytes), "file"), "session-key");
    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static byte[] Bytes(int count = 31) => Enumerable.Range(0, count).Select(i => (byte)(i * 7)).ToArray();
    internal Task<MigrationChunkUploadResult> Upload(Guid job, MigrationSessionStatus session, byte[] all, int index = 0)
    {
        var start = (long)index * session.ChunkSize;
        var bytes = all.AsSpan((int)start, (int)Math.Min(session.ChunkSize, all.Length - start)).ToArray();
        return UploadRaw(job, session, index, bytes, new(start, start + bytes.Length - 1, all.Length, Hash(bytes)));
    }
    internal Task<MigrationChunkUploadResult> UploadRaw(Guid job, MigrationSessionStatus session, int index,
        byte[] body, MigrationChunkMetadata metadata) => WithUploads(async s =>
    { await using var stream = new AsyncOnlyStream(body); return await s.UploadChunkAsync(job, session.SessionId, index, metadata, stream, default); });
    internal Task<MigrationSessionStatus> Complete(Guid id, MigrationSessionStatus session) =>
        WithUploads(s => s.CompleteSessionAsync(id, session.SessionId, default));
    internal Task<MigrationSessionStatus> Status(Guid id, MigrationSessionStatus session) =>
        WithUploads(s => s.GetSessionAsync(id, session.SessionId, default));
    internal Task Sweep() => RunCleanup(c => c.SweepAsync(default));
    internal async Task RunCleanup(Func<MigrationTransferCleanup, Task> action)
    { await using var scope = Provider.CreateAsyncScope(); await action(scope.ServiceProvider.GetRequiredService<MigrationTransferCleanup>()); }
    public async ValueTask DisposeAsync()
    { if (Provider is not null) await Provider.DisposeAsync(); if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true); }
    internal sealed class TestVolume : ITransferVolume
    { public long AvailableFreeSpaceBytes { get; set; } = 20L * 1024 * 1024 * 1024; public long TotalSizeBytes => 20L * 1024 * 1024 * 1024; }
}

// Empty provider-neutral asset storage for engine tests that never read media.
// Export tests replace this registration with a real FileStorageService.
internal sealed class NullBookAssetStorage : IBookAssetStorage
{
    public Task<string> SaveBookFileAsync(Guid bookId, Stream content, string fileName, CancellationToken ct = default) => Task.FromResult(fileName);
    public Task<string> AdoptBookFileAsync(Guid bookId, string sourcePath, string fileName, CancellationToken ct = default) => Task.FromResult(fileName);
    public Task<StoredAssetInfo?> GetBookFileInfoAsync(Guid bookId, CancellationToken ct = default) => Task.FromResult<StoredAssetInfo?>(null);
    public Task<StoredAssetRead?> OpenBookFileAsync(Guid bookId, StorageByteRange? range = null, CancellationToken ct = default) => Task.FromResult<StoredAssetRead?>(null);
    public Task<bool> DeleteBookFileAsync(Guid bookId, CancellationToken ct = default) => Task.FromResult(false);
    public Task DeleteBookFilesAsync(Guid bookId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<string> SaveBookCoverAsync(Guid bookId, Stream content, string fileName, CancellationToken ct = default) => Task.FromResult(fileName);
    public Task<StoredAssetInfo?> GetBookCoverInfoAsync(Guid bookId, CancellationToken ct = default) => Task.FromResult<StoredAssetInfo?>(null);
    public Task<StoredAssetRead?> OpenBookCoverAsync(Guid bookId, CancellationToken ct = default) => Task.FromResult<StoredAssetRead?>(null);
    public Task<StoredAssetInfo?> GetBookCoverThumbnailInfoAsync(Guid bookId, int width, CancellationToken ct = default) => Task.FromResult<StoredAssetInfo?>(null);
    public Task<StoredAssetRead?> OpenBookCoverThumbnailAsync(Guid bookId, int width, CancellationToken ct = default) => Task.FromResult<StoredAssetRead?>(null);
    public Task<bool> DeleteCoverAsync(Guid bookId, CancellationToken ct = default) => Task.FromResult(false);
}

// A request stream that throws on synchronous reads, seeking and length probes.
internal sealed class AsyncOnlyStream(byte[] bytes) : Stream
{
    private int _position;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); var n = Math.Min(buffer.Length, bytes.Length - _position); bytes.AsMemory(_position, n).CopyTo(buffer); _position += n; return ValueTask.FromResult(n); }
    public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous request read.");
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

// Fake UTC clock and timers. Advance fires due timers without wall-clock sleeps.
internal sealed class EngineClock : TimeProvider
{
    private readonly object _gate = new();
    private DateTimeOffset _now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private readonly List<ManualTimer> _timers = new();
    public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    { lock (_gate) { var timer = new ManualTimer(this, callback, state); _timers.Add(timer); timer.Change(dueTime, period); return timer; } }
    internal void Advance(TimeSpan delta)
    {
        List<ManualTimer> due;
        lock (_gate)
        { _now += delta; due = _timers.Where(t => !t.Disposed && t.Due <= _now).ToList(); foreach (var timer in due) timer.Due = timer.Period == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : _now + timer.Period; }
        foreach (var timer in due) timer.Callback(timer.State);
    }
    private sealed class ManualTimer(EngineClock clock, TimerCallback callback, object? state) : ITimer
    {
        internal TimerCallback Callback { get; } = callback;
        internal object? State { get; } = state;
        internal DateTimeOffset Due { get; set; }
        internal TimeSpan Period { get; private set; }
        internal bool Disposed { get; private set; }
        public bool Change(TimeSpan dueTime, TimeSpan period)
        { lock (clock._gate) { if (Disposed) return false; Due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : clock._now + dueTime; Period = period; return true; } }
        public void Dispose() { lock (clock._gate) { Disposed = true; clock._timers.Remove(this); } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nostos.Backend.Data;
using Nostos.Backend.Services;
using Nostos.Backend.Tests.Support;
using Nostos.Backend.Workers;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Workers;

// Hosted-worker tests for the scheduled backup loop (#1621). The poll delay runs
// on a clock the test advances, so a cycle is due exactly when the test says:
// no waiting five real minutes, and the maintenance `Busy` race, a failed
// cycle, and host shutdown are all deterministic.
public sealed class BackupWorkerTests : IDisposable
{
    private static readonly TriggerBackupResultDto CompletedBackup =
        new(Guid.NewGuid(), "Completed", 1024, DateTime.UtcNow);

    private readonly List<string> _roots = [];

    [Fact]
    public async Task Busy_maintenance_is_an_expected_skip_not_an_error()
    {
        using var harness = Create(async _ =>
        {
            await Task.Yield();
            throw LibraryMaintenanceCoordinator.Busy();
        });
        var logger = new CapturingLogger<BackupWorker>();
        var clock = new TestTimeProvider();
        var worker = new TestWorker(harness.Provider, harness.Settings, logger, clock);

        using var cts = new CancellationTokenSource();
        var run = worker.RunAsync(cts.Token);
        await clock.WaitForTimerCountAsync(1);

        clock.Advance(PollInterval);
        // The next poll is armed only after the cycle (including its logging)
        // finished, so this is not racing the log entry.
        await clock.WaitForTimerCountAsync(2);

        harness.BackupCalls.Should().Be(1);
        logger.Entries.Should().ContainSingle(entry =>
            entry.Level == LogLevel.Information && entry.Message.Contains("busy or in maintenance"));
        logger.Entries.Should().NotContain(entry => entry.Level >= LogLevel.Error,
            "a restore or activation that wins the lease is an expected skip, not a failed backup");

        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Failed_cycle_is_logged_and_retried_on_the_next_poll()
    {
        var attempts = 0;
        using var harness = Create(async _ =>
        {
            await Task.Yield();
            return Interlocked.Increment(ref attempts) == 1
                ? throw new InvalidOperationException("simulated backup failure")
                : CompletedBackup;
        });
        var logger = new CapturingLogger<BackupWorker>();
        var clock = new TestTimeProvider();
        var worker = new TestWorker(harness.Provider, harness.Settings, logger, clock);

        using var cts = new CancellationTokenSource();
        var run = worker.RunAsync(cts.Token);
        await clock.WaitForTimerCountAsync(1);

        clock.Advance(PollInterval);
        await clock.WaitForTimerCountAsync(2);

        clock.Advance(PollInterval);
        await clock.WaitForTimerCountAsync(3);

        harness.BackupCalls.Should().Be(2, "the failed cycle must not stop the loop");
        logger.Entries.Should().ContainSingle(entry =>
            entry.Level == LogLevel.Error && entry.Message.Contains("scheduled backup cycle"));
        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Information && entry.Message.Contains("Scheduled backup completed"));

        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Cancellation_during_a_backup_logs_no_error()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var harness = Create(async ct =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return CompletedBackup;
        });
        var logger = new CapturingLogger<BackupWorker>();
        var clock = new TestTimeProvider();
        var worker = new TestWorker(harness.Provider, harness.Settings, logger, clock);

        using var cts = new CancellationTokenSource();
        var run = worker.RunAsync(cts.Token);
        await clock.WaitForTimerCountAsync(1);

        clock.Advance(PollInterval);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        logger.Entries.Should().NotContain(entry => entry.Level >= LogLevel.Error,
            "host shutdown cancels the backup; that is not a failure");
        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Information && entry.Message.Contains("shutting down"));
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    private Harness Create(Func<CancellationToken, Task<TriggerBackupResultDto>> backup)
    {
        var root = Path.Combine(Path.GetTempPath(), $"nostos-backup-worker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _roots.Add(root);

        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={Path.Combine(root, "nostos.db")}")
            .Options;
        using (var db = new NostosDbContext(options))
        {
            db.Database.EnsureCreated();
        }

        var environment = new TestEnvironment
        {
            ApplicationName = "Nostos.Backend",
            EnvironmentName = "Test",
            ContentRootPath = root,
            WebRootPath = Path.Combine(root, "wwwroot"),
        };
        var settings = new BackupSettingsProvider(
            environment,
            Options.Create(new BackupSettings { IsEnabled = true, IntervalHours = 1 }));

        var backups = new FakeBackupService(backup);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(options);
        services.AddScoped(sp => new NostosDbContext(sp.GetRequiredService<DbContextOptions<NostosDbContext>>()));
        services.AddSingleton(settings);
        services.AddSingleton<IBackupService>(backups);
        return new Harness(services.BuildServiceProvider(), settings, backups);
    }

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup only.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup only.
            }
        }
    }

    private sealed record Harness(
        ServiceProvider Provider,
        BackupSettingsProvider Settings,
        FakeBackupService BackupService) : IDisposable
    {
        public int BackupCalls => BackupService.Calls;

        public void Dispose() => Provider.Dispose();
    }

    private sealed class TestWorker(
        IServiceProvider provider,
        BackupSettingsProvider settingsProvider,
        ILogger<BackupWorker> logger,
        TestTimeProvider clock)
        : BackupWorker(provider.GetRequiredService<IServiceScopeFactory>(), settingsProvider, logger)
    {
        protected override TimeProvider Clock => clock;

        public Task RunAsync(CancellationToken stoppingToken) => ExecuteAsync(stoppingToken);
    }

    private sealed class FakeBackupService(
        Func<CancellationToken, Task<TriggerBackupResultDto>> createBackup) : IBackupService
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<TriggerBackupResultDto> CreateBackupAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return createBackup(ct);
        }

        public Task<BackupStatusDto> GetStatusAsync() => throw new NotSupportedException();
        public Task<BackupSettingsDto> GetSettingsAsync() => throw new NotSupportedException();
        public Task<BackupSettingsDto> UpdateSettingsAsync(UpdateBackupSettingsDto dto) => throw new NotSupportedException();
        public Task<RestoreResultDto> RestoreBackupAsync(Guid backupId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<BackupHistoryDto>> GetHistoryAsync() => throw new NotSupportedException();
        public Task DeleteBackupRecordAsync(Guid id) => throw new NotSupportedException();
        public string? GetLocalArchivePath(Guid id) => throw new NotSupportedException();
        public Task<List<BackupHistoryDto>> ImportExistingBackupsAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = string.Empty;
        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } =
            new PhysicalFileProvider(Path.GetTempPath());

        public string WebRootPath { get; set; } = Path.GetTempPath();

        public IFileProvider WebRootFileProvider { get; set; } =
            new PhysicalFileProvider(Path.GetTempPath());
    }
}

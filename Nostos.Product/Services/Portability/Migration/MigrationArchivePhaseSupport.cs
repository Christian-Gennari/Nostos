using System.Text.Json;
using System.Threading.Channels;

namespace Nostos.Backend.Services.Portability.Migration;

/// <summary>
/// Test-only seams for the archive phase handlers. Production never registers
/// this type, so every lookup returns null and no callback runs.
/// </summary>
internal sealed class MigrationArchivePhaseTestHooks
{
    /// <summary>Runs after the export hash/pre-validation and before the pre-rename lease fence.</summary>
    internal Action? BeforeExportRename { get; set; }

    /// <summary>Runs after the export rename and before the fenced row update.</summary>
    internal Action<string>? AfterExportRename { get; set; }
}

/// <summary>
/// Bridges #678 archive-engine progress into durable migration-job progress for
/// one long phase. Reports are pumped off the engine's synchronous callback:
/// the pump never blocks the archive loop on a database round-trip, keeps only
/// the latest pending value, and is flushed by the owning handler before it
/// publishes or returns. A maintenance request cancels the phase's linked token
/// at the callback itself, so long archive IO yields at its next engine check.
/// Every write still goes through <see cref="MigrationPhaseContext.ReportProgressAsync"/>,
/// which is lease-fenced; a lost lease cancels the phase and the successor
/// restarts from durable facts.
/// </summary>
internal sealed class MigrationArchiveProgressPump : IProgress<PortableArchiveProgress>, IAsyncDisposable
{
    private readonly MigrationPhaseContext _context;
    private readonly MigrationProgressPhase _phase;
    private readonly Channel<PortableArchiveProgress> _channel = Channel.CreateBounded<PortableArchiveProgress>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Task _pump;

    internal MigrationArchiveProgressPump(MigrationPhaseContext context, MigrationProgressPhase phase)
    {
        _context = context;
        _phase = phase;
        _pump = Task.Run(PumpAsync);
    }

    public void Report(PortableArchiveProgress value)
    {
        _context.RequestYieldToMaintenance();
        _channel.Writer.TryWrite(value);
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _pump.ConfigureAwait(false);
    }

    private async Task PumpAsync()
    {
        await foreach (var value in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await _context.ReportProgressAsync(
                    new MigrationProgress(
                        _phase,
                        value.BytesProcessed,
                        value.TotalBytes,
                        value.TotalItems is null ? null : value.ItemsProcessed,
                        value.TotalItems),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (MigrationJobStoreException)
            {
                // ReportProgressAsync already cancelled the phase token. The
                // long operation stops at its next cancellation check and the
                // successor rebuilds from durable facts; never rewrite history.
            }
            catch (OperationCanceledException)
            {
                // Maintenance/heartbeat cancellation; the engine observes the
                // cancelled token at its next check.
            }
        }
    }
}

internal static class MigrationPreparedMetadata
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    internal static string Serialize(PreparedPortableImportMetadata metadata) =>
        JsonSerializer.Serialize(metadata, JsonOptions);

    internal static PreparedPortableImportMetadata? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<PreparedPortableImportMetadata>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Compares the durable identity of a rebuilt prepared import with the
    /// descriptor persisted on the job. Timestamps are not part of the identity:
    /// only what binds the staging area to the sealed archive is compared.
    /// </summary>
    internal static bool Matches(PreparedPortableImportMetadata rebuilt, PreparedPortableImportMetadata persisted) =>
        rebuilt.StagingId == persisted.StagingId
        && rebuilt.FormatVersion == persisted.FormatVersion
        && rebuilt.DataVersion == persisted.DataVersion
        && rebuilt.DataBytes == persisted.DataBytes
        && string.Equals(rebuilt.DataSha256, persisted.DataSha256, StringComparison.OrdinalIgnoreCase)
        && rebuilt.Counts == persisted.Counts
        && rebuilt.MediaFiles == persisted.MediaFiles
        && rebuilt.MediaBytes == persisted.MediaBytes
        && rebuilt.ArchiveBytes == persisted.ArchiveBytes;
}

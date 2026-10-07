using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Migration;
using static Nostos.Backend.Tests.Portability.PortableArchiveTestSupport;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Real-exporter fixtures and archive mutation helpers for the Slice 9/10
/// engine tests. Every archive is produced by the production exporter from the
/// representative library and then (for legacy/invalid cases) rewritten at the
/// ZIP entry level with the data descriptor recomputed exactly.
/// </summary>
internal static class MigrationArchiveJobTestSupport
{
    internal const string ManifestPath = "manifest.json";
    internal const string DataPath = "data/library.json";

    internal static async Task<byte[]> ExportRepresentativeAsync()
    {
        await using var source = await LocalPortableTestLibrary.CreateAsync();
        await PortableArchiveTestSupport.PopulateRepresentativeAsync(source.Db, source.Storage);
        using var bytes = new MemoryStream();
        await source.Portability().ExportAsync(bytes);
        return bytes.ToArray();
    }

    /// <summary>
    /// Rewrites a current archive as the requested legacy data version by
    /// removing the fields that version predates and recomputing the data
    /// descriptor carried in the manifest.
    /// </summary>
    internal static byte[] ToDataVersion(byte[] archive, int dataVersion)
    {
        var entries = ReadEntries(archive);
        MutateJsonEntry(entries, ManifestPath, root =>
        {
            root["formatVersion"] = 1;
            root["dataVersion"] = dataVersion;
        });
        MutateJsonEntry(entries, DataPath, root =>
        {
            root["version"] = dataVersion;
            if (dataVersion <= 2)
            {
                root.Remove("noteImportBookLinks");
            }

            if (dataVersion <= 1)
            {
                root.Remove("writingNotes");
            }
        });
        RehashDataDescriptor(entries);
        return BuildArchive(entries);
    }

    /// <summary>Replaces the relational payload with invalid JSON so preparation fails typed.</summary>
    internal static byte[] CorruptData(byte[] archive)
    {
        var entries = ReadEntries(archive);
        var index = entries.FindIndex(x => x.Name == DataPath);
        entries[index] = new TestArchiveEntry(DataPath, Encoding.UTF8.GetBytes("{ this is not valid json"));
        RehashDataDescriptor(entries);
        return BuildArchive(entries);
    }

    internal static int CountStagingAreas(MigrationEngineHarness harness)
    {
        var root = harness.Paths.GetStagingRoot();
        return Directory.Exists(root)
            ? Directory.EnumerateDirectories(root)
                .Count(directory => Guid.TryParseExact(Path.GetFileName(directory), "N", out _))
            : 0;
    }

    internal static string? SingleStagingDirectory(MigrationEngineHarness harness)
    {
        var root = harness.Paths.GetStagingRoot();
        return Directory.Exists(root)
            ? Directory.EnumerateDirectories(root)
                .Single(directory => Guid.TryParseExact(Path.GetFileName(directory), "N", out _))
            : null;
    }

    internal static int CountExportFiles(MigrationEngineHarness harness, Guid jobId)
    {
        var directory = harness.Paths.GetExportDirectory(jobId);
        return Directory.Exists(directory) ? Directory.EnumerateFiles(directory).Count() : 0;
    }

    /// <summary>
    /// Drives worker cycles until the job reaches <paramref name="expected"/>.
    /// One <c>RunCycleAsync</c> can yield at a load checkpoint (the worker's
    /// admission gate, lease or a database busy timeout can bounce under CI
    /// load), so tests must not assume a single call is enough. Every end-state
    /// assertion still runs; only the number of cycles needed is made
    /// load-independent.
    /// </summary>
    internal static async Task RunToStateAsync(
        MigrationEngineHarness harness,
        Guid jobId,
        MigrationJobState expected,
        MigrationJobWorker? worker = null)
    {
        const int maxCycles = 20;
        worker ??= harness.Worker;
        for (var cycle = 0; cycle < maxCycles; cycle++)
        {
            await worker.RunCycleAsync(default);
            var job = await harness.WithJobs(s => s.GetAsync(jobId, default));
            if (job!.State == expected)
            {
                return;
            }
        }

        var last = await harness.WithJobs(s => s.GetAsync(jobId, default));
        last!.State.Should().Be(expected,
            $"the worker must reach {expected} within {maxCycles} bounded cycles");
    }

    internal static FileStorageService CreateFileStorage(string root) =>
        new(
            new PortableTestWebHostEnvironment(root),
            Microsoft.Extensions.Options.Options.Create(new Nostos.Backend.Configuration.FileStorageOptions
            {
                BooksRoot = Path.Combine(root, "books"),
            }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FileStorageService>.Instance);

    /// <summary>Representative seeded library used by export tests.</summary>
    internal static Task<PortableFixtureIds> SeedRepresentativeAsync(
        MigrationEngineHarness harness,
        IBookAssetStorage storage) =>
        harness.WithDb(db => PortableArchiveTestSupport.PopulateRepresentativeAsync(db, storage));
}

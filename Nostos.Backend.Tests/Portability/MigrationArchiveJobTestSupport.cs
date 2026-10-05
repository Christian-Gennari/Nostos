using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Migration;

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
        MutateJson(entries, ManifestPath, root =>
        {
            root["formatVersion"] = 1;
            root["dataVersion"] = dataVersion;
        });
        MutateJson(entries, DataPath, root =>
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

    internal static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
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

    internal static List<TestArchiveEntry> ReadEntries(byte[] archiveBytes)
    {
        using var source = new MemoryStream(archiveBytes, writable: false);
        using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: false);
        var entries = new List<TestArchiveEntry>();
        foreach (var entry in archive.Entries)
        {
            using var input = entry.Open();
            using var output = new MemoryStream();
            input.CopyTo(output);
            entries.Add(new TestArchiveEntry(entry.FullName, output.ToArray()));
        }

        return entries;
    }

    internal static byte[] BuildArchive(IEnumerable<TestArchiveEntry> entries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var item in entries)
            {
                var entry = archive.CreateEntry(item.Name, CompressionLevel.NoCompression);
                using var target = entry.Open();
                target.Write(item.Bytes);
            }
        }

        return output.ToArray();
    }

    private static void MutateJson(
        List<TestArchiveEntry> entries,
        string name,
        Action<JsonObject> mutate)
    {
        var index = entries.FindIndex(x => string.Equals(x.Name, name, StringComparison.Ordinal));
        var root = JsonNode.Parse(entries[index].Bytes)?.AsObject()
            ?? throw new InvalidDataException($"Archive entry '{name}' is not a JSON object.");
        mutate(root);
        entries[index] = new TestArchiveEntry(name, Encoding.UTF8.GetBytes(root.ToJsonString()));
    }

    private static void RehashDataDescriptor(List<TestArchiveEntry> entries)
    {
        var data = entries.Single(x => string.Equals(x.Name, DataPath, StringComparison.Ordinal)).Bytes;
        var manifestIndex = entries.FindIndex(x => string.Equals(x.Name, ManifestPath, StringComparison.Ordinal));
        var manifest = JsonNode.Parse(entries[manifestIndex].Bytes)!.AsObject();
        var descriptor = manifest["data"]!.AsObject();
        descriptor["length"] = data.LongLength;
        descriptor["sha256"] = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        entries[manifestIndex] = new TestArchiveEntry(ManifestPath, Encoding.UTF8.GetBytes(manifest.ToJsonString()));
    }

    internal sealed record TestArchiveEntry(string Name, byte[] Bytes);

    /// <summary>Representative seeded library used by export tests.</summary>
    internal static Task<PortableFixtureIds> SeedRepresentativeAsync(
        MigrationEngineHarness harness,
        IBookAssetStorage storage) =>
        harness.WithDb(db => PortableArchiveTestSupport.PopulateRepresentativeAsync(db, storage));
}

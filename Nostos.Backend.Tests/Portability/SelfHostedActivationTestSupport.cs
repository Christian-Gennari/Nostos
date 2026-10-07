using System.Security.Cryptography;
using Nostos.Backend.Services.Portability;

namespace Nostos.Backend.Tests.Portability;

internal static class SelfHostedActivationTestSupport
{
    internal static async Task<byte[]> ExportArchiveAsync(LocalPortableTestLibrary library)
    {
        using var destination = new MemoryStream();
        await library.Portability().ExportAsync(destination);
        return destination.ToArray();
    }

    internal static async Task<PortablePreparedImport> PrepareStagedAsync(
        byte[] archiveBytes,
        InMemoryPortableImportStaging staging)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"nostos-activation-archive-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "archive.nostos");
        try
        {
            await File.WriteAllBytesAsync(path, archiveBytes);
            await using var source = new FilePortableArchiveSource(path);
            var reader = new PortableArchiveReader();
            return await reader.PrepareImportAsync(source, staging);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Test cleanup only.
            }
        }
    }

    internal static Dictionary<string, string> SnapshotTree(string root)
    {
        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(root))
        {
            return snapshot;
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            snapshot[Path.GetRelativePath(root, file)] =
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        }

        return snapshot;
    }

    internal static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        return output.ToArray();
    }
}

using Nostos.Product.Storage;

namespace Nostos.Backend.Tests.Support;

/// <summary>
/// A scratch root that records every path the product staged under it, so a
/// test can prove where a staging path came from (issue #1596) as well as that
/// the caller deleted it afterwards.
/// </summary>
internal sealed class RecordingProductScratchStorage(string root) : IProductScratchStorage
{
    private readonly ProductScratchStorage _inner = new(root);

    public string Root => _inner.Root;

    public List<string> Directories { get; } = [];

    public List<string> Files { get; } = [];

    public string CreateDirectory(string purpose)
    {
        var path = _inner.CreateDirectory(purpose);
        Directories.Add(path);
        return path;
    }

    public string CreateFilePath(string purpose, string extension)
    {
        var path = _inner.CreateFilePath(purpose, extension);
        Files.Add(path);
        return path;
    }

    /// <summary>What is still on disk under the root; empty after cleanup.</summary>
    public string[] Leftovers() =>
        Directory.Exists(Root) ? Directory.GetFileSystemEntries(Root) : [];
}

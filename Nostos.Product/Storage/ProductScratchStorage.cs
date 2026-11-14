namespace Nostos.Product.Storage;

/// <summary>
/// Where the product stages bytes that are large and transient: a whole
/// publication copied out of asset storage for extraction, the EPUB an MCP
/// client's passage is anchored in, a provider's bulk catalogue snapshot, a
/// portable archive's import spool (issue #1596).
///
/// A host registers this seam to put that scratch on the volume that holds its
/// library. The default is <see cref="Path.GetTempPath"/>, which is only safe
/// where <c>/tmp</c> is real disk: on Linux it is frequently a RAM-backed
/// tmpfs, and a 512 MiB source publication staged there can exhaust host memory
/// and get the process OOM-killed. Acquisition staging already refuses that
/// trade (<see cref="Nostos.Backend.Providers.Acquisition.AcquisitionOptions.ResolveWorkingRoot"/>);
/// this is the same decision for everything else the product stages.
///
/// Register it before <c>AddNostosProduct</c> (or replace it after) to win over
/// the product default. <see cref="Root"/> is the only placement decision;
/// callers still delete what they create.
/// </summary>
public interface IProductScratchStorage
{
    /// <summary>
    /// The directory scratch entries are created under. It exists after the
    /// first <see cref="CreateDirectory"/> or <see cref="CreateFilePath"/> call.
    /// </summary>
    string Root { get; }

    /// <summary>
    /// A new, empty, uniquely named directory under <see cref="Root"/>. The
    /// caller owns it and deletes it when the work that needed it ends.
    /// </summary>
    string CreateDirectory(string purpose);

    /// <summary>
    /// A unique, not yet created file path under <see cref="Root"/>. The caller
    /// creates the file and deletes it when the work that needed it ends.
    /// </summary>
    string CreateFilePath(string purpose, string extension);
}

/// <summary>
/// Scratch confined to one directory, named by the host.
///
/// Every entry is <c>{purpose}-{guid}</c> directly under <see cref="Root"/>, so
/// an existing sweep that matches a fixed prefix (the legacy portability
/// scratch sweep) still finds what it owns and nothing else.
/// </summary>
public sealed class ProductScratchStorage : IProductScratchStorage
{
    public ProductScratchStorage(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    /// <summary>
    /// The product default: the process temp path. Kept so a host that has not
    /// adopted the seam yet — Nostos-Cloud today — keeps exactly the placement
    /// it had before the seam existed.
    /// </summary>
    public static ProductScratchStorage TempPath { get; } = new(Path.GetTempPath());

    public string Root { get; }

    public string CreateDirectory(string purpose)
    {
        EnsureRoot();
        var path = Path.Combine(Root, $"{Name(purpose)}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    public string CreateFilePath(string purpose, string extension)
    {
        EnsureRoot();
        return Path.Combine(
            Root,
            $"{Name(purpose)}-{Guid.NewGuid():N}{NormalizeExtension(extension)}");
    }

    private void EnsureRoot() => Directory.CreateDirectory(Root);

    private static string Name(string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        return purpose;
    }

    private static string NormalizeExtension(string? extension)
    {
        if (string.IsNullOrEmpty(extension))
            return string.Empty;

        return extension.StartsWith('.') ? extension : $".{extension}";
    }
}

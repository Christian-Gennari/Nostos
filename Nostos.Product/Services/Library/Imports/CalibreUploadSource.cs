namespace Nostos.Backend.Services.Library.Imports;

/// <summary>
/// A Calibre library that arrived as a zipped upload and was safely extracted
/// into the owner's scratch area (issue #1572). It implements the same source
/// seam as the mounted folder, so the preview, matcher, commit and undo are
/// the existing Calibre import unchanged.
/// </summary>
public sealed class CalibreUploadSource(CalibreUploadService uploads) : ICalibreLibrarySource
{
    public const string SourceKind = "calibre";

    public string Kind => SourceKind;

    /// <summary>The upload path exists on this host; a library may not be uploaded yet.</summary>
    public bool IsConfigured => true;

    public bool IsAvailable => uploads.HasStagedLibrary;

    /// <summary>
    /// Asks the scratch store instead of the process cache. The background
    /// import runs in a fresh scope where the request's cache is not present.
    /// </summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct) =>
        await uploads.GetStagedLibraryAsync(ct) is not null;

    public string? CoverExtension => ".jpg";

    public async Task<CalibreSourceLibrary> ReadAsync(CancellationToken ct)
    {
        var staged = await uploads.GetStagedLibraryAsync(ct)
            ?? throw new CalibreLibraryException(
                "No uploaded Calibre library is ready. Upload a zipped library first.");
        return await new CalibreLibraryReader(staged.RootPath).ReadAsync(ct);
    }

    public Stream? OpenRead(CalibreSourceFormat format) =>
        Reader()?.OpenRead(format);

    public Stream? OpenCover(CalibreSourceBook book) =>
        Reader()?.OpenCover(book);

    /// <summary>The upload is only needed while an import runs; remove it afterwards.</summary>
    public Task ReleaseAfterCommitAsync(CancellationToken ct) => uploads.DiscardCurrentAsync(ct);

    private CalibreLibraryReader? Reader()
    {
        var staged = uploads.CachedStaged;
        return staged is null ? null : new CalibreLibraryReader(staged.RootPath);
    }
}

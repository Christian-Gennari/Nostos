using Nostos.Backend.Services.Library.Imports;

namespace Nostos.Backend.Services.Calibre;

/// <summary>
/// Chooses the library the import reads: a configured folder wins, because
/// SelfHosted keeps that as its primary path; otherwise the uploaded library
/// (#1572). Both are read by <see cref="CalibreLibraryReader"/>, so the two
/// paths cannot drift apart.
/// </summary>
public sealed class CalibreImportSourceRouter(
    CalibreFolderSource folder,
    CalibreUploadSource upload) : ICalibreLibrarySource
{
    private ICalibreLibrarySource Active =>
        folder.IsAvailable ? folder
        : upload.IsAvailable ? upload
        : folder;

    public string Kind => CalibreFolderSource.SourceKind;

    public bool IsConfigured => folder.IsConfigured || upload.IsConfigured;

    public bool IsAvailable => folder.IsAvailable || upload.IsAvailable;

    public string? CoverExtension => Active.CoverExtension;

    public async Task<CalibreSourceLibrary> ReadAsync(CancellationToken ct)
    {
        if (folder.IsAvailable)
            return await folder.ReadAsync(ct);
        if (await upload.IsAvailableAsync(ct))
            return await upload.ReadAsync(ct);
        return await folder.ReadAsync(ct);
    }

    public Stream? OpenRead(CalibreSourceFormat format) => Active.OpenRead(format);

    public Stream? OpenCover(CalibreSourceBook book) => Active.OpenCover(book);

    public Task ReleaseAfterCommitAsync(CancellationToken ct) => Active.ReleaseAfterCommitAsync(ct);
}

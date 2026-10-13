using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Services.ExternalMedia;

/// <summary>
/// Book storage that also serves media Nostos does not own.
///
/// Everything Nostos owns is delegated unchanged to the storage it wraps.
/// A book file or track that the wrapped storage does not hold is looked up
/// as an external reference and, when one exists, read in place from the
/// external root.
///
/// The invariant this type exists for: no member reaches a source file with
/// anything but a read. Saving stores a Nostos-owned copy and then forgets
/// the reference it replaces; deleting forgets references and removes only
/// what the wrapped storage holds.
/// </summary>
public sealed class ExternalMediaBookStorage(
    IBookAssetStorage assets,
    IBookTrackStorage tracks,
    ExternalMediaRoot root,
    ExternalMediaLinkStore links) : IBookAssetStorage, IBookTrackStorage
{
    // ------------------------------------------------------------------
    // Primary file
    // ------------------------------------------------------------------

    public async Task<StoredAssetInfo?> GetBookFileInfoAsync(Guid bookId, CancellationToken ct = default) =>
        await assets.GetBookFileInfoAsync(bookId, ct)
        ?? await ExternalInfoAsync(bookId, ExternalMediaLinkModel.PrimaryFile, ct);

    public async Task<StoredAssetRead?> OpenBookFileAsync(
        Guid bookId,
        StorageByteRange? range = null,
        CancellationToken ct = default) =>
        await assets.OpenBookFileAsync(bookId, range, ct)
        ?? await OpenExternalAsync(bookId, ExternalMediaLinkModel.PrimaryFile, range, ct);

    public async Task<string> SaveBookFileAsync(
        Guid bookId,
        Stream content,
        string fileName,
        CancellationToken ct = default)
    {
        var stored = await assets.SaveBookFileAsync(bookId, content, fileName, ct);
        await links.RemovePrimaryAsync(bookId, ct);
        return stored;
    }

    public async Task<string> AdoptBookFileAsync(
        Guid bookId,
        string sourcePath,
        string fileName,
        CancellationToken ct = default)
    {
        var stored = await assets.AdoptBookFileAsync(bookId, sourcePath, fileName, ct);
        await links.RemovePrimaryAsync(bookId, ct);
        return stored;
    }

    public async Task<bool> DeleteBookFileAsync(Guid bookId, CancellationToken ct = default)
    {
        var unlinked = await links.RemovePrimaryAsync(bookId, ct);
        var deleted = await assets.DeleteBookFileAsync(bookId, ct);
        return unlinked || deleted;
    }

    public async Task DeleteBookFilesAsync(Guid bookId, CancellationToken ct = default)
    {
        // Normally the book row is already gone and took its references with
        // it; this covers a caller that clears a book's media but keeps the row.
        await links.RemoveAllAsync(bookId, ct);
        await assets.DeleteBookFilesAsync(bookId, ct);
    }

    // ------------------------------------------------------------------
    // Covers are always Nostos-owned
    // ------------------------------------------------------------------

    public Task<string> SaveBookCoverAsync(Guid bookId, Stream content, string fileName, CancellationToken ct = default) =>
        assets.SaveBookCoverAsync(bookId, content, fileName, ct);

    public Task<StoredAssetInfo?> GetBookCoverInfoAsync(Guid bookId, CancellationToken ct = default) =>
        assets.GetBookCoverInfoAsync(bookId, ct);

    public Task<StoredAssetRead?> OpenBookCoverAsync(Guid bookId, CancellationToken ct = default) =>
        assets.OpenBookCoverAsync(bookId, ct);

    public Task<StoredAssetInfo?> GetBookCoverThumbnailInfoAsync(Guid bookId, int width, CancellationToken ct = default) =>
        assets.GetBookCoverThumbnailInfoAsync(bookId, width, ct);

    public Task<StoredAssetRead?> OpenBookCoverThumbnailAsync(Guid bookId, int width, CancellationToken ct = default) =>
        assets.OpenBookCoverThumbnailAsync(bookId, width, ct);

    public Task<bool> DeleteCoverAsync(Guid bookId, CancellationToken ct = default) =>
        assets.DeleteCoverAsync(bookId, ct);

    // ------------------------------------------------------------------
    // Tracks
    // ------------------------------------------------------------------

    public async Task<StoredAssetInfo?> GetTrackInfoAsync(Guid bookId, int number, CancellationToken ct = default) =>
        await tracks.GetTrackInfoAsync(bookId, number, ct)
        ?? (IsTrackNumber(number) ? await ExternalInfoAsync(bookId, number, ct) : null);

    public async Task<StoredAssetRead?> OpenTrackAsync(
        Guid bookId,
        int number,
        StorageByteRange? range = null,
        CancellationToken ct = default) =>
        await tracks.OpenTrackAsync(bookId, number, range, ct)
        ?? (IsTrackNumber(number) ? await OpenExternalAsync(bookId, number, range, ct) : null);

    public async Task<string> SaveTrackAsync(
        Guid bookId,
        int number,
        Stream content,
        string fileName,
        CancellationToken ct = default)
    {
        var stored = await tracks.SaveTrackAsync(bookId, number, content, fileName, ct);
        await links.RemoveTrackAsync(bookId, number, ct);
        return stored;
    }

    public async Task<string> AdoptTrackAsync(
        Guid bookId,
        int number,
        string sourcePath,
        string fileName,
        CancellationToken ct = default)
    {
        var stored = await tracks.AdoptTrackAsync(bookId, number, sourcePath, fileName, ct);
        await links.RemoveTrackAsync(bookId, number, ct);
        return stored;
    }

    public async Task DeleteTracksAsync(Guid bookId, CancellationToken ct = default)
    {
        await links.RemoveTracksAsync(bookId, ct);
        await tracks.DeleteTracksAsync(bookId, ct);
    }

    // ------------------------------------------------------------------
    // External reads
    // ------------------------------------------------------------------

    private static bool IsTrackNumber(int number) => number is >= 1 and <= BookTrackList.MaxTracks;

    private async Task<StoredAssetInfo?> ExternalInfoAsync(Guid bookId, int trackNumber, CancellationToken ct)
    {
        if (!root.IsConfigured
            || await links.FindAsync(bookId, trackNumber, ct) is not { } link
            || root.Find(link.RootId, link.RelativePath) is not { } file)
            return null;

        return Describe(link, file.Length, file.LastModified);
    }

    private async Task<StoredAssetRead?> OpenExternalAsync(
        Guid bookId,
        int trackNumber,
        StorageByteRange? range,
        CancellationToken ct)
    {
        if (!root.IsConfigured
            || await links.FindAsync(bookId, trackNumber, ct) is not { } link
            || root.OpenRead(link.RootId, link.RelativePath) is not { } stream)
            return null;

        try
        {
            // Described from the open handle, so the length a range is
            // checked against is the length of the file actually being read.
            var info = Describe(
                link,
                stream.Length,
                new DateTimeOffset(File.GetLastWriteTimeUtc(stream.SafeFileHandle), TimeSpan.Zero));
            if (range is { } requested)
            {
                if (requested.EndInclusive >= info.Length)
                    throw new ArgumentOutOfRangeException(nameof(range));
                stream.Seek(requested.Start, SeekOrigin.Begin);
            }

            return new StoredAssetRead(info, stream, range);
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Presents a source file under the name an owned copy would have, so
    /// neither the folder layout nor the original file name leaves the server.
    /// </summary>
    private static StoredAssetInfo Describe(ExternalMediaLinkModel link, long length, DateTimeOffset lastModified)
    {
        var extension = Path.GetExtension(link.RelativePath).ToLowerInvariant();
        var fileName = link.TrackNumber == ExternalMediaLinkModel.PrimaryFile
            ? $"book{extension}"
            : BookTrackFormats.CanonicalFileName(link.TrackNumber, extension);

        return new StoredAssetInfo(
            FileName: fileName,
            ContentType: MediaTypeMap.ForBookFile(fileName),
            Length: length,
            EntityTag: $"\"{length:x}-{lastModified.UtcTicks:x}\"",
            LastModified: lastModified);
    }
}

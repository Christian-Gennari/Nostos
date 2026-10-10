namespace Nostos.Backend.Services.Portability;

/// <summary>
/// The one place portable code turns a media descriptor (book, kind, canonical
/// file name) into a storage call.
///
/// Every caller used to choose between "book" and "cover" with a two-way
/// branch, which would have treated any third kind as a cover. Routing through
/// here means an unknown kind is an error everywhere, and a track reaches
/// track storage everywhere.
/// </summary>
internal static class PortableMediaStorage
{
    public const string TrackStorageUnavailableCode = "track_storage_unavailable";

    public static Task<StoredAssetInfo?> GetInfoAsync(
        IBookAssetStorage assets,
        IBookTrackStorage? tracks,
        Guid bookId,
        string kind,
        string? fileName,
        CancellationToken ct) =>
        kind switch
        {
            PortableArchiveFormat.BookMediaKind => assets.GetBookFileInfoAsync(bookId, ct),
            PortableArchiveFormat.CoverMediaKind => assets.GetBookCoverInfoAsync(bookId, ct),
            PortableArchiveFormat.TrackMediaKind =>
                RequireTracks(tracks).GetTrackInfoAsync(bookId, TrackNumber(fileName), ct),
            _ => throw UnsupportedKind(kind),
        };

    public static Task<StoredAssetRead?> OpenAsync(
        IBookAssetStorage assets,
        IBookTrackStorage? tracks,
        Guid bookId,
        string kind,
        string? fileName,
        CancellationToken ct) =>
        kind switch
        {
            PortableArchiveFormat.BookMediaKind => assets.OpenBookFileAsync(bookId, null, ct),
            PortableArchiveFormat.CoverMediaKind => assets.OpenBookCoverAsync(bookId, ct),
            PortableArchiveFormat.TrackMediaKind =>
                RequireTracks(tracks).OpenTrackAsync(bookId, TrackNumber(fileName), null, ct),
            _ => throw UnsupportedKind(kind),
        };

    public static Task SaveAsync(
        IBookAssetStorage assets,
        IBookTrackStorage? tracks,
        Guid bookId,
        string kind,
        string fileName,
        Stream content,
        CancellationToken ct) =>
        kind switch
        {
            PortableArchiveFormat.BookMediaKind => assets.SaveBookFileAsync(bookId, content, fileName, ct),
            PortableArchiveFormat.CoverMediaKind => assets.SaveBookCoverAsync(bookId, content, fileName, ct),
            PortableArchiveFormat.TrackMediaKind =>
                RequireTracks(tracks).SaveTrackAsync(bookId, TrackNumber(fileName), content, fileName, ct),
            _ => throw UnsupportedKind(kind),
        };

    /// <summary>Whether storing or reading this kind needs track storage this host may not have.</summary>
    public static bool IsAvailable(IBookTrackStorage? tracks, string kind) =>
        kind != PortableArchiveFormat.TrackMediaKind || tracks is not null;

    private static int TrackNumber(string? fileName) =>
        fileName is not null && BookTrackFormats.TryParseCanonicalFileName(fileName.ToLowerInvariant(), out var number)
            ? number
            : throw new PortableArchiveException(
                "invalid_media_filename",
                "A track media item does not have a canonical track file name.");

    private static IBookTrackStorage RequireTracks(IBookTrackStorage? tracks) =>
        tracks ?? throw new PortableArchiveException(
            TrackStorageUnavailableCode,
            "This server cannot store multi-track audiobooks.");

    private static PortableArchiveException UnsupportedKind(string kind) =>
        new("invalid_media_kind", $"Portable media kind '{kind}' is not supported.");
}

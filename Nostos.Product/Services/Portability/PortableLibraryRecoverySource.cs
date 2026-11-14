using System.Text.Json;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Services.Portability;

/// <summary>
/// One primary (book or cover) media item of a retained recovery copy as
/// discovered under the retained media root. Only the classification facts are
/// carried; the relative path never leaves the host.
/// </summary>
public sealed record PortableRecoveryMedia(
    Guid BookId,
    string Kind,
    string FileName,
    long Length,
    string Sha256);

/// <summary>
/// The expected portable relational state extracted from a retained recovery
/// database, bound to the byte length and SHA-256 of the exact serialized
/// payload that was read. <see cref="Data"/> is deliberately internal: hosts
/// consume the payload through <see cref="PortableLibraryRecoverySource"/> and
/// verify it through
/// <see cref="PortableLibraryVerifier.VerifyDatabaseAgainstExpectedAsync(NostosDbContext, PortableRecoveryPayload, CancellationToken)"/>.
/// </summary>
public sealed class PortableRecoveryPayload
{
    internal PortableRecoveryPayload(
        PortableLibraryData data,
        long dataBytes,
        string dataSha256,
        MigrationArchiveCounts counts,
        IReadOnlyList<PortableArchiveMediaEntry> primaryMedia)
    {
        Data = data;
        DataBytes = dataBytes;
        DataSha256 = dataSha256;
        Counts = counts;
        PrimaryMedia = primaryMedia;
    }

    internal PortableLibraryData Data { get; }

    public long DataBytes { get; }

    public string DataSha256 { get; }

    public MigrationArchiveCounts Counts { get; }

    /// <summary>Canonical book/cover descriptors in the archive layout.</summary>
    public IReadOnlyList<PortableArchiveMediaEntry> PrimaryMedia { get; }
}

/// <summary>
/// Extracts the portable relational state of a migrated recovery database into
/// the same camelCase JSON payload the portable archive uses, so a restore
/// materializes the previous library through
/// <see cref="PortableLibraryRelationalRestore"/> — the identical code path a
/// normal import uses. The caller owns the database schema level (migrate a
/// working copy forward first) and the destination stream; the retained
/// original is never opened for writing.
/// </summary>
/// <remarks>
/// Media presence flags and filenames are derived from the verified retained
/// media descriptors, never from the recovery database's own file-details
/// columns, so the materialized candidate always describes media that actually
/// exists in the retained copy.
/// </remarks>
public static class PortableLibraryRecoverySource
{
    public static async Task<PortableRecoveryPayload> WriteRelationalPayloadAsync(
        NostosDbContext db,
        Stream destination,
        IReadOnlyList<PortableRecoveryMedia> retainedMedia,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(retainedMedia);

        var data = await ReadPortableDataAsync(db, retainedMedia, ct).ConfigureAwait(false);
        var primary = BuildPrimaryMedia(retainedMedia);
        await using var hashing = new HashingWriteStream(destination);
        await JsonSerializer
            .SerializeAsync(hashing, data, PortableArchiveJson.Options, ct)
            .ConfigureAwait(false);
        await hashing.FlushAsync(ct).ConfigureAwait(false);
        var (dataBytes, dataSha256) = hashing.Complete();
        var counts = PortableLibraryCounts.ComputeCounts(data, primary.Count);
        return new PortableRecoveryPayload(data, dataBytes, dataSha256, counts, primary);
    }

    /// <summary>
    /// Reads the retained database through the same projection a normal export
    /// uses. Only the media flags differ: they describe the media this recovery
    /// copy actually retained, never the database's own file-details columns.
    /// </summary>
    private static Task<PortableLibraryData> ReadPortableDataAsync(
        NostosDbContext db,
        IReadOnlyList<PortableRecoveryMedia> retainedMedia,
        CancellationToken ct)
    {
        var booksWithFile = retainedMedia
            .Where(item => string.Equals(item.Kind, PortableArchiveFormat.BookMediaKind, StringComparison.Ordinal))
            .Select(item => item.BookId)
            .ToHashSet();
        var booksWithCover = retainedMedia
            .Where(item => string.Equals(item.Kind, PortableArchiveFormat.CoverMediaKind, StringComparison.Ordinal))
            .Select(item => item.BookId)
            .ToHashSet();

        var retainedTracks = retainedMedia
            .Where(item => string.Equals(item.Kind, PortableArchiveFormat.TrackMediaKind, StringComparison.Ordinal))
            .GroupBy(item => item.BookId)
            .ToDictionary(
                group => group.Key,
                group => group.ToDictionary(item => item.FileName, item => item.Length, StringComparer.Ordinal));

        return PortableLibrarySnapshot.ReadAsync(
            db,
            book => new PortableBookMedia(
                booksWithFile.Contains(book.Id),
                booksWithCover.Contains(book.Id),
                RetainedTracksJson(book, retainedTracks.GetValueOrDefault(book.Id))),
            ct,
            unsupportedBookTypeSubject: "Recovery book");
    }

    private static List<PortableArchiveMediaEntry> BuildPrimaryMedia(
        IReadOnlyList<PortableRecoveryMedia> retainedMedia)
    {
        var primary = new List<PortableArchiveMediaEntry>();
        foreach (var item in retainedMedia)
        {
            if (!PortableArchiveFormat.IsKnownMediaKind(item.Kind)
                || PortableArchiveFormat.CanonicalMediaFileName(item.Kind, item.FileName) is not { } canonicalFileName)
            {
                continue;
            }

            primary.Add(new PortableArchiveMediaEntry(
                item.BookId,
                item.Kind,
                PortableArchiveFormat.MediaPath(item.BookId, canonicalFileName),
                item.FileName,
                string.Empty,
                item.Length,
                item.Sha256));
        }

        return primary;
    }

    /// <summary>
    /// A book's track list, kept only when the retained copy really holds every
    /// track it names at the recorded size. Like the other media flags, this
    /// describes the retained media rather than trusting the database's claim.
    /// </summary>
    private static string? RetainedTracksJson(BookModel book, Dictionary<string, long>? retained)
    {
        var tracks = BookTrackList.Parse(book.FileDetails.TracksJson);
        if (tracks.Count == 0 || retained is null || retained.Count != tracks.Count)
            return null;

        return tracks.All(track => retained.TryGetValue(track.FileName, out var length) && length == track.Bytes)
            ? book.FileDetails.TracksJson
            : null;
    }
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;
using Nostos.Product.BookText;

namespace Nostos.Product.Alignment;

/// <summary>
/// Revision identities derived from actual storage metadata.
/// Unknown identities are represented by null, never by placeholders.
/// </summary>
public static class AlignmentRevisions
{
    public sealed record TrackIdentity(
        int Number,
        string FileName,
        string EntityTag,
        long Length,
        long DurationMs);

    public static string? SingleFile(StoredAssetInfo? info)
    {
        if (info is null ||
            info.Length <= 0 ||
            string.IsNullOrWhiteSpace(info.EntityTag))
            return null;

        return Hash(
            "nostos-alignment-audio-single-v1",
            info.EntityTag,
            Invariant(info.Length));
    }

    public static string? Text(
        Guid bookId,
        StoredAssetInfo? info)
    {
        if (info is null ||
            info.Length <= 0 ||
            string.IsNullOrWhiteSpace(info.EntityTag))
            return null;

        // This is exactly the cache-key construction, including extractor
        // version. Keep the function shared with AlignmentDocumentCache.
        return string.Join(
            ":",
            bookId.ToString("N"),
            info.EntityTag,
            Invariant(info.Length),
            Invariant(info.LastModified.UtcTicks),
            BookTextArtifactSchema.CurrentExtractorVersion);
    }

    public static string? MultiTrack(
        IReadOnlyList<TrackIdentity> tracks)
    {
        if (tracks.Count == 0 ||
            tracks.Count > BookTrackList.MaxTracks)
            return null;

        var fields = new List<string>
        {
            "nostos-alignment-audio-tracks-v1",
            Invariant(tracks.Count),
        };

        for (var i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];

            if (track.Number != i + 1 ||
                track.Length <= 0 ||
                track.DurationMs <= 0 ||
                string.IsNullOrWhiteSpace(track.EntityTag) ||
                !BookTrackFormats.IsCanonicalFileName(
                    track.FileName, track.Number))
                return null;

            fields.Add(Invariant(track.Number));
            fields.Add(track.FileName);
            fields.Add(track.EntityTag);
            fields.Add(Invariant(track.Length));
            fields.Add(Invariant(track.DurationMs));
        }

        return Hash(fields.ToArray());
    }

    private static string Invariant(long value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private static string Hash(params string[] fields)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(
            stream, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var field in fields)
            {
                var bytes = Encoding.UTF8.GetBytes(field);
                writer.Write(bytes.Length);
                writer.Write(bytes);
            }
        }

        return Convert.ToHexString(
            SHA256.HashData(stream.ToArray()))
            .ToLowerInvariant();
    }
}

/// <summary>
/// Resolves an audiobook revision without depending on physical paths,
/// provider URLs, or implementation-specific storage details.
/// </summary>
public interface IAlignmentAudioRevisionProvider
{
    Task<string?> GetAsync(
        AudioBookModel book,
        CancellationToken ct = default);
}

public sealed class AlignmentAudioRevisionProvider(
    IBookAssetStorage assets,
    IServiceProvider services) : IAlignmentAudioRevisionProvider
{
    public async Task<string?> GetAsync(
        AudioBookModel book,
        CancellationToken ct = default)
    {
        // A nonempty but malformed track list must not be interpreted
        // as a single-file audiobook.
        if (!string.IsNullOrWhiteSpace(book.FileDetails.TracksJson))
        {
            var tracks = BookTrackList.Parse(
                book.FileDetails.TracksJson);

            if (BookTrackList.Validate(tracks) is not null)
                return null;

            var storage = services.GetService(
                typeof(IBookTrackStorage)) as IBookTrackStorage;

            if (storage is null)
                return null;

            var identities =
                new List<AlignmentRevisions.TrackIdentity>(
                    tracks.Count);

            foreach (var track in tracks)
            {
                ct.ThrowIfCancellationRequested();

                var info = await storage.GetTrackInfoAsync(
                    book.Id, track.Number, ct);

                if (info is null ||
                    info.Length != track.Bytes ||
                    !string.Equals(
                        info.FileName,
                        track.FileName,
                        StringComparison.Ordinal))
                    return null;

                identities.Add(new AlignmentRevisions.TrackIdentity(
                    track.Number,
                    track.FileName,
                    info.EntityTag,
                    info.Length,
                    track.DurationMs));
            }

            return AlignmentRevisions.MultiTrack(identities);
        }

        var asset = await assets.GetBookFileInfoAsync(
            book.Id, ct);

        return AlignmentRevisions.SingleFile(asset);
    }
}

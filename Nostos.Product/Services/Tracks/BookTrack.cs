using System.Text.Json;

namespace Nostos.Backend.Services;

/// <summary>
/// One stored audio file of a multi-track audiobook.
///
/// A book with tracks has no primary file: the ordered track list IS its
/// media. Every position the rest of the product handles (reading progress,
/// chapters, note anchors, the assistant's timestamp) stays in seconds across
/// the whole book; only playback and delivery translate that into a track and
/// an offset, using the durations recorded here.
///
/// <paramref name="Bytes"/> and <paramref name="Crc32"/> are measured from the
/// bytes that were actually stored. Together they make the layout of the
/// book's download archive computable without reading a single track, which
/// is what lets that archive advertise its length and honour range requests.
/// </summary>
/// <param name="Number">1-based position in the book.</param>
/// <param name="FileName">Bare stored file name, e.g. <c>track-0001.mp3</c>.</param>
public sealed record BookTrack(
    int Number,
    string FileName,
    string ContentType,
    long DurationMs,
    long Bytes,
    uint Crc32,
    string? Title);

/// <summary>
/// The persisted form of a book's track list
/// (<see cref="Data.Models.FileInfoDetails.TracksJson"/>).
/// </summary>
public static class BookTrackList
{
    /// <summary>
    /// A track list is rewritten as a whole and travels in a JSON column, so a
    /// ceiling keeps one book from carrying an unbounded document. LibriVox's
    /// own part limit is far below this.
    /// </summary>
    public const int MaxTracks = 2000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Serialize(IReadOnlyList<BookTrack> tracks) =>
        JsonSerializer.Serialize(tracks, JsonOptions);

    /// <summary>
    /// Returns the ordered tracks, or an empty list for a single-file book.
    /// A stored document that cannot be read is treated as "no tracks" rather
    /// than thrown: every book read goes through here, and one damaged row
    /// must not take the library listing down with it.
    /// </summary>
    public static IReadOnlyList<BookTrack> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            var tracks = JsonSerializer.Deserialize<List<BookTrack>>(json, JsonOptions);
            if (tracks is null || tracks.Count == 0)
                return [];

            tracks.Sort((a, b) => a.Number.CompareTo(b.Number));
            return tracks;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The rules a track list must satisfy before it is persisted: contiguous
    /// numbering from 1, bare canonical file names and positive sizes. Returns
    /// null when the list is valid, otherwise what is wrong with it.
    /// </summary>
    public static string? Validate(IReadOnlyList<BookTrack> tracks)
    {
        if (tracks.Count == 0)
            return "A track list needs at least one track.";
        if (tracks.Count > MaxTracks)
            return $"A book can hold at most {MaxTracks} tracks.";

        for (var i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            if (track.Number != i + 1)
                return "Tracks must be numbered contiguously from 1.";
            if (track.Bytes <= 0 || track.DurationMs <= 0)
                return $"Track {track.Number} has no measured size or duration.";
            if (!BookTrackFormats.IsCanonicalFileName(track.FileName, track.Number))
                return $"Track {track.Number} has a non-canonical stored file name.";
        }

        return null;
    }

    public static long TotalDurationMs(IReadOnlyList<BookTrack> tracks) =>
        tracks.Sum(track => track.DurationMs);

    public static long TotalBytes(IReadOnlyList<BookTrack> tracks) =>
        tracks.Sum(track => track.Bytes);
}

/// <summary>
/// Naming and format policy for stored tracks, shared by every storage
/// provider so they cannot disagree about what a track file is called.
/// </summary>
public static class BookTrackFormats
{
    public static readonly IReadOnlySet<string> TrackExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp3", ".m4a", ".m4b" };

    public static string RequireTrackExtension(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (!TrackExtensions.Contains(extension))
            throw new InvalidOperationException($"Unsupported track file type: {extension}");
        return extension;
    }

    /// <summary>
    /// Every stored track name starts with this. Storage keeps tracks beside a
    /// book's cover rather than in a folder of their own, so the prefix is what
    /// tells a track apart from a primary <c>book.*</c> file with the same
    /// extension.
    /// </summary>
    public const string FileNamePrefix = "track-";

    /// <summary>The one name a track is stored under: prefix, zero-padded number, extension.</summary>
    public static string CanonicalFileName(int number, string extension)
    {
        if (number is < 1 or > BookTrackList.MaxTracks)
            throw new ArgumentOutOfRangeException(nameof(number));
        return $"{FileNamePrefix}{number:D4}{RequireTrackExtension("x" + extension)}";
    }

    public static bool IsCanonicalFileName(string fileName, int number)
    {
        if (number is < 1 or > BookTrackList.MaxTracks || string.IsNullOrEmpty(fileName))
            return false;

        var extension = Path.GetExtension(fileName);
        return TrackExtensions.Contains(extension)
            && string.Equals(
                fileName,
                $"{FileNamePrefix}{number:D4}{extension.ToLowerInvariant()}",
                StringComparison.Ordinal);
    }

    /// <summary>
    /// Parses a canonical stored name back into its track number. Used where
    /// only a name is available (an archive entry, a directory listing).
    /// </summary>
    public static bool TryParseCanonicalFileName(string fileName, out int number)
    {
        number = 0;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (!stem.StartsWith(FileNamePrefix, StringComparison.Ordinal))
            return false;

        var digits = stem[FileNamePrefix.Length..];
        return digits.Length == 4
            && digits.All(char.IsAsciiDigit)
            && int.TryParse(digits, out number)
            && IsCanonicalFileName(fileName, number);
    }

    /// <summary>True for any file that is, by name, a stored track or its in-progress write.</summary>
    public static bool IsTrackFileName(string fileName) =>
        fileName.StartsWith(FileNamePrefix, StringComparison.Ordinal);
}

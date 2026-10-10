using System.Text;
using System.Text.Json;
using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Services;

/// <summary>
/// Writes a multi-track audiobook's Readium audiobook manifest
/// (<c>application/audiobook+json</c>,
/// https://readium.org/webpub-manifest/profiles/audiobook).
///
/// One writer serves both shapes the manifest is published in: packaged, where
/// tracks are archive members addressed by relative path, and streamed, where
/// they are URLs. They differ only in the hrefs passed in, so the two can
/// never describe a different book.
/// </summary>
public static class AudiobookManifest
{
    public const string ManifestMediaType = "application/audiobook+json";
    public const string PackageMediaType = "application/audiobook+zip";
    public const string PackageExtension = ".audiobook";
    public const string ManifestEntryName = "manifest.json";

    private const string Profile = "https://readium.org/webpub-manifest/profiles/audiobook";
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true };

    public static byte[] Write(
        BookModel book,
        IReadOnlyList<BookTrack> tracks,
        Func<BookTrack, string> trackHref,
        string? coverHref,
        string? coverType,
        string? selfHref = null)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, WriterOptions))
        {
            json.WriteStartObject();
            json.WriteString("@context", "https://readium.org/webpub-manifest/context.jsonld");

            json.WriteStartObject("metadata");
            json.WriteString("@type", "http://schema.org/Audiobook");
            json.WriteString("conformsTo", Profile);
            json.WriteString("identifier", $"urn:uuid:{book.Id}");
            json.WriteString("title", book.Title);
            WriteOptional(json, "subtitle", book.Metadata.Subtitle);
            WriteOptional(json, "author", book.Author);
            WriteOptional(json, "narrator", (book as AudioBookModel)?.Narrator);
            WriteOptional(json, "translator", book.Metadata.Translator);
            WriteOptional(json, "language", book.Metadata.Language);
            WriteOptional(json, "publisher", book.Metadata.Publisher);
            WriteOptional(json, "published", book.Metadata.PublishedDate);
            WriteOptional(json, "description", book.Metadata.Description);
            json.WriteNumber("duration", Seconds(BookTrackList.TotalDurationMs(tracks)));
            json.WriteEndObject();

            if (selfHref is not null)
            {
                json.WriteStartArray("links");
                json.WriteStartObject();
                json.WriteString("rel", "self");
                json.WriteString("href", selfHref);
                json.WriteString("type", ManifestMediaType);
                json.WriteEndObject();
                json.WriteEndArray();
            }

            json.WriteStartArray("readingOrder");
            foreach (var track in tracks)
            {
                json.WriteStartObject();
                json.WriteString("href", trackHref(track));
                json.WriteString("type", track.ContentType);
                json.WriteNumber("duration", Seconds(track.DurationMs));
                json.WriteString("title", TrackTitle(track));
                json.WriteEndObject();
            }
            json.WriteEndArray();

            if (coverHref is not null)
            {
                json.WriteStartArray("resources");
                json.WriteStartObject();
                json.WriteString("rel", "cover");
                json.WriteString("href", coverHref);
                json.WriteString("type", coverType ?? "image/jpeg");
                json.WriteEndObject();
                json.WriteEndArray();
            }

            json.WriteStartArray("toc");
            foreach (var track in tracks)
            {
                json.WriteStartObject();
                json.WriteString("href", trackHref(track));
                json.WriteString("title", TrackTitle(track));
                json.WriteEndObject();
            }
            json.WriteEndArray();

            json.WriteEndObject();
        }

        return buffer.ToArray();
    }

    public static string TrackTitle(BookTrack track) =>
        string.IsNullOrWhiteSpace(track.Title) ? $"Track {track.Number}" : track.Title.Trim();

    private static double Seconds(long milliseconds) => Math.Round(milliseconds / 1000d, 3);

    private static void WriteOptional(Utf8JsonWriter json, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            json.WriteString(name, value.Trim());
    }
}

/// <summary>
/// File names for the tracks of a downloaded audiobook: a zero-padded number
/// first, so any file browser or player lists them in listening order, then
/// the track's own title made safe for every common filesystem.
/// </summary>
public static class AudiobookPackageNaming
{
    private const int MaxTitleLength = 80;

    public static string TrackEntryName(BookTrack track, int trackCount)
    {
        var width = Math.Max(2, trackCount.ToString().Length);
        var number = track.Number.ToString().PadLeft(width, '0');
        var extension = Path.GetExtension(track.FileName).ToLowerInvariant();
        var title = SafeName(track.Title, MaxTitleLength);
        return title.Length == 0 ? $"{number}{extension}" : $"{number} - {title}{extension}";
    }

    public static string DownloadBaseName(BookModel book)
    {
        var title = SafeName(book.Title, 100);
        var author = SafeName(book.Author, 60);
        if (title.Length == 0)
            title = "Audiobook";
        return author.Length == 0 ? title : $"{title} - {author}";
    }

    /// <summary>
    /// Drops what Windows, macOS or Linux would refuse or reinterpret in a file
    /// name (separators, reserved punctuation, control characters, trailing dots
    /// and spaces). Letters outside ASCII are kept: member names are UTF-8.
    /// </summary>
    public static string SafeName(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Normalize(NormalizationForm.FormC))
        {
            if (char.IsControl(character) || character is '<' or '>' or '"' or '|' or '?' or '*')
                continue;

            builder.Append(character is '/' or '\\' or ':' ? '-' : character);
        }

        var collapsed = string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (collapsed.Length > maxLength)
        {
            var cut = maxLength;
            if (char.IsHighSurrogate(collapsed[cut - 1]))
                cut--;
            collapsed = collapsed[..cut];
        }

        return collapsed.Trim(' ', '.');
    }

    /// <summary>A relative manifest href must be URL-encoded (Readium packaging).</summary>
    public static string RelativeHref(string entryName) => Uri.EscapeDataString(entryName);
}

/// <summary>
/// A multi-track audiobook as ONE downloadable file: an uncompressed ZIP that
/// is also a Readium audiobook package. Unzipped it is an ordered folder of
/// the original audio files plus the cover; opened by an audiobook-aware
/// application it is a single book with chapters.
///
/// It is never stored. The tracks are already in storage with their sizes and
/// checksums recorded, so the archive is a computed layout over them.
/// </summary>
public sealed class AudiobookPackage
{
    private readonly Guid _bookId;
    private readonly IBookTrackStorage _trackStorage;
    private readonly DateTimeOffset _lastModified;

    internal AudiobookPackage(
        Guid bookId,
        string baseName,
        StoredZipLayout layout,
        IBookTrackStorage trackStorage,
        DateTimeOffset lastModified)
    {
        _bookId = bookId;
        BaseName = baseName;
        Layout = layout;
        _trackStorage = trackStorage;
        _lastModified = lastModified;
    }

    public string BaseName { get; }
    public StoredZipLayout Layout { get; }

    /// <summary>
    /// Describes the archive as a stored asset so the ordinary asset response
    /// (ranges, validators, disposition) can deliver it unchanged.
    /// </summary>
    public StoredAssetInfo Info(string extension, string contentType) =>
        new(
            FileName: BaseName + extension,
            ContentType: contentType,
            Length: Layout.Length,
            EntityTag: Layout.EntityTag,
            LastModified: _lastModified);

    public StoredAssetRead Open(string extension, string contentType, StorageByteRange? range)
    {
        var stream = new StoredZipReadStream(
            Layout.Slices(range),
            (number, trackRange, ct) => _trackStorage.OpenTrackAsync(_bookId, number, trackRange, ct));
        return new StoredAssetRead(Info(extension, contentType), stream, range);
    }
}

/// <summary>Builds the download archive and streaming manifest of a multi-track audiobook.</summary>
public sealed class AudiobookPackageService(
    IBookRepository books,
    IBookAssetStorage assets,
    IBookTrackStorage? trackStorage = null)
{
    /// <summary>Whether this host can hold and deliver multi-track books at all.</summary>
    public bool Supported => trackStorage is not null;

    /// <summary>
    /// Returns the book's package, or null when the book does not exist, is a
    /// single-file book, or this host has no track storage.
    /// </summary>
    public async Task<AudiobookPackage?> BuildAsync(Guid bookId, CancellationToken ct = default)
    {
        if (trackStorage is null)
            return null;

        var book = await books.GetByIdAsync(bookId);
        if (book is null)
            return null;

        var tracks = BookTrackList.Parse(book.FileDetails.TracksJson);
        if (tracks.Count == 0)
            return null;

        var entries = new List<StoredZipEntry>(tracks.Count + 2);
        var entryNames = tracks.ToDictionary(
            track => track.Number,
            track => AudiobookPackageNaming.TrackEntryName(track, tracks.Count));

        var cover = await ReadCoverAsync(bookId, ct);
        var manifest = AudiobookManifest.Write(
            book,
            tracks,
            track => AudiobookPackageNaming.RelativeHref(entryNames[track.Number]),
            cover?.EntryName,
            cover?.ContentType);

        entries.Add(new StoredZipEntry(
            AudiobookManifest.ManifestEntryName, manifest.LongLength, Crc32.Compute(manifest), manifest));
        if (cover is not null)
            entries.Add(new StoredZipEntry(cover.EntryName, cover.Bytes.LongLength, Crc32.Compute(cover.Bytes), cover.Bytes));
        foreach (var track in tracks)
            entries.Add(new StoredZipEntry(entryNames[track.Number], track.Bytes, track.Crc32, Inline: null, track.Number));

        var created = DateTime.SpecifyKind(book.CreatedAt, DateTimeKind.Utc);
        return new AudiobookPackage(
            bookId,
            AudiobookPackageNaming.DownloadBaseName(book),
            StoredZipLayout.Build(entries, created),
            trackStorage,
            new DateTimeOffset(created));
    }

    /// <summary>
    /// The streaming manifest: the same document as the packaged one, with
    /// tracks and cover addressed through <paramref name="trackUrl"/> and
    /// <paramref name="coverUrl"/> instead of archive members.
    /// </summary>
    public async Task<byte[]?> BuildStreamingManifestAsync(
        Guid bookId,
        Func<BookTrack, string> trackUrl,
        string? coverUrl,
        string selfUrl,
        CancellationToken ct = default)
    {
        if (trackStorage is null)
            return null;

        var book = await books.GetByIdAsync(bookId);
        if (book is null)
            return null;

        var tracks = BookTrackList.Parse(book.FileDetails.TracksJson);
        if (tracks.Count == 0)
            return null;

        var hasCover = !string.IsNullOrWhiteSpace(book.FileDetails.CoverFileName);
        return AudiobookManifest.Write(
            book,
            tracks,
            trackUrl,
            hasCover ? coverUrl : null,
            hasCover ? MediaTypeMap.ForCover(book.FileDetails.CoverFileName!) : null,
            selfUrl);
    }

    private async Task<PackageCover?> ReadCoverAsync(Guid bookId, CancellationToken ct)
    {
        await using var opened = await assets.OpenBookCoverAsync(bookId, ct);
        if (opened is null)
            return null;

        using var buffer = new MemoryStream();
        await opened.Content.CopyToAsync(buffer, ct);
        if (buffer.Length == 0)
            return null;

        var extension = Path.GetExtension(opened.Info.FileName).ToLowerInvariant();
        return new PackageCover($"cover{extension}", opened.Info.ContentType, buffer.ToArray());
    }

    private sealed record PackageCover(string EntryName, string ContentType, byte[] Bytes);
}

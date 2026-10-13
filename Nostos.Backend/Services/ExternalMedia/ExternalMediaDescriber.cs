using System.IO.Compression;
using System.Xml.Linq;

namespace Nostos.Backend.Services.ExternalMedia;

/// <summary>What a source file says about itself, read without changing it.</summary>
public sealed record ExternalMediaDescription(
    bool Readable,
    string? Title = null,
    string? Album = null,
    string? Author = null,
    string? Narrator = null,
    string? Language = null,
    long DurationMs = 0,
    IReadOnlyList<ExternalMediaChapter>? Chapters = null,
    ExternalMediaCover? Cover = null)
{
    public static readonly ExternalMediaDescription Unreadable = new(false);
}

public sealed record ExternalMediaChapter(string Title, double StartSeconds);

/// <summary>Artwork embedded in a source file. Stored as a Nostos-owned cover on import.</summary>
public sealed record ExternalMediaCover(byte[] Bytes, string Extension);

/// <summary>
/// Reads the embedded metadata of a file in the external root. A seam so
/// scanning and importing can be tested without real audio.
/// </summary>
public interface IExternalMediaDescriber
{
    ExternalMediaDescription Describe(string rootId, string relativePath);
}

/// <summary>
/// Reads tags through <see cref="ExternalMediaRoot"/>, so a file is described
/// under the same checks it is later opened under, and only ever by reading.
/// </summary>
public sealed class ExternalMediaDescriber(ExternalMediaRoot root) : IExternalMediaDescriber
{
    private const int MaxPackageBytes = 4 * 1024 * 1024;
    private const int MaxCoverBytes = 20 * 1024 * 1024;

    public ExternalMediaDescription Describe(string rootId, string relativePath)
    {
        using var stream = root.OpenRead(rootId, relativePath);
        if (stream is null)
            return ExternalMediaDescription.Unreadable;

        try
        {
            return Path.GetExtension(relativePath).ToLowerInvariant() switch
            {
                ".epub" => DescribeEpub(stream),
                ".pdf" => DescribePdf(stream),
                _ => DescribeAudio(stream),
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            // A damaged file is one unreadable candidate, never a failed scan.
            return ExternalMediaDescription.Unreadable;
        }
    }

    private static ExternalMediaDescription DescribeAudio(Stream stream)
    {
        var track = new ATL.Track(stream);
        if (track.DurationMs <= 0)
            return ExternalMediaDescription.Unreadable;

        return new ExternalMediaDescription(
            Readable: true,
            Title: Clean(track.Title),
            Album: Clean(track.Album),
            Author: Clean(track.AlbumArtist) ?? Clean(track.Artist),
            // Audiobook taggers conventionally put the narrator in "composer".
            Narrator: Clean(track.Composer),
            Language: Clean(track.Language),
            DurationMs: (long)track.DurationMs,
            Chapters: track.Chapters?
                .Select(chapter => new ExternalMediaChapter(chapter.Title ?? string.Empty, chapter.StartTime / 1000.0))
                .ToList(),
            Cover: CoverOf(track));
    }

    private static ExternalMediaCover? CoverOf(ATL.Track track)
    {
        var picture = track.EmbeddedPictures?.FirstOrDefault(p => p.PictureData is { Length: > 0 and <= MaxCoverBytes });
        if (picture is null)
            return null;

        var extension = picture.NativeFormat switch
        {
            Commons.ImageFormat.Jpeg => ".jpg",
            Commons.ImageFormat.Png => ".png",
            _ => null,
        };
        return extension is null ? null : new ExternalMediaCover(picture.PictureData, extension);
    }

    private static ExternalMediaDescription DescribeEpub(Stream stream)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var container = XDocument.Parse(ReadEntry(archive, "META-INF/container.xml"));
        var packagePath = container.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "rootfile")
            ?.Attribute("full-path")?.Value;
        if (string.IsNullOrWhiteSpace(packagePath))
            return ExternalMediaDescription.Unreadable;

        var package = XDocument.Parse(ReadEntry(archive, packagePath));
        string? Dc(string name) => Clean(package.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == name
                && element.Name.NamespaceName == "http://purl.org/dc/elements/1.1/")
            ?.Value);

        return new ExternalMediaDescription(
            Readable: true,
            Title: Dc("title"),
            Author: Dc("creator"),
            Language: Dc("language"));
    }

    private static ExternalMediaDescription DescribePdf(Stream stream)
    {
        Span<byte> header = stackalloc byte[5];
        return stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length
            && header.SequenceEqual("%PDF-"u8)
            ? new ExternalMediaDescription(Readable: true)
            : ExternalMediaDescription.Unreadable;
    }

    private static string ReadEntry(ZipArchive archive, string path)
    {
        var entry = archive.Entries.FirstOrDefault(candidate =>
                string.Equals(candidate.FullName, path, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("The EPUB is missing a required file.");
        if (entry.Length > MaxPackageBytes)
            throw new InvalidDataException("The EPUB package document is too large.");

        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}

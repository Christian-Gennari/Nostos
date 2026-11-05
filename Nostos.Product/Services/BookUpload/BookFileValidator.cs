using System.IO.Compression;
using System.Xml.Linq;
using ATL;

namespace Nostos.Backend.Services.BookUpload;

/// <summary>
/// Checks that the bytes of an uploaded book file are the kind of file the
/// name claims (#1386). The type allow-list only looks at the name and the
/// declared content type, so an empty file, a truncated archive or an
/// executable renamed to .epub used to become a normal-looking book whose
/// error only showed when the reader opened it.
///
/// Only the formats Nostos reads are inspected: EPUB, PDF and audio. TXT and
/// MOBI are kept as stored files, so any bytes are accepted for them.
/// </summary>
public static class BookFileValidator
{
    private const int PdfHeaderWindow = 1024;

    /// <summary>
    /// Returns null when the file looks right, otherwise a short reason a
    /// reader can act on. The stream must be readable and seekable.
    /// </summary>
    public static string? Validate(Stream content, string fileName)
    {
        if (content.CanSeek)
        {
            if (content.Length == 0) return "This file is empty.";
            content.Position = 0;
        }

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        try
        {
            return extension switch
            {
                ".epub" => ValidateEpub(content),
                ".pdf" => ValidatePdf(content),
                ".mp3" or ".m4a" or ".m4b" => ValidateAudio(content, extension),
                _ => null,
            };
        }
        finally
        {
            if (content.CanSeek) content.Position = 0;
        }
    }

    private static string? ValidateEpub(Stream content)
    {
        const string broken = "This file is not a valid EPUB. It may be damaged or incomplete.";
        try
        {
            using var zip = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);
            var container = zip.GetEntry("META-INF/container.xml");
            if (container is null) return broken;

            XDocument doc;
            using (var stream = container.Open())
                doc = XDocument.Load(stream);

            var rootfile = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "rootfile")
                ?.Attribute("full-path")?.Value;
            if (string.IsNullOrWhiteSpace(rootfile)) return broken;

            var opf = zip.GetEntry(Uri.UnescapeDataString(rootfile).TrimStart('/'));
            if (opf is null) return broken;

            using var opfStream = opf.Open();
            var package = XDocument.Load(opfStream).Root;
            return package is { Name.LocalName: "package" } ? null : broken;
        }
        catch (Exception)
        {
            // Any failure to open or parse the archive means a reader cannot either.
            return broken;
        }
    }

    private static string? ValidatePdf(Stream content)
    {
        var buffer = new byte[PdfHeaderWindow];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = content.Read(buffer, read, buffer.Length - read);
            if (n == 0) break;
            read += n;
        }

        // Readers tolerate a little junk before the header; the spec allows
        // the marker anywhere in the first 1024 bytes.
        return buffer.AsSpan(0, read).IndexOf("%PDF-"u8) >= 0
            ? null
            : "This file is not a valid PDF.";
    }

    private static string? ValidateAudio(Stream content, string extension)
    {
        const string broken = "This file is not a playable audio file.";
        try
        {
            var track = new Track(content, extension == ".mp3" ? "audio/mpeg" : "audio/mp4");
            return track.Duration > 0 || track.Bitrate > 0 ? null : broken;
        }
        catch (Exception)
        {
            return broken;
        }
    }
}

using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using UglyToad.PdfPig;

namespace Nostos.Backend.Services;

/// <summary>What an EPUB or PDF says about itself. Every field is optional.</summary>
/// <param name="Language">The language's English name when it is known, else the file's own code.</param>
/// <param name="CoverExtension"><c>.jpg</c> or <c>.png</c> when <paramref name="CoverBytes"/> is set.</param>
public sealed record BookFileMetadata(
    string? Title,
    string? Author,
    string? Language,
    int? PageCount,
    byte[]? CoverBytes,
    string? CoverExtension)
{
    public static readonly BookFileMetadata Empty = new(null, null, null, null, null, null);

    public bool IsEmpty =>
        Title is null && Author is null && Language is null && PageCount is null && CoverBytes is null;
}

/// <summary>
/// Reads the title, author, language, page count and cover that an EPUB or a
/// PDF carries itself. The bytes are untrusted: nothing here may throw on a
/// broken or hostile file, and an EPUB's XML is read with DTDs and external
/// entities off. A file that cannot be read gives <see cref="BookFileMetadata.Empty"/>.
/// </summary>
public static class BookFileMetadataReader
{
    private const long MaxEpubXmlBytes = 4L * 1024 * 1024;
    private const long MaxCoverBytes = 16L * 1024 * 1024;

    private static readonly XNamespace Container = "urn:oasis:names:tc:opendocument:xmlns:container";
    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";

    public static bool CanRead(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() is ".epub" or ".pdf";

    public static async Task<BookFileMetadata> ReadAsync(
        Stream content,
        string fileName,
        CancellationToken ct = default)
    {
        if (!CanRead(fileName))
            return BookFileMetadata.Empty;
        var extension = Path.GetExtension(fileName).ToLowerInvariant();

        try
        {
            Stream seekable = content;
            if (!content.CanSeek)
            {
                var copy = new MemoryStream();
                await content.CopyToAsync(copy, ct);
                seekable = copy;
            }

            seekable.Position = 0;
            return extension == ".epub"
                ? await ReadEpubAsync(seekable, ct)
                : ReadPdf(seekable);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return BookFileMetadata.Empty;
        }
    }

    private static BookFileMetadata ReadPdf(Stream content)
    {
        using var pdf = PdfDocument.Open(content);
        return new BookFileMetadata(
            Clean(pdf.Information.Title),
            Clean(pdf.Information.Author),
            null,
            pdf.NumberOfPages > 0 ? pdf.NumberOfPages : null,
            null,
            null);
    }

    private static async Task<BookFileMetadata> ReadEpubAsync(Stream content, CancellationToken ct)
    {
        using var zip = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);

        var containerDoc = LoadXml(zip, "META-INF/container.xml");
        var opfPath = containerDoc?
            .Descendants(Container + "rootfile")
            .Select(e => (string?)e.Attribute("full-path"))
            .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
        if (opfPath is null)
            return BookFileMetadata.Empty;

        var opf = LoadXml(zip, opfPath);
        if (opf is null)
            return BookFileMetadata.Empty;

        var metadata = opf.Descendants(Opf + "metadata").FirstOrDefault();
        string? First(string name) => metadata?
            .Elements(Dc + name)
            .Select(e => Clean(e.Value))
            .FirstOrDefault(v => v is not null);

        var authors = metadata?
            .Elements(Dc + "creator")
            .Select(e => Clean(e.Value))
            .Where(v => v is not null)
            .Distinct()
            .ToList();

        var (coverBytes, coverExtension) = await ReadCoverAsync(zip, opf, opfPath, ct);

        return new BookFileMetadata(
            First("title"),
            authors is { Count: > 0 } ? string.Join(", ", authors) : null,
            LanguageName(First("language")),
            null,
            coverBytes,
            coverExtension);
    }

    private static async Task<(byte[]?, string?)> ReadCoverAsync(
        ZipArchive zip,
        XDocument opf,
        string opfPath,
        CancellationToken ct)
    {
        var items = opf.Descendants(Opf + "manifest").Elements(Opf + "item").ToList();

        // EPUB 3 marks the item; EPUB 2 points at it from <meta name="cover">.
        var item = items.FirstOrDefault(i =>
            ((string?)i.Attribute("properties") ?? "")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains("cover-image"));
        if (item is null)
        {
            var coverId = opf.Descendants(Opf + "meta")
                .Where(m => (string?)m.Attribute("name") == "cover")
                .Select(m => (string?)m.Attribute("content"))
                .FirstOrDefault();
            if (coverId is not null)
                item = items.FirstOrDefault(i => (string?)i.Attribute("id") == coverId);
        }

        var href = (string?)item?.Attribute("href");
        if (string.IsNullOrWhiteSpace(href))
            return (null, null);

        var path = Resolve(opfPath, href);
        var entry = path is null ? null : zip.GetEntry(path);
        if (entry is null || entry.Length is 0 or > MaxCoverBytes)
            return (null, null);

        byte[] bytes;
        await using (var stream = entry.Open())
        {
            bytes = await ReadBoundedAsync(stream, MaxCoverBytes, ct);
        }

        // Decide from the bytes, not the name; this also drops files that are
        // not a decodable image.
        await using var probe = new MemoryStream(bytes);
        var info = await CoverImageProcessor.InspectAsync(probe, ct);
        var extension = info?.Format switch
        {
            "jpeg" => ".jpg",
            "png" => ".png",
            _ => null,
        };
        return extension is null ? (null, null) : (bytes, extension);
    }

    /// <summary>Resolves a manifest href against the OPF's folder; null if it leaves the archive.</summary>
    private static string? Resolve(string opfPath, string href)
    {
        var withoutFragment = href.Split('#')[0];
        string decoded;
        try { decoded = Uri.UnescapeDataString(withoutFragment); }
        catch (UriFormatException) { return null; }

        var folder = opfPath.Contains('/') ? opfPath[..opfPath.LastIndexOf('/')] : "";
        var parts = new List<string>();
        foreach (var segment in (folder.Length > 0 ? folder + "/" + decoded : decoded).Split('/'))
        {
            if (segment is "" or ".") continue;
            if (segment == "..")
            {
                if (parts.Count == 0) return null;
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(segment);
        }

        return parts.Count == 0 ? null : string.Join('/', parts);
    }

    private static XDocument? LoadXml(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path.TrimStart('/'));
        if (entry is null || entry.Length > MaxEpubXmlBytes)
            return null;

        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 0,
            MaxCharactersInDocument = MaxEpubXmlBytes,
        });
        return XDocument.Load(reader);
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream source, long limit, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > limit)
                throw new InvalidDataException("Cover is too large.");
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static string? Clean(string? value)
    {
        var trimmed = value is null ? null : string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > 500 ? null : trimmed;
    }

    private static string? LanguageName(string? code)
    {
        if (code is null) return null;
        var primary = code.Split('-', '_')[0].ToLowerInvariant();
        if (primary.Length is < 2 or > 3) return code;
        try
        {
            var name = CultureInfo.GetCultureInfo(primary).EnglishName;
            return name.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase) || name.Contains('(')
                ? code
                : name;
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }
}

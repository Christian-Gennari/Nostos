using System.IO.Compression;
using System.Xml.Linq;
using HtmlAgilityPack;
using Nostos.Product.Storage;

namespace Nostos.Backend.Services.Notes;

/// <summary>
/// The chapter titles of an EPUB, one for each section of its reading order
/// (issue #1382). A CFI names a section by its second step (<c>/6/8</c> is the
/// fourth spine item), the book's table of contents names chapters by file, and
/// this joins the two so an export or a citation can say "in “The Storm”"
/// instead of a raw position. A section the table of contents does not name
/// belongs to the chapter before it; one before the first chapter has no title.
/// </summary>
public static class EpubSections
{
    private const long MaxEntryBytes = 8 * 1024 * 1024;

    /// <summary>
    /// The titles in reading order: index 0 is section 1. Empty when the book
    /// has no readable table of contents.
    /// </summary>
    public static IReadOnlyList<string?> Titles(Stream epub)
    {
        try
        {
            if (epub.CanSeek) epub.Position = 0;
            using var archive = new ZipArchive(epub, ZipArchiveMode.Read, leaveOpen: true);
            return Read(archive);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException or IOException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>The titles of the book's stored EPUB; empty when it has none or it cannot be read.</summary>
    public static async Task<IReadOnlyList<string?>> ReadAsync(
        IBookAssetStorage storage,
        Guid bookId,
        CancellationToken ct = default,
        IProductScratchStorage? scratchStorage = null)
    {
        await using var stored = await storage.OpenBookFileAsync(bookId, ct: ct);
        if (stored is null || !string.Equals(Path.GetExtension(stored.Info.FileName), ".epub", StringComparison.OrdinalIgnoreCase))
            return [];

        if (stored.Content.CanSeek)
            return Titles(stored.Content);

        // Object storage streams forward only; a zip needs to seek. The copy is
        // staged on the host's scratch root, not the process temp path
        // (issue #1596), and deletes itself when it closes.
        var scratch = scratchStorage ?? ProductScratchStorage.TempPath;
        var temp = scratch.CreateFilePath("nostos-epub", ".tmp");
        await using var copy = new FileStream(
            temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        await stored.Content.CopyToAsync(copy, ct);
        return Titles(copy);
    }

    private static IReadOnlyList<string?> Read(ZipArchive archive)
    {
        var container = ReadEntry(archive, "META-INF/container.xml");
        var packagePath = container is null
            ? null
            : XDocument.Parse(container).Descendants().FirstOrDefault(e => e.Name.LocalName == "rootfile")?.Attribute("full-path")?.Value;
        if (string.IsNullOrWhiteSpace(packagePath)) return [];

        var packageXml = ReadEntry(archive, packagePath);
        if (packageXml is null) return [];
        var package = XDocument.Parse(packageXml);
        var packageDir = Directory(packagePath);

        var manifest = package.Descendants().Where(e => e.Name.LocalName == "item" && e.Attribute("id") is not null && e.Attribute("href") is not null)
            .GroupBy(e => e.Attribute("id")!.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var spine = package.Descendants().Where(e => e.Name.LocalName == "itemref")
            .Select(e => e.Attribute("idref")?.Value)
            .ToList();
        if (spine.Count == 0) return [];

        var spineIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < spine.Count; i++)
        {
            if (spine[i] is { } id && manifest.TryGetValue(id, out var item))
                spineIndex.TryAdd(Resolve(packageDir, item.Attribute("href")!.Value), i);
        }

        var entries = TocEntries(archive, package, manifest, packageDir);
        var titles = new string?[spine.Count];
        foreach (var (path, title) in entries)
        {
            if (spineIndex.TryGetValue(path, out var index) && titles[index] is null)
                titles[index] = title;
        }

        // A section the contents do not name carries on the chapter before it.
        string? current = null;
        for (var i = 0; i < titles.Length; i++)
        {
            if (titles[i] is null) titles[i] = current;
            else current = titles[i];
        }
        return titles.All(t => t is null) ? [] : titles;
    }

    private static List<(string Path, string Title)> TocEntries(
        ZipArchive archive,
        XDocument package,
        Dictionary<string, XElement> manifest,
        string packageDir)
    {
        var nav = manifest.Values.FirstOrDefault(i =>
            (i.Attribute("properties")?.Value ?? string.Empty).Split(' ').Contains("nav"));
        if (nav is not null)
        {
            var path = Resolve(packageDir, nav.Attribute("href")!.Value);
            var html = ReadEntry(archive, path);
            if (html is not null && NavEntries(html, Directory(path)) is { Count: > 0 } fromNav)
                return fromNav;
        }

        var tocId = package.Descendants().FirstOrDefault(e => e.Name.LocalName == "spine")?.Attribute("toc")?.Value;
        var ncx = tocId is not null && manifest.TryGetValue(tocId, out var byId)
            ? byId
            : manifest.Values.FirstOrDefault(i => i.Attribute("media-type")?.Value == "application/x-dtbncx+xml");
        if (ncx is not null)
        {
            var path = Resolve(packageDir, ncx.Attribute("href")!.Value);
            var xml = ReadEntry(archive, path);
            if (xml is not null) return NcxEntries(xml, Directory(path));
        }
        return [];
    }

    private static List<(string Path, string Title)> NavEntries(string html, string navDir)
    {
        var document = new HtmlDocument { OptionFixNestedTags = true, OptionAutoCloseOnEnd = true };
        document.LoadHtml(html);
        var navs = document.DocumentNode.Descendants("nav").ToList();
        var toc = navs.FirstOrDefault(n => n.GetAttributeValue("epub:type", string.Empty).Split(' ').Contains("toc"))
            ?? navs.FirstOrDefault();
        var entries = new List<(string, string)>();
        if (toc is null) return entries;
        foreach (var anchor in toc.Descendants("a"))
        {
            var href = anchor.GetAttributeValue("href", string.Empty);
            var title = Clean(HtmlEntity.DeEntitize(anchor.InnerText));
            if (href.Length > 0 && title.Length > 0 && !href.StartsWith('#'))
                entries.Add((Resolve(navDir, StripFragment(href)), title));
        }
        return entries;
    }

    private static List<(string Path, string Title)> NcxEntries(string xml, string ncxDir)
    {
        var entries = new List<(string, string)>();
        foreach (var point in XDocument.Parse(xml).Descendants().Where(e => e.Name.LocalName == "navPoint"))
        {
            var src = point.Elements().FirstOrDefault(e => e.Name.LocalName == "content")?.Attribute("src")?.Value;
            var label = point.Elements().FirstOrDefault(e => e.Name.LocalName == "navLabel")?
                .Elements().FirstOrDefault(e => e.Name.LocalName == "text")?.Value;
            var title = Clean(label ?? string.Empty);
            if (!string.IsNullOrEmpty(src) && title.Length > 0)
                entries.Add((Resolve(ncxDir, StripFragment(src)), title));
        }
        return entries;
    }

    private static string Clean(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

    private static string StripFragment(string href)
    {
        var hash = href.IndexOf('#');
        return hash >= 0 ? href[..hash] : href;
    }

    private static string Directory(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    // A path inside the zip: relative to a directory, percent-decoded, "." and ".." folded.
    private static string Resolve(string directory, string href)
    {
        var decoded = Uri.UnescapeDataString(href.Replace('\\', '/'));
        var parts = new List<string>();
        foreach (var part in (decoded.StartsWith('/') ? decoded : directory + "/" + decoded).Split('/'))
        {
            if (part.Length == 0 || part == ".") continue;
            if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else parts.Add(part);
        }
        return string.Join('/', parts);
    }

    private static string? ReadEntry(ZipArchive archive, string path)
    {
        var entry = archive.GetEntry(path)
            ?? archive.Entries.FirstOrDefault(e => string.Equals(e.FullName, path, StringComparison.OrdinalIgnoreCase));
        if (entry is null || entry.Length > MaxEntryBytes) return null;
        using var reader = new StreamReader(entry.Open(), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}

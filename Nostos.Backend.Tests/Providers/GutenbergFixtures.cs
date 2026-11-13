using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace Nostos.Backend.Tests.Providers;

/// <summary>
/// The recorded Project Gutenberg responses used by both the provider's
/// behaviour tests and the characterization suite, so a refactor is measured
/// against the same bytes the source really sent. Also builds the tiny
/// zipped-tar archives the bulk reader is exercised with, so no binary fixture
/// is committed.
/// </summary>
internal static class GutenbergFixtures
{
    public static string Load(string filename)
    {
        var basePath = AppContext.BaseDirectory;
        var directPath = Path.Combine(basePath, "Fixtures", "gutenberg", filename);
        if (File.Exists(directPath))
            return File.ReadAllText(directPath);

        // Fallback: look upward from base directory
        var current = new DirectoryInfo(basePath);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "Fixtures", "gutenberg", filename);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            var nestedCandidate = Path.Combine(current.FullName, "Nostos.Backend.Tests", "Fixtures", "gutenberg", filename);
            if (File.Exists(nestedCandidate))
                return File.ReadAllText(nestedCandidate);

            current = current.Parent;
        }

        throw new FileNotFoundException(
            $"Gutenberg test fixture '{filename}' could not be located in output directory or repo tree.");
    }

    /// <summary>
    /// The snapshot archive as the source publishes it: a zip with one tar
    /// member, holding one file per ebook.
    /// </summary>
    public static byte[] BuildArchive(params (string Name, string Content)[] entries) =>
        BuildArchiveWithDirectory("rdf-files.tar", entries);

    public static byte[] BuildArchiveWithDirectory(
        string tarName,
        params (string Name, string Content)[] entries)
    {
        using var tarBuffer = new MemoryStream();
        using (var tar = new TarWriter(tarBuffer, TarEntryFormat.Pax, leaveOpen: true))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "cache/"));
            foreach (var (name, content) in entries)
            {
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
                });
            }
        }

        using var zipBuffer = new MemoryStream();
        using (var zip = new ZipArchive(zipBuffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry(tarName, CompressionLevel.NoCompression);
            using var stream = entry.Open();
            tarBuffer.Position = 0;
            tarBuffer.CopyTo(stream);
        }

        return zipBuffer.ToArray();
    }

    /// <summary>A minimal, valid RDF record for one ebook.</summary>
    public static string Rdf(
        string id,
        string title = "A Book",
        string? author = null,
        string type = "Text")
    {
        var creator = author is null
            ? string.Empty
            : $"<dcterms:creator><pgterms:agent rdf:about=\"2009/agents/1\">"
                + $"<pgterms:name>{author}</pgterms:name></pgterms:agent></dcterms:creator>";

        return $"""
        <?xml version="1.0" encoding="utf-8"?>
        <rdf:RDF xml:base="http://www.gutenberg.org/"
          xmlns:dcterms="http://purl.org/dc/terms/"
          xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"
          xmlns:pgterms="http://www.gutenberg.org/2009/pgterms/"
          xmlns:dcam="http://purl.org/dc/dcam/">
          <pgterms:ebook rdf:about="ebooks/{id}">
            <dcterms:title>{title}</dcterms:title>
            {creator}
            <dcterms:type>
              <rdf:Description rdf:nodeID="Ntype">
                <rdf:value>{type}</rdf:value>
              </rdf:Description>
            </dcterms:type>
          </pgterms:ebook>
        </rdf:RDF>
        """;
    }
}

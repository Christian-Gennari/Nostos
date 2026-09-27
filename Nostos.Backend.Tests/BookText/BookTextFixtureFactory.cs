using System.IO.Compression;
using System.Text;
using Nostos.Product.BookText;

namespace Nostos.Backend.Tests.BookText;

/// <summary>
/// Generates tiny, redistributable publications for the book-text pipeline.
///
/// All prose is synthetic. Fixtures are created at test time rather than
/// committing commercial books or opaque binary samples.
/// </summary>
internal static class BookTextFixtureFactory
{
    private static readonly DateTimeOffset ZipEpoch =
        new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static GeneratedBookTextFixture CreatePdf()
    {
        var pages = new[]
        {
            new[]
            {
                "Nostos Fixture Header",
                "CHAPTER ONE",
                "A repeated sentence appears here.",
                "This line ends with inter-",
                "national prose on the next line.",
            },
            new[]
            {
                "Nostos Fixture Header",
                "The chapter continues across a page boundary.",
                "A repeated sentence appears here.",
                "Straight quotes, emulation-safe punctuation, and page provenance live here.",
            },
            Array.Empty<string>(),
            new[]
            {
                "Nostos Fixture Header",
                "CHAPTER TWO",
                "The final physical page uses a printed label independent of its index.",
            },
        };

        return new GeneratedBookTextFixture(
            FileName: "generated-book-text-fixture.pdf",
            ContentType: "application/pdf",
            Format: BookTextSourceFormat.Pdf,
            Bytes: BuildPdf(pages),
            ExpectedReadingOrderSnippets:
            [
                "CHAPTER ONE",
                "The chapter continues across a page boundary.",
                "CHAPTER TWO",
            ]);
    }

    public static GeneratedBookTextFixture CreateLargePdf(int pageCount = 160)
    {
        if (pageCount < 10) throw new ArgumentOutOfRangeException(nameof(pageCount));

        var pages = Enumerable.Range(1, pageCount)
            .Select(page => new[]
            {
                "Nostos Synthetic Benchmark",
                $"CHAPTER {(page - 1) / 20 + 1}",
                $"Physical page {page} carries deterministic generated prose for indexing.",
                page == pageCount / 2
                    ? "The amber lighthouse is the unique benchmark retrieval phrase."
                    : "Ordinary repeated material exercises the lexical corpus without customer text.",
                "A final generated sentence keeps each page large enough to exercise parsing.",
            })
            .ToArray();

        return new GeneratedBookTextFixture(
            FileName: "generated-book-text-benchmark.pdf",
            ContentType: "application/pdf",
            Format: BookTextSourceFormat.Pdf,
            Bytes: BuildPdf(pages),
            ExpectedReadingOrderSnippets:
            [
                "Nostos Synthetic Benchmark",
                "The amber lighthouse is the unique benchmark retrieval phrase.",
            ]);
    }

    public static GeneratedBookTextFixture CreateEpub(bool rootPackage = false)
    {
        const string mimetype = "application/epub+zip";
        var packagePath = rootPackage ? "package.opf" : "OEBPS/package.opf";
        var resourcePrefix = rootPackage ? string.Empty : "OEBPS/";
        var container = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
              <rootfiles>
                <rootfile full-path="{packagePath}" media-type="application/oebps-package+xml"/>
              </rootfiles>
            </container>
            """;
        const string package = """
            <?xml version="1.0" encoding="UTF-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="book-id">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:identifier id="book-id">urn:uuid:00000000-0000-0000-0000-000000000468</dc:identifier>
                <dc:title>Nostos Generated Fixture</dc:title>
                <dc:language>en</dc:language>
              </metadata>
              <manifest>
                <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
                <item id="c1" href="chapter-1.xhtml" media-type="application/xhtml+xml"/>
                <item id="c2a" href="chapter-2a.xhtml" media-type="application/xhtml+xml"/>
                <item id="c2b" href="chapter-2b.xhtml" media-type="application/xhtml+xml"/>
              </manifest>
              <spine>
                <itemref idref="c1"/>
                <itemref idref="c2a"/>
                <itemref idref="c2b"/>
              </spine>
            </package>
            """;
        const string nav = """
            <?xml version="1.0" encoding="UTF-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
              <head><title>Contents</title></head>
              <body>
                <nav epub:type="toc">
                  <ol>
                    <li><a href="chapter-1.xhtml">Chapter One</a></li>
                    <li><a href="chapter-2a.xhtml">Chapter Two</a></li>
                  </ol>
                </nav>
              </body>
            </html>
            """;
        const string chapter1 = """
            <?xml version="1.0" encoding="UTF-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml">
              <head><title>Chapter One</title></head>
              <body>
                <h1>Chapter One</h1>
                <p>A sentence is split across <em>nested</em> inline markup without changing reading order.</p>
                <p id="duplicate-a">The duplicate quotation belongs to chapter one.</p>
                <p>Café déjà vu — Göteborg &amp; Malmö.</p>
                <aside id="note-1" epub:type="footnote" xmlns:epub="http://www.idpf.org/2007/ops">
                  A generated footnote.
                </aside>
              </body>
            </html>
            """;
        const string chapter2a = """
            <?xml version="1.0" encoding="UTF-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml">
              <head><title>Chapter Two, first resource</title></head>
              <body>
                <h1>Chapter Two</h1>
                <h2>Across resources</h2>
                <p>This chapter begins in one spine resource and continues in the next.</p>
                <p id="duplicate-b">The duplicate quotation belongs to chapter one.</p>
                <p><a href="chapter-2b.xhtml#continuation">Continue internally</a></p>
              </body>
            </html>
            """;
        const string chapter2b = """
            <?xml version="1.0" encoding="UTF-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml">
              <head><title>Chapter Two, continuation</title></head>
              <body>
                <p id="continuation">The intended continuation is structurally distinct despite reader reflow.</p>
              </body>
            </html>
            """;

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(archive, "mimetype", mimetype, CompressionLevel.NoCompression);
            AddEntry(archive, "META-INF/container.xml", container, CompressionLevel.Optimal);
            AddEntry(archive, packagePath, package, CompressionLevel.Optimal);
            AddEntry(archive, $"{resourcePrefix}nav.xhtml", nav, CompressionLevel.Optimal);
            AddEntry(archive, $"{resourcePrefix}chapter-1.xhtml", chapter1, CompressionLevel.Optimal);
            AddEntry(archive, $"{resourcePrefix}chapter-2a.xhtml", chapter2a, CompressionLevel.Optimal);
            AddEntry(archive, $"{resourcePrefix}chapter-2b.xhtml", chapter2b, CompressionLevel.Optimal);
        }

        return new GeneratedBookTextFixture(
            FileName: rootPackage
                ? "generated-root-opf-book-text-fixture.epub"
                : "generated-book-text-fixture.epub",
            ContentType: "application/epub+zip",
            Format: BookTextSourceFormat.Epub,
            Bytes: output.ToArray(),
            ExpectedReadingOrderSnippets:
            [
                "Chapter One",
                "This chapter begins in one spine resource",
                "The intended continuation is structurally distinct",
            ]);
    }

    private static byte[] BuildPdf(IReadOnlyList<string[]> pages)
    {
        var pageCount = pages.Count;
        var firstPageObject = 3;
        var fontObject = firstPageObject + pageCount;
        var firstContentObject = fontObject + 1;
        var lastObject = firstContentObject + pageCount - 1;

        var objects = new Dictionary<int, byte[]>
        {
            [1] = Ascii(
                "<< /Type /Catalog /Pages 2 0 R " +
                "/PageLabels << /Nums [0 << /S /r >> 2 << /S /D /St 1 >>] >> >>"),
            [2] = Ascii(
                $"<< /Type /Pages /Kids [{string.Join(" ", Enumerable.Range(firstPageObject, pageCount).Select(i => $"{i} 0 R"))}] /Count {pageCount} >>"),
            [fontObject] = Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
        };

        for (var index = 0; index < pageCount; index++)
        {
            var pageObject = firstPageObject + index;
            var contentObject = firstContentObject + index;
            objects[pageObject] = Ascii(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                $"/Resources << /Font << /F1 {fontObject} 0 R >> >> /Contents {contentObject} 0 R >>");

            var stream = Ascii(BuildPageContent(pages[index]));
            using var content = new MemoryStream();
            WriteAscii(content, $"<< /Length {stream.Length} >>\nstream\n");
            content.Write(stream);
            WriteAscii(content, "\nendstream");
            objects[contentObject] = content.ToArray();
        }

        using var pdf = new MemoryStream();
        WriteAscii(pdf, "%PDF-1.4\n% Nostos generated fixture\n");

        var offsets = new long[lastObject + 1];
        for (var number = 1; number <= lastObject; number++)
        {
            offsets[number] = pdf.Position;
            WriteAscii(pdf, $"{number} 0 obj\n");
            pdf.Write(objects[number]);
            WriteAscii(pdf, "\nendobj\n");
        }

        var xrefOffset = pdf.Position;
        WriteAscii(pdf, $"xref\n0 {lastObject + 1}\n");
        WriteAscii(pdf, "0000000000 65535 f \n");
        for (var number = 1; number <= lastObject; number++)
            WriteAscii(pdf, $"{offsets[number]:D10} 00000 n \n");

        WriteAscii(
            pdf,
            $"trailer\n<< /Size {lastObject + 1} /Root 1 0 R >>\n" +
            $"startxref\n{xrefOffset}\n%%EOF\n");

        return pdf.ToArray();
    }

    private static string BuildPageContent(IReadOnlyList<string> lines)
    {
        var builder = new StringBuilder();
        builder.Append("BT\n/F1 12 Tf\n72 720 Td\n");

        for (var index = 0; index < lines.Count; index++)
        {
            if (index > 0)
                builder.Append("0 -18 Td\n");

            builder.Append('(')
                .Append(EscapePdfLiteral(lines[index]))
                .Append(") Tj\n");
        }

        builder.Append("ET");
        return builder.ToString();
    }

    private static string EscapePdfLiteral(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal);

    private static void AddEntry(
        ZipArchive archive,
        string path,
        string content,
        CompressionLevel compression)
    {
        var entry = archive.CreateEntry(path, compression);
        entry.LastWriteTime = ZipEpoch;
        using var writer = new StreamWriter(
            entry.Open(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024,
            leaveOpen: false);
        writer.Write(content);
    }

    private static byte[] Ascii(string value) => Encoding.ASCII.GetBytes(value);

    private static void WriteAscii(Stream stream, string value)
    {
        var bytes = Ascii(value);
        stream.Write(bytes);
    }
}

internal sealed record GeneratedBookTextFixture(
    string FileName,
    string ContentType,
    BookTextSourceFormat Format,
    byte[] Bytes,
    IReadOnlyList<string> ExpectedReadingOrderSnippets);

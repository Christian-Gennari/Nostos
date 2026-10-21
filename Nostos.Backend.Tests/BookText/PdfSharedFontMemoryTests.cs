using System.Text;
using FluentAssertions;
using Nostos.Product.BookText;
using UglyToad.PdfPig;
using Xunit;

namespace Nostos.Backend.Tests.BookText;

/// <summary>
/// Regression guard for the scanned-PDF memory failure of 2026-10-11 (fixed by
/// #870 for that book): the PdfPig prerelease Nostos pinned kept a
/// parsed copy of an embedded font program for every per-page composite font,
/// about 3 MB a page on a 291-page book, until the extraction child hit its
/// heap limit. Here every page has its own Type0 font, and all of them share
/// one embedded CFF program (<c>Fixtures/pdf/shared-cff-font.cff</c>, generated
/// by <c>tools/fixtures/make_shared_cff_font.py</c>, 1,200 synthetic glyphs).
/// PdfPig still keeps one parsed copy per page font while a document is open
/// (about 1 MB a page here); the extractor bounds that by reopening the
/// document every <see cref="PdfBookTextExtractor.PagesPerOpen"/> pages.
/// </summary>
public sealed class PdfSharedFontMemoryTests
{
    private const int Pages = 160;
    private const long AllowedGrowthBytes = 24L * 1024 * 1024;

    [Fact]
    public async Task Extraction_memory_does_not_grow_with_pages_that_each_bring_their_own_font()
    {
        using var stream = new MemoryStream(BuildSharedFontPdf(Pages));
        var retained = new Dictionary<int, long>();
        var firstFull = PdfBookTextExtractor.PagesPerOpen;
        var extractor = new PdfBookTextExtractor
        {
            // Measured at the end of a full run of pages on one open document,
            // early and late, so both points hold the same number of fonts.
            AfterPage = number =>
            {
                if (number == firstFull || number == Pages)
                    retained[number] = RetainedBytes();
            },
        };

        var document = await extractor.ExtractAsync(stream);

        document.Blocks.Should().HaveCount(Pages);
        (retained[Pages] - retained[firstFull]).Should().BeLessThan(
            AllowedGrowthBytes,
            $"{Pages - firstFull} more pages with their own font must not each keep a parsed copy of the shared program");
    }

    [Fact]
    public void PdfPig_keeps_a_parsed_font_per_page_font_while_a_document_is_open()
    {
        // Why the extractor reopens: this is PdfPig's own behaviour, measured
        // here so a PdfPig upgrade that fixes it shows up as this test failing.
        var pdf = BuildSharedFontPdf(Pages);
        using var document = UglyToad.PdfPig.PdfDocument.Open(pdf);
        for (var number = 1; number <= 20; number++) document.GetPage(number).GetWords();
        var early = RetainedBytes();
        for (var number = 21; number <= Pages; number++) document.GetPage(number).GetWords();
        var late = RetainedBytes();

        (late - early).Should().BeGreaterThan(
            AllowedGrowthBytes,
            "if PdfPig stopped keeping one parsed font per page font, PdfBookTextExtractor.PagesPerOpen can go");
    }

    [Fact]
    public async Task The_extractor_reads_every_page_across_reopens_with_its_text_and_page_number()
    {
        using var stream = new MemoryStream(BuildSharedFontPdf(PdfBookTextExtractor.PagesPerOpen * 2 + 3));

        var document = await new PdfBookTextExtractor().ExtractAsync(stream);

        document.Blocks.Should().HaveCount(PdfBookTextExtractor.PagesPerOpen * 2 + 3);
        document.Blocks.Should().OnlyContain(block => block.Text.Contains("NOSTOS"));
        document.Blocks.Select(block => ((PdfBookTextSourceLocator)block.SourceSegments[0].Locator).PageIndex)
            .Should().Equal(Enumerable.Range(0, PdfBookTextExtractor.PagesPerOpen * 2 + 3));
        document.Blocks.Select(block => block.Order).Should().BeInAscendingOrder();
    }

    private static long RetainedBytes()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    /// <summary>
    /// One page per Type0/Identity-H font, each with its own CIDFontType0 and
    /// descriptor, all pointing at the same FontFile3 stream and ToUnicode CMap.
    /// The page shows CIDs 1–26, which the CMap maps to A–Z.
    /// </summary>
    internal static byte[] BuildSharedFontPdf(int pageCount)
    {
        var fontProgram = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pdf", "shared-cff-font.cff"));
        var objects = new List<byte[]>();
        int Add(byte[] body)
        {
            objects.Add(body);
            return objects.Count;
        }
        int AddText(string body) => Add(Encoding.ASCII.GetBytes(body));
        int AddStream(string dictionary, byte[] data)
        {
            using var buffer = new MemoryStream();
            buffer.Write(Encoding.ASCII.GetBytes($"<< {dictionary} /Length {data.Length} >>\nstream\n"));
            buffer.Write(data);
            buffer.Write(Encoding.ASCII.GetBytes("\nendstream"));
            return Add(buffer.ToArray());
        }

        var catalog = AddText("PLACEHOLDER");
        var pages = AddText("PLACEHOLDER");
        var fontFile = AddStream("/Subtype /Type1C", fontProgram);
        var toUnicode = AddStream(string.Empty, Encoding.ASCII.GetBytes(ToUnicodeCMap()));

        var text = string.Concat(Enumerable.Range(1, 26).Select(cid => cid.ToString("X4")));
        var nostos = string.Concat("NOSTOS".Select(c => (c - 'A' + 1).ToString("X4")));
        var content = Encoding.ASCII.GetBytes(
            $"BT /F1 18 Tf 72 720 Td <{nostos}> Tj 0 -30 Td <{text}> Tj ET");

        var pageIds = new List<int>();
        for (var index = 0; index < pageCount; index++)
        {
            var descriptor = AddText(
                $"<< /Type /FontDescriptor /FontName /NostosLeakFixture{index} /Flags 4 " +
                $"/FontBBox [0 0 600 800] /ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 " +
                $"/StemV 80 /FontFile3 {fontFile} 0 R >>");
            var cidFont = AddText(
                $"<< /Type /Font /Subtype /CIDFontType0 /BaseFont /NostosLeakFixture{index} " +
                $"/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> " +
                $"/FontDescriptor {descriptor} 0 R /DW 600 >>");
            var font = AddText(
                $"<< /Type /Font /Subtype /Type0 /BaseFont /NostosLeakFixture{index} " +
                $"/Encoding /Identity-H /DescendantFonts [{cidFont} 0 R] /ToUnicode {toUnicode} 0 R >>");
            var pageContent = AddStream(string.Empty, content);
            pageIds.Add(AddText(
                $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] " +
                $"/Resources << /Font << /F1 {font} 0 R >> >> /Contents {pageContent} 0 R >>"));
        }

        objects[catalog - 1] = Encoding.ASCII.GetBytes($"<< /Type /Catalog /Pages {pages} 0 R >>");
        objects[pages - 1] = Encoding.ASCII.GetBytes(
            $"<< /Type /Pages /Kids [{string.Join(" ", pageIds.Select(id => $"{id} 0 R"))}] /Count {pageCount} >>");

        using var pdf = new MemoryStream();
        pdf.Write(Encoding.ASCII.GetBytes("%PDF-1.6\n% Nostos shared-font fixture\n"));
        var offsets = new long[objects.Count + 1];
        for (var number = 1; number <= objects.Count; number++)
        {
            offsets[number] = pdf.Position;
            pdf.Write(Encoding.ASCII.GetBytes($"{number} 0 obj\n"));
            pdf.Write(objects[number - 1]);
            pdf.Write(Encoding.ASCII.GetBytes("\nendobj\n"));
        }

        var xref = pdf.Position;
        var trailer = new StringBuilder($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        for (var number = 1; number <= objects.Count; number++)
            trailer.Append($"{offsets[number]:D10} 00000 n \n");
        trailer.Append($"trailer\n<< /Size {objects.Count + 1} /Root {catalog} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        pdf.Write(Encoding.ASCII.GetBytes(trailer.ToString()));
        return pdf.ToArray();
    }

    private static string ToUnicodeCMap() =>
        "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n" +
        "/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n" +
        "/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n" +
        "1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n" +
        "1 beginbfrange\n<0001> <001A> <0041>\nendbfrange\n" +
        "endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n";
}

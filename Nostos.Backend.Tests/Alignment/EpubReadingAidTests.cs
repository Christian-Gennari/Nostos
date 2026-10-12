using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Nostos.Product.Alignment;
using Nostos.Product.BookText;
using Xunit;

namespace Nostos.Backend.Tests.Alignment;

public sealed class EpubReadingAidTests
{
    private const string WithFallbackBrackets =
        "<p><ruby><rb>親譲</rb><rp>（</rp><rt>おやゆず</rt><rp>）</rp></ruby>りの" +
        "<ruby><rb>無鉄砲</rb><rp>（</rp><rt>むてっぽう</rt><rp>）</rp></ruby>で小供の時から損ばかりしている。</p>";

    private const string WithoutFallbackBrackets =
        "<p>こしが痛いと云った。<ruby>腰<rt>こし</rt></ruby>を<ruby>抜<rt>ぬ</rt></ruby>かした事がある。</p>";

    [Fact]
    public async Task The_annotated_text_is_the_text_every_other_reader_of_the_book_gets()
    {
        var epub = Epub(WithFallbackBrackets, WithoutFallbackBrackets, "<p>Plain prose has no glosses.</p>");
        var extractor = new EpubBookTextExtractor();

        var plain = await extractor.ExtractAsync(new MemoryStream(epub));
        var annotated = await extractor.ExtractAnnotatedAsync(new MemoryStream(epub));

        annotated.Document.Blocks.Select(block => block.Text)
            .Should().Equal(plain.Blocks.Select(block => block.Text));
        annotated.Document.Blocks.Select(block => block.SourceSegments.Single().Locator)
            .Should().Equal(plain.Blocks.Select(block => block.SourceSegments.Single().Locator));
    }

    [Fact]
    public async Task Glosses_and_their_fallback_brackets_are_marked_and_nothing_else()
    {
        var annotated = await new EpubBookTextExtractor()
            .ExtractAnnotatedAsync(new MemoryStream(Epub(WithFallbackBrackets, "<p>Plain prose.</p>")));

        var block = annotated.Document.Blocks[0];

        block.Text.Should().Be("親譲（おやゆず）りの無鉄砲（むてっぽう）で小供の時から損ばかりしている。");
        Marked(annotated, 0).Should().Equal("（", "おやゆず", "）", "（", "むてっぽう", "）");
        annotated.ReadingAids.Should().NotContainKey(1);
    }

    [Fact]
    public async Task A_gloss_is_marked_where_it_is_printed_not_where_the_same_letters_occur_earlier()
    {
        var annotated = await new EpubBookTextExtractor()
            .ExtractAnnotatedAsync(new MemoryStream(Epub(WithoutFallbackBrackets)));

        var text = annotated.Document.Blocks[0].Text;
        var ranges = annotated.ReadingAids[0];

        text.Should().Be("こしが痛いと云った。腰こしを抜ぬかした事がある。");
        ranges.Should().Equal(
            new BookTextRange(text.IndexOf("腰こし", StringComparison.Ordinal) + 1, 2),
            new BookTextRange(text.IndexOf("抜ぬ", StringComparison.Ordinal) + 1, 1));
    }

    [Fact]
    public async Task The_narration_matches_the_page_and_lands_on_its_true_offset()
    {
        var annotated = await new EpubBookTextExtractor()
            .ExtractAnnotatedAsync(new MemoryStream(Epub(WithoutFallbackBrackets)));

        var match = new PassageMatcher(annotated.Document, annotated.ReadingAids)
            .Locate("こしが痛いと云った腰を抜かした事がある");

        match.Accepted.Should().BeTrue();
        match.Score.Should().Be(1);

        var end = match.End!.Locator.Should().BeOfType<EpubBookTextSourceLocator>().Subject;
        end.EndTextOffset.Should().Be(annotated.Document.Blocks[0].Text.Length - 1);
    }

    private static string[] Marked(BookTextAnnotatedDocument annotated, int order)
    {
        var text = annotated.Document.Blocks.Single(block => block.Order == order).Text;

        return annotated.ReadingAids[order]
            .Select(range => text.Substring(range.Start, range.Length))
            .ToArray();
    }

    private static byte[] Epub(params string[] paragraphs)
    {
        const string container = """
            <?xml version="1.0" encoding="UTF-8"?>
            <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
              <rootfiles><rootfile full-path="package.opf" media-type="application/oebps-package+xml"/></rootfiles>
            </container>
            """;
        const string package = """
            <?xml version="1.0" encoding="UTF-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:identifier id="id">urn:uuid:00000000-0000-0000-0000-000000000844</dc:identifier>
                <dc:title>Ruby</dc:title><dc:language>ja</dc:language>
              </metadata>
              <manifest><item id="c" href="c.xhtml" media-type="application/xhtml+xml"/></manifest>
              <spine><itemref idref="c"/></spine>
            </package>
            """;
        var chapter =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>c</title></head><body>\n" +
            string.Join("\n", paragraphs) +
            "\n</body></html>";

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(archive, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            Add(archive, "META-INF/container.xml", container, CompressionLevel.Optimal);
            Add(archive, "package.opf", package, CompressionLevel.Optimal);
            Add(archive, "c.xhtml", chapter, CompressionLevel.Optimal);
        }

        return output.ToArray();
    }

    private static void Add(ZipArchive archive, string name, string content, CompressionLevel level)
    {
        using var writer = new StreamWriter(
            archive.CreateEntry(name, level).Open(),
            new UTF8Encoding(false));
        writer.Write(content);
    }
}

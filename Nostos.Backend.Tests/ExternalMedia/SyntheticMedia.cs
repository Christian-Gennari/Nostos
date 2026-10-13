using System.IO.Compression;
using System.Text;
using Nostos.Product.Alignment;

namespace Nostos.Backend.Tests.ExternalMedia;

/// <summary>Small real media files, built in memory so no fixture is checked in.</summary>
internal static class SyntheticMedia
{
    /// <summary>
    /// Silent MPEG 2 Layer III at 22 050 Hz and 64 kbps, optionally led by an
    /// ID3v2.3 tag. Each frame plays for 576 samples.
    /// </summary>
    public static byte[] Mp3(int frames, string? album = null, string? artist = null, string? title = null)
    {
        using var stream = new MemoryStream();
        var tag = Id3(("TALB", album), ("TPE1", artist), ("TIT2", title));
        stream.Write(tag);

        var header = new byte[] { 0xFF, 0xF3, 0x80, 0xC4 };
        var frame = new byte[Mp3ConstantBitrate.ParseFrame(header)!.Value.Length];
        header.CopyTo(frame, 0);
        for (var i = 0; i < frames; i++)
            stream.Write(frame);

        return stream.ToArray();
    }

    public static double Mp3Seconds(int frames) => frames * 576 / 22050d;

    public static byte[] Epub(string? title, string? author)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, "mimetype", "application/epub+zip");
            Write(archive, "META-INF/container.xml",
                """
                <?xml version="1.0"?>
                <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                  <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
                </container>
                """);
            Write(archive, "OEBPS/content.opf",
                $"""
                <?xml version="1.0"?>
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                    {(title is null ? "" : $"<dc:title>{title}</dc:title>")}
                    {(author is null ? "" : $"<dc:creator>{author}</dc:creator>")}
                    <dc:language>en</dc:language>
                  </metadata>
                </package>
                """);
        }

        return stream.ToArray();
    }

    public static byte[] Pdf() => Encoding.ASCII.GetBytes("%PDF-1.7\n%%EOF\n");

    private static void Write(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(content.TrimStart());
    }

    private static byte[] Id3(params (string Id, string? Value)[] frames)
    {
        using var body = new MemoryStream();
        foreach (var (id, value) in frames)
        {
            if (value is null)
                continue;

            var text = Encoding.Latin1.GetBytes(value);
            body.Write(Encoding.ASCII.GetBytes(id));
            var size = text.Length + 1;
            body.Write([(byte)(size >> 24), (byte)(size >> 16), (byte)(size >> 8), (byte)size, 0, 0, 0]);
            body.Write(text);
        }

        if (body.Length == 0)
            return [];

        var length = (int)body.Length;
        using var tag = new MemoryStream();
        tag.Write("ID3"u8);
        tag.Write([3, 0, 0,
            (byte)((length >> 21) & 0x7F), (byte)((length >> 14) & 0x7F), (byte)((length >> 7) & 0x7F), (byte)(length & 0x7F)]);
        body.WriteTo(tag);
        return tag.ToArray();
    }
}

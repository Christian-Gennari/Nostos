namespace Nostos.Backend.Tests.Support;

/// <summary>
/// The versioned highlight-import fixture corpus under
/// <c>Fixtures/&lt;source&gt;/&lt;version&gt;/</c>. Tests name a fixture by its
/// source version here, so a new version moves one path, not every test.
/// </summary>
internal static class ImportFixtures
{
    public static string KindleEnglish => FixturePath("kindle", "2024-en", "My Clippings.txt");
    public static string KindleLanguages => FixturePath("kindle", "2024-multi", "My Clippings.languages.txt");
    public static string ReadwiseExport => FixturePath("readwise", "2026-10", "readwise-export.csv");
    public static string KoreaderEpub => FixturePath("koreader", "2026-09-epub", "metadata.lua");
    public static string KoreaderMultiline => FixturePath("koreader", "2026-10-epub-multiline", "metadata.multiline.lua");
    public static string KoreaderPdf => FixturePath("koreader", "2026-09-pdf", "metadata.pdf.lua");
    public static string KoreaderPdfUntitled => FixturePath("koreader", "2026-10-pdf-untitled", "metadata.pdf-untitled.lua");

    public static string FixturePath(params string[] parts) =>
        Path.Combine([AppContext.BaseDirectory, "Fixtures", .. parts]);
}

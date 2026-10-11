using FluentAssertions;
using Nostos.Product.Http;
using Xunit;

namespace Nostos.Backend.Tests.Configuration;

/// <summary>
/// The default static-file provider answers 404 for extensions it does not
/// know. The PDF viewer's locale files are such a case: built into the
/// frontend, requested on every PDF open, and never served.
/// </summary>
public class NostosFrontendStaticFilesTests
{
    [Fact]
    public void ServesThePdfViewerLocaleFiles()
    {
        var options = NostosFrontendStaticFiles.CreateOptions();

        options.ContentTypeProvider!
            .TryGetContentType("assets/locale/en-US/viewer.ftl", out var contentType)
            .Should().BeTrue();
        contentType.Should().Be(NostosFrontendStaticFiles.FluentContentType);
    }

    [Theory]
    [InlineData("index.html", "text/html")]
    [InlineData("main-ABC123.js", "text/javascript")]
    [InlineData("assets/wasm/qcms_bg.wasm", "application/wasm")]
    public void KeepsTheDefaultMappings(string path, string expected)
    {
        var options = NostosFrontendStaticFiles.CreateOptions();

        options.ContentTypeProvider!.TryGetContentType(path, out var contentType).Should().BeTrue();
        contentType.Should().Be(expected);
    }
}

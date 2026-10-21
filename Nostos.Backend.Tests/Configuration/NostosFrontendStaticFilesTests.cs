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
    [InlineData("assets/iccs/CGATS001Compat-v2-micro.icc", NostosFrontendStaticFiles.IccProfileContentType)]
    [InlineData("assets/cmaps/78-EUC-H.bcmap", NostosFrontendStaticFiles.BinaryCMapContentType)]
    public void ServesThePdfViewerColourProfileAndCMaps(string path, string expected)
    {
        var options = NostosFrontendStaticFiles.CreateOptions();

        options.ContentTypeProvider!.TryGetContentType(path, out var contentType).Should().BeTrue();
        contentType.Should().Be(expected);
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

    [Theory]
    [InlineData("main-UG2TJAJM.js")]
    [InlineData("chunk-J553T4TW.js")]
    [InlineData("styles-JJ4MLHCO.css")]
    [InlineData("worker-VBGNI7MH.js")]
    public void HashedBundlesAreCachedForGood(string fileName) =>
        NostosFrontendStaticFiles.CacheControlFor(fileName)
            .Should().Be("public, max-age=31536000, immutable");

    [Theory]
    [InlineData("index.html")]
    [InlineData("ngsw.json")]
    [InlineData("ngsw-worker.js")]
    [InlineData("safety-worker.js")]
    [InlineData("manifest.webmanifest")]
    public void EntryPointsAreAlwaysRevalidated(string fileName) =>
        NostosFrontendStaticFiles.CacheControlFor(fileName).Should().Be("no-cache");

    [Theory]
    [InlineData("favicon.ico")]
    [InlineData("pwa-192.png")]
    [InlineData("viewer-5.4.149.min.mjs")]
    [InlineData("main.js")]
    public void OtherFilesKeepTheHostDefault(string fileName) =>
        NostosFrontendStaticFiles.CacheControlFor(fileName).Should().BeNull();
}

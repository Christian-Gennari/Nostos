using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.StaticFiles;

namespace Nostos.Product.Http;

/// <summary>
/// Static-file options every host uses to serve the one Angular frontend.
/// The default provider refuses extensions it does not know, so the PDF
/// viewer's Fluent locale files (<c>assets/locale/*/viewer.ftl</c>) were built
/// into the frontend but answered 404 on every PDF open.
/// </summary>
public static partial class NostosFrontendStaticFiles
{
    public const string FluentContentType = "text/plain; charset=utf-8";

    /// <summary>
    /// The PDF viewer's ICC colour profile (<c>assets/iccs/*.icc</c>, pdf.js's
    /// default <c>iccUrl</c> since ngx-extended-pdf-viewer 31): without it a
    /// CMYK PDF renders without colour management.
    /// </summary>
    public const string IccProfileContentType = "application/vnd.iccprofile";

    /// <summary>
    /// The PDF viewer's binary CMaps (<c>assets/cmaps/*.bcmap</c>), which
    /// pdf.js fetches to show text in many CJK PDFs.
    /// </summary>
    public const string BinaryCMapContentType = "application/octet-stream";

    /// <summary>
    /// A file whose name carries the build's content hash (<c>main-UG2TJAJM.js</c>):
    /// its bytes never change under that name, so browsers and the edge may keep
    /// it for good. Without a header the hosting edge marks every file private
    /// and the browser revalidates it.
    /// </summary>
    public const string HashedFileCacheControl = "public, max-age=31536000, immutable";

    /// <summary>
    /// The entry points that name the hashed files (and the service worker's
    /// manifest and scripts): always asked again, so a deploy is seen at once.
    /// </summary>
    public const string EntryFileCacheControl = "no-cache";

    private static readonly HashSet<string> EntryFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "index.html",
        "ngsw.json",
        "ngsw-worker.js",
        "safety-worker.js",
        "worker-basic.min.js",
        "manifest.webmanifest",
    };

    public static StaticFileOptions CreateOptions()
    {
        var contentTypes = new FileExtensionContentTypeProvider();
        contentTypes.Mappings[".ftl"] = FluentContentType;
        contentTypes.Mappings[".icc"] = IccProfileContentType;
        contentTypes.Mappings[".bcmap"] = BinaryCMapContentType;
        return new StaticFileOptions
        {
            ContentTypeProvider = contentTypes,
            OnPrepareResponse = context =>
            {
                if (CacheControlFor(context.File.Name) is { } cacheControl)
                    context.Context.Response.Headers.CacheControl = cacheControl;
            },
        };
    }

    /// <summary>
    /// The Cache-Control a frontend file is served with, or null to leave the
    /// host's default (icons, fonts, the PDF viewer's versioned assets).
    /// </summary>
    public static string? CacheControlFor(string fileName)
    {
        if (EntryFiles.Contains(fileName))
            return EntryFileCacheControl;

        return HashedFileName().IsMatch(fileName) ? HashedFileCacheControl : null;
    }

    // The application builder's output hashing: "<name>-<8 upper-case base32>.<ext>".
    [GeneratedRegex(@"^[A-Za-z0-9_.-]+-[A-Z0-9]{8}\.(js|mjs|css)$")]
    private static partial Regex HashedFileName();
}

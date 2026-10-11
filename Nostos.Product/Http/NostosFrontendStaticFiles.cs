using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.StaticFiles;

namespace Nostos.Product.Http;

/// <summary>
/// Static-file options every host uses to serve the one Angular frontend.
/// The default provider refuses extensions it does not know, so the PDF
/// viewer's Fluent locale files (<c>assets/locale/*/viewer.ftl</c>) were built
/// into the frontend but answered 404 on every PDF open.
/// </summary>
public static class NostosFrontendStaticFiles
{
    public const string FluentContentType = "text/plain; charset=utf-8";

    public static StaticFileOptions CreateOptions()
    {
        var contentTypes = new FileExtensionContentTypeProvider();
        contentTypes.Mappings[".ftl"] = FluentContentType;
        return new StaticFileOptions { ContentTypeProvider = contentTypes };
    }
}

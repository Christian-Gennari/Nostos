namespace Nostos.Product.BookText;

public static class BookTextFormatResolver
{
    public static bool TryResolve(string? fileName, out BookTextSourceFormat format)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty);
        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            format = BookTextSourceFormat.Pdf;
            return true;
        }

        if (extension.Equals(".epub", StringComparison.OrdinalIgnoreCase))
        {
            format = BookTextSourceFormat.Epub;
            return true;
        }

        format = default;
        return false;
    }
}

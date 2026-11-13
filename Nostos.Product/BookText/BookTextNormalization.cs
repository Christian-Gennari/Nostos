using System.Text;

namespace Nostos.Product.BookText;

public static class BookTextNormalization
{
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var ch in value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'))
        {
            if (ch == '\n')
            {
                while (builder.Length > 0 && builder[^1] == ' ')
                    builder.Length--;
                if (builder.Length > 0 && builder[^1] != '\n')
                    builder.Append('\n');
                pendingSpace = false;
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = true;
                continue;
            }

            if (pendingSpace && builder.Length > 0 && builder[^1] != '\n')
                builder.Append(' ');
            pendingSpace = false;
            builder.Append(ch);
        }

        return builder.ToString().Trim();
    }
}

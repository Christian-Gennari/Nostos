using HtmlAgilityPack;

namespace Nostos.Backend.Services.Notes;

/// <summary>
/// The text of a passage (issue #1014): the reading markup it is kept with,
/// and the short opening the note lists show instead of the whole thing.
/// </summary>
public static class PassageText
{
    /// <summary>About 150,000 words: a long chapter fits, a whole book does not.</summary>
    public const int MaxTextLength = 1_000_000;

    public const int MaxHtmlLength = 2_000_000;

    /// <summary>How much of a passage a note list carries.</summary>
    public const int OpeningLength = 480;

    // Reading structure the viewer typesets. Anything else is unwrapped to its
    // text, so a book's own spans, links and classes never reach the page.
    private static readonly HashSet<string> AllowedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "br", "hr", "div", "em", "i", "strong", "b", "u", "s", "sub", "sup", "small",
        "h1", "h2", "h3", "h4", "h5", "h6", "blockquote", "ul", "ol", "li", "pre", "code",
    };

    // Dropped with everything inside them: none of it is reading text.
    private static readonly HashSet<string> DroppedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "iframe", "object", "embed", "svg", "math", "img", "picture",
        "video", "audio", "source", "head", "title", "noscript", "template", "form",
        "input", "button", "select", "textarea", "link", "meta", "base", "canvas",
    };

    private static readonly HashSet<string> AllowedAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "lang", "dir",
    };

    /// <summary>
    /// The markup reduced to reading structure: allowed tags keep only
    /// <c>lang</c> and <c>dir</c>, unknown tags give up their text, and
    /// scripts, media and comments go entirely.
    /// </summary>
    public static string Sanitize(string html)
    {
        var document = new HtmlDocument { OptionFixNestedTags = true };
        document.LoadHtml(html);
        Clean(document.DocumentNode);
        return document.DocumentNode.InnerHtml.Trim();
    }

    private static void Clean(HtmlNode parent)
    {
        foreach (var node in parent.ChildNodes.ToList())
        {
            switch (node.NodeType)
            {
                case HtmlNodeType.Comment:
                    node.Remove();
                    continue;
                case HtmlNodeType.Text:
                    continue;
                case HtmlNodeType.Element:
                    break;
                default:
                    node.Remove();
                    continue;
            }

            if (DroppedTags.Contains(node.Name))
            {
                node.Remove();
                continue;
            }

            Clean(node);

            if (!AllowedTags.Contains(node.Name))
            {
                // Keep the words, lose the element.
                foreach (var child in node.ChildNodes.ToList())
                    parent.InsertBefore(child, node);
                node.Remove();
                continue;
            }

            foreach (var attribute in node.Attributes.ToList())
            {
                if (!AllowedAttributes.Contains(attribute.Name))
                    node.Attributes.Remove(attribute);
            }
        }
    }

    /// <summary>The opening a list shows, cut at a word and marked as cut.</summary>
    public static string? Opening(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var flat = Flatten(text);
        if (flat.Length <= OpeningLength) return flat;

        var cut = flat.LastIndexOf(' ', OpeningLength);
        if (cut < OpeningLength / 2) cut = OpeningLength;
        return flat[..cut].TrimEnd() + "…";
    }

    public static int WordCount(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static string Flatten(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

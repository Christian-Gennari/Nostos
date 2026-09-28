using Nostos.Product.BookText;

namespace Nostos.Backend.Integrations.Assistant;

public static partial class AssistantCapabilities
{
    private static IEnumerable<AssistantCapability> BuildBookTextCapabilities(
        IBookTextSearchService bookText)
    {
        yield return new AssistantCapability(
            "book_text_search",
            AssistantTrustClass.Suggest,
            AssistantCapabilityCategory.SourceNavigation,
            "Searches the user's indexed imported PDF/EPUB text inside an explicit book, collection, or library scope. Returns bounded passages plus exact source provenance. Use this before making claims about what an imported book says; if no evidence is returned, do not invent a source citation.",
            """
            {
              "type": "object",
              "properties": {
                "query": { "type": "string", "description": "Distinctive lexical search terms from the user's wording. Preserve rare names, labels and quoted terms verbatim instead of replacing them with broader synonyms. Required." },
                "bookIds": {
                  "type": "array",
                  "items": { "type": "string", "format": "uuid" },
                  "description": "Optional explicit accessible book ids. Omit for collection/library scope."
                },
                "collectionId": { "type": "string", "format": "uuid", "description": "Optional collection scope. Ignored when explicit bookIds are supplied." },
                "maxPassages": { "type": "integer", "minimum": 1, "maximum": 8, "description": "Optional bounded result count." }
              },
              "required": ["query"],
              "additionalProperties": true
            }
            """,
            async (context, args, ct) =>
            {
                var query = Str(args, "query");
                if (string.IsNullOrWhiteSpace(query))
                    return Invalid("'query' is required.");

                if (!TryIds(args, "bookIds", out var bookIds))
                    return Invalid("'bookIds' must be an array of UUID strings.");

                var maxPassages = Num(args, "maxPassages");
                if (maxPassages is < 1 or > 8)
                    return Invalid("'maxPassages' must be between 1 and 8.");

                var result = await bookText.SearchAsync(
                    new BookTextSearchRequest(
                        query.Trim(),
                        bookIds,
                        Id(args, "collectionId"),
                        maxPassages),
                    ct);

                return AssistantToolResult.Ok(Element(result));
            });
    }
}

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
                  "description": "Explicit book scope. Omit to keep the current/recent conversational book scope when one exists. Pass an empty array only when the user explicitly broadens to whole-library scope."
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

    private const int QuotePreviewWords = 12;

    private static AssistantCapability BuildFindQuoteCapability(IBookTextQuoteFinder quotes) =>
        new(
            "book_text_find_quote",
            AssistantTrustClass.Suggest,
            AssistantCapabilityCategory.SourceNavigation,
            "Finds a passage the user names by its first and last words (\"it starts with … and ends with …\") in the text of the open book, or, for a paper book, in the digital editions of the same work. Use it before saving a quote the user does not read out in full, then save it with notes_capture and the quoteRef it returns. The book is supplied by the app when one is open.",
            """
            {
              "type": "object",
              "properties": {
                "startsWith": { "type": "string", "description": "The words the user says the passage starts with, exactly as they said them. Required." },
                "endsWith": { "type": "string", "description": "The words the user says the passage ends with, exactly as they said them. Required." },
                "page": { "type": "integer", "minimum": 1, "description": "The page the user named for the passage, if any. Only narrows the search." },
                "chapter": { "type": "string", "description": "The chapter the user named, if any (\"chapter 3\", \"The Snow\"). Only narrows the search." },
                "choice": { "type": "integer", "minimum": 1, "description": "When an earlier call asked the user to choose or confirm: the number they picked (1 confirms a single passage). Pass the same startsWith and endsWith as before." }
              },
              "required": ["startsWith", "endsWith"],
              "additionalProperties": true
            }
            """,
            async (context, args, ct) =>
            {
                if (Id(args, "bookId") is not { } bookId)
                    return Invalid("'bookId' is required: open the book, or find it first.");

                var startsWith = Str(args, "startsWith");
                var endsWith = Str(args, "endsWith");
                if (string.IsNullOrWhiteSpace(startsWith) || string.IsNullOrWhiteSpace(endsWith))
                    return Invalid("'startsWith' and 'endsWith' are required.");

                var page = Num(args, "page");
                var choice = Num(args, "choice");
                if (page is < 1 || choice is < 1)
                    return Invalid("'page' and 'choice' must be 1 or more.");

                var result = await quotes.FindAsync(
                    new BookTextQuoteFindRequest(
                        bookId,
                        startsWith.Trim(),
                        endsWith.Trim(),
                        page,
                        Str(args, "chapter"),
                        choice),
                    ct);

                return AssistantToolResult.Ok(Element(new
                {
                    status = QuoteStatus(result.Status),
                    message = QuoteGuidance(result),
                    totalMatches = result.TotalMatches,
                    candidates = result.Candidates.Select(candidate => new
                    {
                        number = candidate.Number,
                        quoteRef = candidate.QuoteRef,
                        source = $"{candidate.SourceTitle} ({candidate.SourceFormat})",
                        chapter = candidate.Chapter,
                        positionPercent = candidate.PositionPercent,
                        words = candidate.WordCount,
                        text = candidate.Text.Length <= 600 ? candidate.Text : null,
                        preview = candidate.Text.Length <= 600 ? null : QuotePreview(candidate.Text),
                    }),
                }));
            });

    private static string QuoteStatus(BookTextQuoteFindStatus status) => status switch
    {
        BookTextQuoteFindStatus.Found => "found",
        BookTextQuoteFindStatus.Confirm => "confirm",
        BookTextQuoteFindStatus.Choose => "choose",
        BookTextQuoteFindStatus.TextNotReady => "text_not_ready",
        BookTextQuoteFindStatus.NoDigitalEdition => "no_digital_edition",
        BookTextQuoteFindStatus.BookNotFound => "book_not_found",
        _ => "not_found",
    };

    private static string QuoteGuidance(BookTextQuoteFindResult result) => result.Status switch
    {
        BookTextQuoteFindStatus.Found =>
            "Found. If the user asked to save it, call notes_capture with this quoteRef now. Put only the user's own comment on the quote in content (none is fine), never the start or end words, the page or the request itself.",
        BookTextQuoteFindStatus.Confirm =>
            $"One passage matches, but it is long ({result.Candidates[0].WordCount} words). Show its first and last words and ask the user to confirm before saving. Nothing is saved yet. When they confirm, call book_text_find_quote again with the same words and choice 1.",
        BookTextQuoteFindStatus.Choose =>
            $"{result.TotalMatches} passages match. List them as numbered choices (first and last words, chapter) and ask which one. Nothing is saved yet. When the user answers, call book_text_find_quote again with the same words and their number as choice.",
        BookTextQuoteFindStatus.TextNotReady =>
            "The digital edition's text is still being prepared. Tell the user in one line to try again in a minute. Nothing was saved. Do not save the dictated words as the quote.",
        BookTextQuoteFindStatus.NoDigitalEdition =>
            "No edition of this book in the library has text to search. Tell the user in one line, and that they can photograph the page instead (Photo quote on the book's page). Nothing was saved. Do not save the dictated words as the quote.",
        BookTextQuoteFindStatus.BookNotFound =>
            "That book is not in the library. Nothing was saved.",
        _ =>
            "No passage starts and ends with those words. Tell the user in one line and ask them to check the words. Nothing was saved. Do not save the dictated words as the quote.",
    };

    private static string QuotePreview(string text)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length <= QuotePreviewWords * 2
            ? text
            : $"{string.Join(' ', words.Take(QuotePreviewWords))} … {string.Join(' ', words.TakeLast(QuotePreviewWords))}";
    }
}

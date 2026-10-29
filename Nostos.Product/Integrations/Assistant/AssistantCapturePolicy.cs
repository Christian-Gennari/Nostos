using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Services.Library;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Integrations.Assistant;

/// <summary>
/// Deterministic capture policy: the application decides the target book and
/// source anchor, while the model only supplies the user's words. Missing book
/// or location data becomes an explicit follow-up rather than a guess.
/// </summary>
internal sealed partial class AssistantCapturePolicy(ILibraryService library)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int MaxBookCandidateChoices = 5;

    /// <param name="userMessage">
    /// The user's own message that asked for the capture. Only its words can
    /// turn on a rewrite or topics; the tool call's flags alone never do.
    /// </param>
    /// <param name="fallbackContent">The capture text when the tool call carried none (a continuation).</param>
    /// <param name="earlierUserMessages">
    /// The user's earlier messages in this conversation, newest first. A page
    /// they named there ("page 143, it starts with …") still places the capture.
    /// </param>
    public async Task<AssistantCapturePreparation> PrepareAsync(
        string argumentsJson,
        AssistantContextDto? context,
        string? userMessage,
        string? fallbackContent,
        CancellationToken ct,
        IReadOnlyList<string>? earlierUserMessages = null)
    {
        var book = await DecideBookAsync(context, ct);
        if (book.Prompt is { } bookPrompt)
        {
            return AssistantCapturePreparation.Ask(
                bookPrompt,
                "awaiting_book",
                "Ask the user for this; do not save the capture yet.");
        }

        var anchor = AboutWholeBook(argumentsJson, context)
            ? AnchorDecision.Skip()
            : DecideAnchor(
                context,
                userMessage,
                // Only a quote looked up over several turns ("2", "yes") reaches
                // back for its page; a fresh thought never inherits an old one.
                HasQuoteRef(argumentsJson) ? earlierUserMessages : null);
        if (anchor.Prompt is { } anchorPrompt)
        {
            return AssistantCapturePreparation.Ask(
                anchorPrompt,
                "awaiting_anchor",
                "Ask the user for this; do not save the capture yet.");
        }

        var args = BuildCaptureArgs(
            argumentsJson,
            context,
            anchor,
            book.BookId!.Value,
            userMessage,
            fallbackContent,
            out var quoteFidelity);

        return AssistantCapturePreparation.Ready(args, book.Title, quoteFidelity);
    }

    /// <summary>
    /// A thought about the book as a whole (a character, a theme) belongs to no
    /// position in it, so it is saved without one and nobody is asked for a page
    /// or timestamp. A quote always keeps its place.
    /// </summary>
    private static bool HasQuoteRef(string argumentsJson) =>
        !string.IsNullOrWhiteSpace(ReadString(AssistantToolArguments.ParseObject(argumentsJson), "quoteRef"));

    private static bool AboutWholeBook(string argumentsJson, AssistantContextDto? context)
    {
        var obj = AssistantToolArguments.ParseObject(argumentsJson);
        return ReadBool(obj, "wholeBook") == true
            && string.IsNullOrWhiteSpace(ReadString(obj, "selectedText"))
            && string.IsNullOrWhiteSpace(ReadString(obj, "quoteRef"))
            && string.IsNullOrWhiteSpace(context?.SelectedText);
    }

    /// <summary>
    /// Applies the user's actual continuation message to the deterministic
    /// capture context. The model is never consulted for the target book or
    /// source location; normalization here mirrors the existing product behavior
    /// for spoken page/timestamp answers.
    /// </summary>
    public AssistantContextDto ApplyContinuationAnswer(
        AssistantContextDto context,
        string kind,
        string answer,
        bool skipped)
    {
        if (skipped)
        {
            return context with
            {
                Anchor = new AssistantAnchorDto("unknown", null, false),
            };
        }

        var trimmed = answer.Trim();
        return kind switch
        {
            AssistantOrchestrator.BookPromptKind => context with
            {
                BookId = null,
                BookTitle = null,
                CaptureBookTitle = trimmed,
            },
            "physical_page" => context with
            {
                Anchor = new AssistantAnchorDto(
                    "physical_page",
                    NormalizePageAnswer(trimmed),
                    false),
            },
            // An answer that is not a time ("I don't have one") is a skip. Stored
            // as a timestamp it would read back as 0:00, a place the user never named.
            "external_audio_timestamp" => context with
            {
                Anchor = NormalizeTimestampAnswer(trimmed) is { } seconds
                    ? new AssistantAnchorDto("external_audio_timestamp", seconds, false)
                    : new AssistantAnchorDto("unknown", null, false),
            },
            _ => context,
        };
    }

    /// <summary>
    /// Applies a book follow-up without giving the model authority over the
    /// target. When the previous title produced multiple canonical candidates,
    /// a displayed 1-based choice resolves directly to that candidate id.
    /// Any other answer is treated as a fresh title and resolved normally.
    /// </summary>
    public async Task<AssistantContextDto> ApplyBookContinuationAnswerAsync(
        AssistantContextDto context,
        string answer,
        CancellationToken ct)
    {
        var trimmed = answer.Trim();

        if (!string.IsNullOrWhiteSpace(context.CaptureBookTitle)
            && int.TryParse(
                trimmed,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var selection))
        {
            var prior = await library.ResolveBookAsync(
                new LibraryResolveBookRequest(
                    Title: context.CaptureBookTitle.Trim(),
                    IncludeExternalMetadata: false),
                ct);

            if (prior.Resolution == LibraryResolution.Candidates
                && prior.Candidates is { Count: > 1 } candidates)
            {
                var choices = OrderBookCandidates(candidates);
                if (selection >= 1 && selection <= choices.Count)
                {
                    var chosen = choices[selection - 1];
                    return context with
                    {
                        BookId = chosen.BookId.ToString(),
                        BookTitle = chosen.Title,
                        CaptureBookTitle = null,
                    };
                }
            }
        }

        return context with
        {
            BookId = null,
            BookTitle = null,
            CaptureBookTitle = trimmed,
        };
    }

    private async Task<BookDecision> DecideBookAsync(AssistantContextDto? context, CancellationToken ct)
    {
        // The open book needs no resolution and cannot be overridden: whatever
        // the user says, this is where the words were written.
        if (Guid.TryParse(context?.BookId, out var open))
        {
            return BookDecision.At(open, context?.BookTitle);
        }

        var title = context?.CaptureBookTitle;
        if (string.IsNullOrWhiteSpace(title))
        {
            return BookDecision.Ask(new AssistantAnchorPromptDto(AssistantOrchestrator.BookPromptKind, AssistantOrchestrator.WhichBookQuestion));
        }

        var resolved = await library.ResolveBookAsync(
            new LibraryResolveBookRequest(Title: title.Trim(), IncludeExternalMetadata: false),
            ct);

        return resolved.Resolution switch
        {
            LibraryResolution.ExactMatch when resolved.MatchedBook is { } match =>
                BookDecision.At(match.Id, match.Title),

            // Exactly one candidate is an answer, not an ambiguity.
            LibraryResolution.Candidates when resolved.Candidates is { Count: 1 } only =>
                BookDecision.At(only[0].BookId, only[0].Title),

            // Multiple candidates are a real ambiguity, not "not found".
            // Show only a small stable set and let the user's numbered answer
            // select one canonical id on the next deterministic continuation.
            LibraryResolution.Candidates when resolved.Candidates is { Count: > 1 } many =>
                BookDecision.Ask(new AssistantAnchorPromptDto(
                    AssistantOrchestrator.BookPromptKind,
                    BuildAmbiguousBookQuestion(many))),

            _ => BookDecision.Ask(new AssistantAnchorPromptDto(
                AssistantOrchestrator.BookPromptKind,
                AssistantOrchestrator.BookNotFoundQuestion)),
        };
    }

    private static IReadOnlyList<LibraryCandidate> OrderBookCandidates(
        IReadOnlyList<LibraryCandidate> candidates) =>
        candidates
            .OrderBy(candidate => candidate.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Author ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.BookId)
            .Take(MaxBookCandidateChoices)
            .ToList();

    private static string BuildAmbiguousBookQuestion(IReadOnlyList<LibraryCandidate> candidates)
    {
        var choices = OrderBookCandidates(candidates)
            .Select((candidate, index) =>
            {
                var label = string.IsNullOrWhiteSpace(candidate.Author)
                    ? candidate.Title
                    : $"{candidate.Title} — {candidate.Author}";
                var identifier = candidate.Isbn ?? candidate.Asin;
                if (!string.IsNullOrWhiteSpace(identifier))
                {
                    label += $" ({identifier})";
                }

                return $"{index + 1}. {label}";
            });

        return $"I found more than one matching book. Reply with the number: {string.Join("; ", choices)}";
    }

    // ------------------------------------------------------------------
    // Anchor policy (deterministic)
    // ------------------------------------------------------------------

    /// <summary>
    /// Decides where a capture is anchored, or asks. The model never supplies
    /// the anchor: it is derived from what the app actually knows.
    /// </summary>
    private JsonElement BuildCaptureArgs(
        string argumentsJson,
        AssistantContextDto? context,
        AnchorDecision decision,
        Guid bookId,
        string? userMessage,
        string? fallbackContent,
        out bool quoteFidelity)
    {
        quoteFidelity = false;

        var obj = AssistantToolArguments.ParseObject(argumentsJson);

        // The book is the APP's, exactly like the anchor, and it is now the ONLY
        // source: the open book, or the book the user named when the capture
        // asked. Measured against the live gateway with Pride and Prejudice open:
        // the model searched notes, found one about the same subject in another
        // book, and filed the thought there — and because the acknowledgement is
        // built from the ambient context, the confirmation then named a book the
        // note was not filed against. Both were wrong, and neither was visible.
        // An open book is a fact; a book the model went and found is a guess.
        obj["bookId"] = bookId.ToString();

        // A passage found by its first and last words is the book's own text,
        // which the capture reads from the ref. Neither the model's copy of it
        // nor a reader selection replaces it.
        var fromBookText = !string.IsNullOrWhiteSpace(ReadString(obj, "quoteRef"));
        var selectedText = fromBookText
            ? null
            : ReadString(obj, "selectedText") ?? context?.SelectedText;
        if (fromBookText)
        {
            obj.Remove("selectedText");
        }
        else if (!string.IsNullOrWhiteSpace(selectedText))
        {
            obj["selectedText"] = selectedText;
        }

        var content = ReadString(obj, "content");
        if (string.IsNullOrWhiteSpace(content)
            && string.IsNullOrWhiteSpace(selectedText)
            && !fromBookText
            && !string.IsNullOrWhiteSpace(fallbackContent))
        {
            // A continuation answer supplies only the missing book/anchor. The
            // original user turn remains the capture when a provider omitted
            // content from the tool arguments that created the continuation.
            content = fallbackContent;
            obj["content"] = fallbackContent;
        }

        // The orchestrator owns the anchor. A guessed one would file the note
        // against the wrong place, which is worse than none.
        obj["sourceAnchorKind"] = decision.Kind;
        obj["sourceAnchorValue"] = decision.Value;
        obj["anchorVerified"] = decision.Verified;

        // Ask Nostos keeps the user's words exactly as given unless the USER
        // asked for a rewrite. Whatever `processingMode` the tool call carried
        // is overwritten on purpose; the model's `rewrite` flag counts only when
        // the user's own message asks for one, so the model can never decide
        // on its own to rewrite a thought. The exact words stay as the original.
        var rewrite = ReadBool(obj, "rewrite") == true
            && AssistantRewriteIntent.Requested(userMessage);
        obj["processingMode"] = rewrite
            ? ThoughtProcessingModes.Clarify
            : ThoughtProcessingModes.Verbatim;

        // Topics come with a rewrite, or when the user asked for them. A plain
        // capture stays the user's words alone and waits in Unsorted.
        if (!rewrite && !AssistantRewriteIntent.TopicsMentioned(userMessage))
        {
            obj.Remove("topics");
        }

        if (string.Equals(decision.Kind, "epub_cfi", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(decision.Value))
        {
            obj["cfiRange"] = decision.Value;
        }
        else if (decision.Verified
            && string.Equals(decision.Kind, "pdf_page", StringComparison.Ordinal)
            && int.TryParse(decision.Value, CultureInfo.InvariantCulture, out var pdfPage)
            && pdfPage > 0)
        {
            // Reader note navigation still consumes the legacy CfiRange field.
            // Keep the typed assistant anchor as canonical provenance, but bridge
            // a verified PDF page into the same JSON location shape PdfReader
            // already emits from getCurrentLocation(). This makes new assistant
            // captures navigable without inventing a second PDF location format.
            obj["cfiRange"] = JsonSerializer.Serialize(
                new { pageNumber = pdfPage, yPercent = 0, rects = Array.Empty<object>() },
                JsonOptions);
        }

        if (!string.IsNullOrWhiteSpace(selectedText) && !decision.Verified)
        {
            quoteFidelity = true;
            if (!string.IsNullOrWhiteSpace(content)
                && !content.Contains(AssistantOrchestrator.QuoteFidelityNote, StringComparison.Ordinal))
            {
                obj["content"] = $"{content}\n\n_{AssistantOrchestrator.QuoteFidelityNote}_";
            }
        }

        return JsonSerializer.SerializeToElement(obj, JsonOptions);
    }

    private static AnchorDecision DecideAnchor(
        AssistantContextDto? context,
        string? userMessage = null,
        IReadOnlyList<string>? earlierUserMessages = null)
    {
        // An anchor the client supplied is an answer (or an explicit skip).
        if (context?.Anchor is { } anchor)
        {
            if (string.IsNullOrWhiteSpace(anchor.Kind)
                || string.Equals(anchor.Kind, "unknown", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(anchor.Value))
            {
                return AnchorDecision.Skip();
            }

            return AnchorDecision.At(anchor.Kind, anchor.Value, anchor.Verified);
        }

        // No anchor at all: derive one from an app-known location.
        if (context is not null)
        {
            if (!string.IsNullOrWhiteSpace(context.EpubCfi))
            {
                return AnchorDecision.At("epub_cfi", context.EpubCfi, verified: true);
            }

            if (context.PdfPage is not null)
            {
                return AnchorDecision.At(
                    "pdf_page",
                    context.PdfPage.Value.ToString(CultureInfo.InvariantCulture),
                    verified: true);
            }

            if (context.AudioTimestamp is not null)
            {
                return AnchorDecision.At(
                    "audio_timestamp",
                    context.AudioTimestamp.Value.ToString(CultureInfo.InvariantCulture),
                    verified: true);
            }
        }

        // A paper book has no reader to ask, but the user may already have
        // said where they are. Their words, never the model's, are the page.
        if (string.Equals(context?.BookFormat, "physical", StringComparison.OrdinalIgnoreCase)
            && PageNamedBy(userMessage, earlierUserMessages) is { } namedPage)
        {
            return AnchorDecision.At("physical_page", namedPage, verified: false);
        }

        // The format cannot supply a location: ask.
        var prompt = AnchorPromptFor(context);
        return prompt is null ? AnchorDecision.Skip() : AnchorDecision.Ask(prompt);
    }

    private static AssistantAnchorPromptDto? AnchorPromptFor(AssistantContextDto? context)
    {
        if (context is null)
        {
            return null;
        }

        if (string.Equals(context.BookFormat, "physical", StringComparison.OrdinalIgnoreCase))
        {
            return new AssistantAnchorPromptDto("physical_page", "What page are you on?");
        }

        // An in-app audio reader publishes its own timestamp; only an external
        // audiobook (no in-app reader open) needs to be asked.
        if (string.Equals(context.BookFormat, "audiobook", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(context.ReaderType, "audio", StringComparison.OrdinalIgnoreCase))
        {
            return new AssistantAnchorPromptDto(
                "external_audio_timestamp",
                "What's the current timestamp?");
        }

        return null;
    }

    public static string BuildAcknowledgement(string? bookTitle, bool quoteFidelity, bool rewritten = false)
    {
        var text = string.IsNullOrWhiteSpace(bookTitle)
            ? "Saved."
            : $"Saved to {bookTitle}.";

        if (rewritten)
        {
            text += " I tidied the wording; your own words are kept as the original.";
        }

        // The response contract carries no undo field; the note id and the note
        // itself are the affordance, so point at them.
        text += " You can undo this from your notes.";

        if (quoteFidelity)
        {
            text += $" {AssistantOrchestrator.QuoteFidelityNote}";
        }

        return text;
    }


    [GeneratedRegex(
        @"(?<![\p{L}\d])(?:page|pg\.?|p\.|sida|sidan|s\.)\s*(\d{1,5})(?!\d)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NamedPageRegex();

    /// <summary>
    /// The one page the user named ("on page 143", "sidan 12"), in this message
    /// or, failing that, the latest earlier one that named one. Two different
    /// pages in one message are no answer.
    /// </summary>
    internal static string? PageNamedBy(string? userMessage, IReadOnlyList<string>? earlierUserMessages)
    {
        foreach (var message in new[] { userMessage }.Concat(earlierUserMessages ?? []).Take(4))
        {
            if (string.IsNullOrWhiteSpace(message))
                continue;

            var pages = NamedPageRegex().Matches(message)
                .Select(match => match.Groups[1].Value.TrimStart('0'))
                .Where(page => page.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (pages.Count == 1)
                return pages[0];
            if (pages.Count > 1)
                return null;
        }

        return null;
    }

    private static string NormalizePageAnswer(string answer)
    {
        var cleaned = Regex.Replace(answer, @"[.\s]+$", string.Empty);
        var match = Regex.Match(
            cleaned,
            @"^(?:page|p\.?)\s*(\d+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value : cleaned;
    }

    /// <summary>
    /// Seconds for an h:mm:ss / m:ss / s answer; any other answer with a number
    /// in it is kept as the user said it. Null when there is no number at all:
    /// that is not a time, whatever the words.
    /// </summary>
    private static string? NormalizeTimestampAnswer(string answer)
    {
        if (!answer.Any(char.IsAsciiDigit))
            return null;

        var parts = answer.Trim().Split(':');
        if (parts.Length is < 1 or > 3)
            return answer.Trim();

        long seconds = 0;
        foreach (var part in parts)
        {
            if (!long.TryParse(
                    part.Trim(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var value)
                || value < 0)
            {
                return answer.Trim();
            }

            try
            {
                seconds = checked(seconds * 60 + value);
            }
            catch (OverflowException)
            {
                return answer.Trim();
            }
        }

        return seconds.ToString(CultureInfo.InvariantCulture);
    }

    private static bool? ReadBool(JsonObject obj, string name)
    {
        if (!obj.TryGetPropertyValue(name, out var node) || node is null)
            return null;

        try
        {
            return node.GetValue<bool>();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonObject obj, string name)
    {
        if (!obj.TryGetPropertyValue(name, out var node) || node is null)
            return null;

        try
        {
            return node.GetValue<string>();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private sealed record AnchorDecision(
        string Kind,
        string? Value,
        bool Verified,
        AssistantAnchorPromptDto? Prompt)
    {
        public static AnchorDecision At(string kind, string? value, bool verified) =>
            new(kind, value, verified, null);

        public static AnchorDecision Skip() => new("unknown", null, false, null);

        public static AnchorDecision Ask(AssistantAnchorPromptDto prompt) =>
            new("unknown", null, false, prompt);
    }

    private sealed record BookDecision(Guid? BookId, string? Title, AssistantAnchorPromptDto? Prompt)
    {
        public static BookDecision At(Guid bookId, string? title) => new(bookId, title, null);

        public static BookDecision Ask(AssistantAnchorPromptDto prompt) => new(null, null, prompt);
    }
}

internal sealed record AssistantCapturePreparation(
    JsonElement? Arguments,
    string? BookTitle,
    bool QuoteFidelity,
    AssistantAnchorPromptDto? Prompt,
    string? PromptStatus,
    string? PromptMessage)
{
    public static AssistantCapturePreparation Ready(
        JsonElement arguments,
        string? bookTitle,
        bool quoteFidelity) =>
        new(arguments, bookTitle, quoteFidelity, null, null, null);

    public static AssistantCapturePreparation Ask(
        AssistantAnchorPromptDto prompt,
        string status,
        string message) =>
        new(null, null, false, prompt, status, message);
}

using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nostos.Backend.Services.Library;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Integrations.Assistant;

/// <summary>
/// Deterministic capture policy: the application decides the target book and
/// source anchor, while the model only supplies the user's words. Missing book
/// or location data becomes an explicit follow-up rather than a guess.
/// </summary>
internal sealed class AssistantCapturePolicy(ILibraryService library)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<AssistantCapturePreparation> PrepareAsync(
        string argumentsJson,
        AssistantContextDto? context,
        string processingMode,
        CancellationToken ct)
    {
        var book = await DecideBookAsync(context, ct);
        if (book.Prompt is { } bookPrompt)
        {
            return AssistantCapturePreparation.Ask(
                bookPrompt,
                "awaiting_book",
                "Ask the user for this; do not save the capture yet.");
        }

        var anchor = DecideAnchor(context);
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
            processingMode,
            out var quoteFidelity);

        return AssistantCapturePreparation.Ready(args, book.Title, quoteFidelity);
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
                CaptureBookTitle = trimmed,
            },
            "physical_page" => context with
            {
                Anchor = new AssistantAnchorDto(
                    "physical_page",
                    NormalizePageAnswer(trimmed),
                    false),
            },
            "external_audio_timestamp" => context with
            {
                Anchor = new AssistantAnchorDto(
                    "external_audio_timestamp",
                    NormalizeTimestampAnswer(trimmed),
                    false),
            },
            _ => context,
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

            // Exactly one candidate is an answer, not an ambiguity. More than
            // one is the user's to settle, so it becomes the same question again.
            LibraryResolution.Candidates when resolved.Candidates is { Count: 1 } only =>
                BookDecision.At(only[0].BookId, only[0].Title),

            _ => BookDecision.Ask(new AssistantAnchorPromptDto(AssistantOrchestrator.BookPromptKind, AssistantOrchestrator.BookNotFoundQuestion)),
        };
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
        string mode,
        out bool quoteFidelity)
    {
        quoteFidelity = false;

        var obj = ParseObject(argumentsJson);

        // The book is the APP's, exactly like the anchor, and it is now the ONLY
        // source: the open book, or the book the user named when the capture
        // asked. Measured against the live gateway with Pride and Prejudice open:
        // the model searched notes, found one about the same subject in another
        // book, and filed the thought there — and because the acknowledgement is
        // built from the ambient context, the confirmation then named a book the
        // note was not filed against. Both were wrong, and neither was visible.
        // An open book is a fact; a book the model went and found is a guess.
        obj["bookId"] = bookId.ToString();

        var selectedText = ReadString(obj, "selectedText") ?? context?.SelectedText;
        if (!string.IsNullOrWhiteSpace(selectedText))
        {
            obj["selectedText"] = selectedText;
        }

        // The orchestrator owns the anchor. A guessed one would file the note
        // against the wrong place, which is worse than none.
        obj["sourceAnchorKind"] = decision.Kind;
        obj["sourceAnchorValue"] = decision.Value;
        obj["anchorVerified"] = decision.Verified;

        // The stored setting is the ONLY source of the mode. Whatever
        // `processingMode` the tool call carried is overwritten on purpose: the
        // owner chose the mode once, so neither a per-capture request nor the
        // model may change it. It rides on the canonical `processingMode`
        // argument the capability's reader already accepts, so the frozen
        // capability signature does not change (issue #262 §7).
        obj["processingMode"] = mode;

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

        var content = ReadString(obj, "content");
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

    private static AnchorDecision DecideAnchor(AssistantContextDto? context)
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

    public static string BuildAcknowledgement(string? bookTitle, bool quoteFidelity)
    {
        var text = string.IsNullOrWhiteSpace(bookTitle)
            ? "Saved."
            : $"Saved to {bookTitle}.";

        // The response contract carries no undo field; the note id and the note
        // itself are the affordance, so point at them.
        text += " You can undo this from your notes.";

        if (quoteFidelity)
        {
            text += $" {AssistantOrchestrator.QuoteFidelityNote}";
        }

        return text;
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

    private static string NormalizeTimestampAnswer(string answer)
    {
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


    private static JsonObject ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new JsonObject();

        try
        {
            return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
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

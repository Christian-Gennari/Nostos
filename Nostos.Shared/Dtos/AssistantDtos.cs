namespace Nostos.Shared.Dtos;

// --- ASSISTANT BRIDGE WIRE CONTRACT (issue #261 §3, decision D2) ---
// The Angular assistant shell sends one structured turn and receives the reply,
// any deterministic follow-up question, non-mutating suggestions, and a pending
// plan to approve later. The context DTO mirrors the frontend context service
// field-for-field (the names in the 261-S2 brief); it is deliberately not a
// superset and carries no field the client does not already resolve.

/// <summary>
/// One assistant turn. New callers use <paramref name="ConversationId"/> and
/// <paramref name="TurnId"/>; the legacy ClientId / IdempotencyKey pair remains
/// the append-only compatibility alias. Canonical mutations derive receipt keys
/// from the stable logical TurnId plus the capability and a deterministic
/// fingerprint of its effective arguments (server-prepared for capture), so a
/// transport retry that reorders or changes the model's tool sequence cannot
/// create a second write or replay a different command's receipt.
/// </summary>
public sealed record AssistantTurnRequest(
    string ClientId,
    string IdempotencyKey,
    string Message,
    AssistantContextDto Context,
    // APPENDED (positional record): the post-processing mode the composer used
    // to send per capture (issue #262 §7). It is now IGNORED: the capture mode
    // comes from the stored assistant setting, which the owner chooses once in
    // Settings. It is kept for wire compatibility — the record is append-only
    // and other callers may still send it — so removing it is a separate
    // decision, not this change.
    string? ProcessingMode = null,
    // APPENDED (positional record): the recent turns the client remembers, so the
    // model can follow the exchange instead of rebuilding it from nothing each
    // turn (issue #286). Untrusted, client-supplied text: it travels only as
    // ordinary user/assistant turns and is never stored server-side.
    IReadOnlyList<AssistantHistoryMessageDto>? History = null,
    // APPENDED (#560): explicit v3 identities. ClientId / IdempotencyKey remain
    // the compatibility aliases for older callers; new clients send the same
    // values here so a logical turn owns one stable mutation identity.
    string? ConversationId = null,
    string? TurnId = null,
    // APPENDED (#560): when present, Message is the user's actual answer to a
    // server-held deterministic capture continuation. The original capture text
    // is never replayed as this turn's message.
    string? ContinuationId = null,
    bool ContinuationSkipped = false);

/// <summary>
/// One remembered turn sent by the client (issue #286). <c>Role</c> is
/// <c>"user"</c> or <c>"assistant"</c>; any other role is ignored by the
/// orchestrator and never promoted to a system message. <c>Text</c> is untrusted
/// user-supplied text.
/// </summary>
public sealed record AssistantHistoryMessageDto(
    string Role,
    string Text,
    // APPENDED (#561): compact historical context from when this user turn
    // happened. It is untrusted conversational metadata only; the backend may
    // use it to resolve references but never as mutation authorization.
    AssistantHistoricalContextDto? Context = null,
    // APPENDED (#561): compact identities for source evidence surfaced during
    // the historical turn. The excerpt itself is deliberately not repeated.
    IReadOnlyList<AssistantHistoricalEvidenceDto>? Evidence = null,
    // APPENDED (#561): capabilities the server reported as completed on that
    // historical turn. This is reference context only; canonical current state
    // must still be read before acting.
    IReadOnlyList<string>? Actions = null,
    // APPENDED (#561): identity of a note captured by this turn, when one exists.
    // It helps resolve later "that note" references but carries no write authority.
    string? CapturedNoteId = null,
    // APPENDED (#565): exact #562 evidence handles associated with the historical
    // turn. These are inert references only; any later use must re-read canonical
    // Nostos state through knowledge_read_evidence.
    IReadOnlyList<AssistantEvidenceHandleDto>? EvidenceHandles = null);

/// <summary>
/// Compact application snapshot attached to an older user turn. Current
/// <see cref="AssistantContextDto"/> remains authoritative for the present turn;
/// this snapshot is only a reference-resolution aid.
/// </summary>
public sealed record AssistantHistoricalContextDto(
    string? Surface = null,
    string? BookId = null,
    string? BookTitle = null,
    string? BrainReviewNoteId = null,
    string? Topic = null,
    string? CollectionId = null);

/// <summary>
/// Compact identity for previously surfaced imported-book evidence. It carries
/// no excerpt and grants no authority; a later factual claim should re-read the
/// canonical source through the normal retrieval capability.
/// </summary>
public sealed record AssistantHistoricalEvidenceDto(
    string BookId,
    string BookTitle,
    string SourceSha256,
    IReadOnlyList<AssistantSourceLocatorDto>? Locators = null);

/// <summary>
/// What the user is looking at. Mirrors the frontend
/// <c>AssistantContextService</c> snapshot; nulls are the norm and are never
/// guessed at.
/// </summary>
public sealed record AssistantContextDto(
    string Surface,
    string Route,
    string? BookId = null,
    string? BookTitle = null,
    string? BookFormat = null,
    string? ReaderType = null,
    string? EpubCfi = null,
    int? PdfPage = null,
    double? AudioTimestamp = null,
    string? AudioChapter = null,
    string? SelectedText = null,
    string? BrainReviewNoteId = null,
    string? Topic = null,
    string? CollectionId = null,
    AssistantAnchorDto? Anchor = null,
    // APPENDED (positional record): legacy compatibility for the pre-#560 book
    // follow-up shape. V3 clients send the title as the actual Message tied to a
    // server continuation; the capture policy sets this field internally before
    // canonical library resolution. The model never chooses the target book.
    string? CaptureBookTitle = null);

/// <summary>
/// A resolved source anchor. <c>Verified</c> is true ONLY for an anchor the app
/// acquired itself; a value the user typed is never verified.
/// </summary>
public sealed record AssistantAnchorDto(
    string Kind,
    string? Value = null,
    bool Verified = false);

/// <summary>
/// A normal turn result. <c>Suggestions</c> is always present (empty when the
/// model proposed none); <c>PendingPlan</c> is non-null only when a
/// PlanAndAct tool was requested and is waiting for explicit approval.
/// </summary>
public sealed record AssistantTurnResponse(
    string Reply,
    string? Acknowledgement,
    AssistantAnchorPromptDto? AnchorPrompt,
    IReadOnlyList<AssistantSuggestionDto> Suggestions,
    AssistantPendingPlanDto? PendingPlan,
    // APPENDED (positional record): the id of the note a capture created this
    // turn, so the surface can show its raw transcript and offer restore
    // (issue #262 §8). Null when the turn captured nothing.
    string? CapturedNoteId = null,
    // APPENDED (positional record): capabilities that actually completed as
    // immediate Act calls during this turn. This is execution truth from the
    // registry, not model narration, so clients can react to a successful
    // mutation without parsing prose.
    IReadOnlyList<string>? ExecutedCapabilities = null,
    // APPENDED: source references produced by successful server-side book-text
    // retrieval. These are never model-authored citations; every locator comes
    // from the indexed exact source revision.
    IReadOnlyList<AssistantSourceReferenceDto>? Sources = null,
    // APPENDED (#560): deterministic continuation failures are turn results,
    // not provider failures. They are typed so a stale/wrong continuation never
    // falls back to guessing or mutation.
    AssistantTurnErrorDto? Error = null,
    // APPENDED (#565): canonical evidence found/used by this exact turn. Stable
    // handles are authoritative; excerpt/labels are bounded display data only.
    IReadOnlyList<AssistantEvidenceReferenceDto>? Evidence = null,
    // APPENDED (#608): when this turn unambiguously resolved/read/searched one
    // book, return that canonical identity so the client can attach it to this
    // turn's existing historical context. It is conversational scope only and
    // grants no write authority.
    AssistantResolvedBookDto? ResolvedBook = null);

/// <summary>
/// One canonical book identity established by a successful current-turn read.
/// The client may carry it forward as historical conversation scope; current
/// application context and explicit tool arguments still outrank it.
/// </summary>
public sealed record AssistantResolvedBookDto(Guid BookId, string BookTitle);

/// <summary>
/// A stable #562 reference to canonical evidence. It grants no write authority.
/// Book-text handles include the exact source revision + extractor + ordinal so
/// a later turn can fail closed rather than trusting a stale excerpt.
/// </summary>
public sealed record AssistantEvidenceHandleDto(
    string Kind,
    Guid? NoteId = null,
    Guid? TopicId = null,
    Guid? BookId = null,
    string? SourceSha256 = null,
    string? ExtractorVersion = null,
    int? Ordinal = null);

/// <summary>
/// Bounded presentation data paired with a canonical evidence handle. This is a
/// source/evidence relationship for the turn, not a claim-level citation.
/// </summary>
public sealed record AssistantEvidenceReferenceDto(
    AssistantEvidenceHandleDto Handle,
    string Label,
    string? Excerpt = null,
    string? BookTitle = null,
    string? BookAuthor = null,
    string? Format = null,
    IReadOnlyList<AssistantSourceLocatorDto>? Locators = null);

/// <summary>
/// One grounded imported-book passage surfaced by Ask Nostos. The excerpt is
/// bounded by the retrieval service; locators point into the exact source
/// revision identified by <paramref name="SourceSha256"/>.
/// </summary>
public sealed record AssistantSourceReferenceDto(
    Guid BookId,
    string BookTitle,
    string? BookAuthor,
    string Format,
    string SourceSha256,
    string Excerpt,
    IReadOnlyList<AssistantSourceLocatorDto> Locators);

/// <summary>
/// Transport-only representation of a typed source locator. PDF physical page
/// index is zero-based. EPUB resource/spine/offset is the stable fallback when
/// an epub.js CFI is unavailable.
/// </summary>
public sealed record AssistantSourceLocatorDto(
    string Type,
    int? PdfPageIndex = null,
    string? PdfPageLabel = null,
    int? EpubSpineIndex = null,
    string? EpubResourceHref = null,
    string? EpubCfi = null,
    int? StartTextOffset = null,
    int? EndTextOffset = null);

/// <summary>
/// The deterministic follow-up a capture needs before it can be saved. Its
/// <c>Kind</c> says what is being asked for: <c>"book"</c> when no book is open
/// and the app cannot know which one the thought belongs to, otherwise a source
/// location the format cannot supply (<c>"physical_page"</c>,
/// <c>"external_audio_timestamp"</c>). Nothing is saved until it is answered; an
/// explicitly skipped location saves as <c>unknown</c>.
/// </summary>
public sealed record AssistantAnchorPromptDto(
    string Kind,
    string Question,
    // APPENDED (#560): identifies the bounded server-held deterministic capture
    // continuation. Null only for legacy callers/tests that construct the DTO.
    string? ContinuationId = null);

/// <summary>
/// A deterministic turn-level failure. Used for continuation expiry/mismatch
/// and other server-known refusal states where no model/tool mutation ran.
/// </summary>
public sealed record AssistantTurnErrorDto(string Code, string Message);

/// <summary>
/// One safe, product-owned activity update for an in-flight turn. Code/message
/// are chosen by Nostos from known operations; raw model/tool payloads never
/// enter this contract.
/// </summary>
public sealed record AssistantTurnActivityDto(string Code, string Message);

/// <summary>
/// A terminal product failure. Retryable describes transport/provider recovery,
/// not whether a canonical mutation should be replayed.
/// </summary>
public sealed record AssistantTurnFailureDto(
    string Code,
    string Message,
    bool Retryable = false);

/// <summary>
/// One ordered event in the streamed turn transport. Sequence is monotonic
/// within the logical TurnId. Response is present only when the server has
/// terminal turn truth worth preserving (including already-committed actions).
/// </summary>
public sealed record AssistantTurnEventDto(
    string TurnId,
    long Sequence,
    string Kind,
    AssistantTurnActivityDto? Activity = null,
    AssistantTurnFailureDto? Failure = null,
    AssistantTurnResponse? Response = null);

public static class AssistantTurnEventKinds
{
    public const string Started = "started";
    public const string Activity = "activity";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

/// <summary>Stop exactly one active logical turn in one conversation.</summary>
public sealed record AssistantTurnCancelRequest(string ConversationId, string TurnId);

/// <summary>
/// Result of a stop request. Not-active is intentionally harmless: a stale stop
/// must never attach itself to a newer turn.
/// </summary>
public sealed record AssistantTurnCancelResponse(bool Accepted, string State);

/// <summary>
/// A non-mutating proposal. The user selects one; the assistant never links or
/// applies a suggestion on its own.
/// </summary>
public sealed record AssistantSuggestionDto(
    string Kind,
    string Label,
    string Reason,
    string? Value = null,
    // The canonical target note for this proposal; never infer it from the
    // current panel after the turn has completed or the user has navigated.
    string? NoteId = null);

/// <summary>
/// A plan the assistant will not run inline. <c>ApprovalToken</c> is bound to
/// this <c>PlanId</c> and is required by
/// <c>POST /api/assistant/plan/approve</c>; a missing or mismatched pair is
/// refused and mutates nothing.
/// </summary>
public sealed record AssistantPendingPlanDto(
    string PlanId,
    string Summary,
    IReadOnlyList<AssistantPlanStepDto> Steps,
    string ApprovalToken);

/// <summary>One ordered step of a pending plan; executed exactly as stored.</summary>
public sealed record AssistantPlanStepDto(
    string Capability,
    string Summary,
    string ArgumentsJson);

/// <summary>Approve exactly one pending plan.</summary>
public sealed record AssistantPlanApproveRequest(
    string PlanId,
    string? ApprovalToken);

/// <summary>
/// The result of executing an approved plan. Failures are data
/// (<c>ErrorCode</c>), never hidden behind a 200-shaped success.
/// </summary>
public sealed record AssistantPlanApproveResponse(
    bool Success,
    string? ErrorCode,
    string? ErrorMessage,
    IReadOnlyList<AssistantPlanStepOutcomeDto> Steps);

/// <summary>Outcome of one executed plan step, in plan order.</summary>
public sealed record AssistantPlanStepOutcomeDto(
    string Capability,
    bool Success,
    string? ErrorCode,
    string? ErrorMessage,
    object? Data);

// --- ASSISTANT SETTINGS (issue #262 §7) ---
// The settings the owner picks once, stored on the server. The wire contract is
// frozen: GET/PUT /api/settings/assistant carry exactly the one field below.

/// <summary>
/// The stored assistant settings as returned to the client. The single field is
/// the effective capture post-processing mode; it is <c>"verbatim"</c> when the
/// owner has never chosen.
/// </summary>
public sealed record AssistantSettingsResponse(string CaptureProcessingMode);

/// <summary>
/// A settings update. <c>CaptureProcessingMode</c> must be one of the supported
/// modes; anything else (including absent) is refused with
/// <c>invalid_processing_mode</c> and stores nothing.
/// </summary>
public sealed record AssistantSettingsUpdateRequest(string? CaptureProcessingMode);

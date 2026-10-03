namespace Nostos.Backend.Services.Notes;

/// <summary>
/// Outcome of a note mutation. Deliberately carries no HTTP type and no status
/// code: the endpoint maps <see cref="ErrorCode"/> to a status and body, the
/// same separation <c>ILibraryService</c> keeps for the library domain.
/// </summary>
public sealed record NoteCommandResult<T>(
    bool Success,
    string? ErrorCode,
    string? ErrorMessage,
    T? Value)
{
    public static NoteCommandResult<T> Ok(T value) => new(true, null, null, value);

    public static NoteCommandResult<T> Fail(string errorCode, string errorMessage) =>
        new(false, errorCode, errorMessage, default);
}

/// <summary>
/// The typed failure codes <see cref="INoteService"/> emits. Exact strings are
/// part of the service contract.
/// </summary>
public static class NoteErrorCodes
{
    public const string BookNotFound = "book_not_found";
    public const string NoteNotFound = "note_not_found";
    public const string EmptyNote = "empty_note";
    public const string TopicNotFound = "topic_not_found";

    // Post-processing (issue #262 §7, §8): an unknown mode is a typed refusal
    // rather than a silent downgrade to verbatim, and a restore of a note that
    // never kept a raw transcript is a conflict, not a success.
    public const string InvalidProcessingMode = "invalid_processing_mode";
    public const string NoRawTranscript = "no_raw_transcript";

    // The idempotency key pair is all-or-nothing: a request carrying neither
    // keeps the non-idempotent behaviour, while a request carrying only one
    // half (or an out-of-bounds value) can never be replayed exactly once and
    // is therefore rejected rather than silently treated as a new command.
    public const string InvalidIdempotency = "invalid_idempotency";
}

/// <summary>
/// Canonical in-process capture request (issue #260 §3). Later streams (the
/// assistant tool bridge, 261-S2) call <see cref="INoteService.CaptureAsync"/>
/// with this shape instead of hand-building a <c>CreateNoteDto</c>; the HTTP
/// surface only ever varies the optional key pair.
/// </summary>
public sealed record CaptureNoteRequest(
    Guid BookId,
    string Content,
    string? CfiRange = null,
    string? SelectedText = null,
    // Capture provenance (issue #260 §2, §4): the raw capture kept alongside
    // the polished Content, plus the anchor recording where it came from.
    string? RawContent = null,
    string CaptureSource = "text",
    string ProcessingMode = "verbatim",
    string SourceAnchorKind = "unknown",
    string? SourceAnchorValue = null,
    bool AnchorVerified = false,
    // Both optional: absent => today's (non-idempotent) behaviour; present =>
    // exactly-once keyed on the (ClientId, IdempotencyKey) pair.
    string? ClientId = null,
    string? IdempotencyKey = null);

/// <summary>
/// Read model for the note-review flow (issue #261): what the note says, which
/// book it belongs to, and the topics it is currently linked to. Service
/// internal — no client contract is defined for it yet.
/// </summary>
public sealed record NoteReviewDto(
    Guid NoteId,
    Guid BookId,
    string? BookTitle,
    string Content,
    string? SelectedText,
    IReadOnlyList<string> TopicNames);

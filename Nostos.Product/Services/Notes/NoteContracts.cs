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

    // Only an imported highlight can be given a reader location after the
    // fact; every other note records its own when it is made.
    public const string LocationNotAllowed = "location_not_allowed";

    // A highlight colour is one of the four pen names; anything else is refused
    // rather than stored and silently drawn as the default.
    public const string InvalidHighlightColour = "invalid_highlight_colour";

    // A passage needs a title and both ends in its book's own terms; one that
    // is incomplete, or too large to be a passage, is refused (issue #1014).
    public const string InvalidPassage = "invalid_passage";
}

/// <summary>
/// What kind of thing a note is (issue #1014). A passage is a very big
/// highlight, read again in the passage viewer rather than drawn in the book.
/// </summary>
public static class NoteKinds
{
    public const string Note = "note";
    public const string Passage = "passage";

    /// <summary>The canonical kind, or null for an unknown one.</summary>
    public static string? Normalize(string? value)
    {
        var name = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(name)) return Note;
        return name is Note or Passage ? name : null;
    }
}

/// <summary>
/// The highlighter pens a highlight can be stored with (issue #861). A colour
/// is a NAME at every layer above CSS; the fill itself lives in the
/// frontend's <c>--highlight-&lt;name&gt;</c> tokens.
/// </summary>
public static class NoteHighlightColours
{
    /// <summary>The default pen, and what a note with no recorded colour is drawn in.</summary>
    public const string Sage = "sage";

    public const string Amber = "amber";

    /// <summary>The pen a new highlight gets when nothing chose one, such as an import.</summary>
    public const string Default = Sage;

    public static readonly IReadOnlyList<string> All = [Sage, Amber, "blue", "rose"];

    /// <summary>
    /// The canonical name for <paramref name="value"/>, or null when it is not
    /// one of the pens.
    /// </summary>
    public static string? Normalize(string? value)
    {
        var name = value?.Trim().ToLowerInvariant();
        return name is not null && All.Contains(name) ? name : null;
    }
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
    string? IdempotencyKey = null,
    // APPENDED (issue #861): the highlighter pen, by name.
    string? HighlightColour = null);

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
    IReadOnlyList<string> TopicNames,
    IReadOnlyList<Guid> TopicIds);

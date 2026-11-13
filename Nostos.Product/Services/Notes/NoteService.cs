using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Mapping;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Services.Notes;

/// <summary>
/// Canonical note service (issue #260 §1, §5). Owns every note write —
/// including idempotency and topic processing — so an endpoint (or a future
/// assistant capability) can never orchestrate the repositories itself. The
/// scoped <see cref="NostosDbContext"/> is shared with the repositories and the
/// <see cref="NoteProcessorService"/>, so a create/update stays one unit of
/// work exactly as it did when the handler held the repositories.
///
/// Reads, search, topic linking and the raw-transcript lifecycle are delegated
/// to focused collaborators (issue #1619); this class keeps the commands and
/// their validation.
/// </summary>
public sealed class NoteService : INoteService
{
    // The SQLite CHECK on NoteCommandReceipts.ResultJson caps the payload at
    // this many characters; a serialised result that would exceed it is a bug
    // we must surface, never silently truncate.
    private const int MaxReceiptJsonLength = 131072;

    // Stored in NoteCommandReceipt.Command: the captured note's command kind,
    // distinct from any library command so a note receipt can never be read as
    // a library one (separate tables enforce that anyway).
    private const string CaptureCommand = "CaptureNote";

    // Process-local gate serializes keyed captures (mirror of LibraryService):
    // the assistant tool loop runs in-process and can fire concurrent captures,
    // so the gate is what makes the second same-key call wait for the first
    // rather than racing it. Static so the gate is shared across scoped
    // service instances.
    private static readonly SemaphoreSlim CommandGate = new(1, 1);
    private static readonly JsonSerializerOptions ReceiptJson = new(JsonSerializerDefaults.Web);

    private readonly INoteRepository _notes;
    private readonly IBookRepository _books;
    private readonly NoteProcessorService _processor;
    private readonly NostosDbContext _db;
    private readonly NoteReadService _reads;
    private readonly NoteSearchService _search;
    private readonly NoteTopicLinkService _links;
    private readonly NoteProcessingLifecycleService _lifecycle;

    public NoteService(
        INoteRepository notes,
        IBookRepository books,
        NoteProcessorService processor,
        NostosDbContext db,
        NoteReadService reads,
        NoteSearchService search,
        NoteTopicLinkService links,
        NoteProcessingLifecycleService lifecycle)
    {
        _notes = notes;
        _books = books;
        _processor = processor;
        _db = db;
        _reads = reads;
        _search = search;
        _links = links;
        _lifecycle = lifecycle;
    }

    // ------------------------------------------------------------------
    // Read-only: delegated to NoteReadService
    // ------------------------------------------------------------------

    public Task<IReadOnlyList<NoteDto>> GetByBookAsync(Guid bookId, CancellationToken ct = default) =>
        _reads.GetByBookAsync(bookId, ct);

    public Task<NoteSearchHitDto?> GetAsync(Guid noteId, CancellationToken ct = default) =>
        _reads.GetAsync(noteId, ct);

    public Task<IReadOnlyList<NoteSearchHitDto>> GetManyAsync(
        IReadOnlyCollection<Guid> noteIds, CancellationToken ct = default) =>
        _reads.GetManyAsync(noteIds, ct);

    /// <summary>The most notes one <see cref="GetManyAsync"/> call answers for.</summary>
    public const int MaxNotesByIds = NoteReadService.MaxNotesByIds;

    public Task<NoteSearchPageDto> GetUnlinkedAsync(int limit, int offset, CancellationToken ct = default) =>
        _reads.GetUnlinkedAsync(limit, offset, ct);

    public Task<NoteSearchPageDto> BrowseAsync(
        NoteBrowseQuery query, int limit, int offset, CancellationToken ct = default) =>
        _reads.BrowseAsync(query, limit, offset, ct);

    public Task<IReadOnlyList<NoteBookFacetDto>> GetBookFacetsAsync(
        NoteBrowseQuery query, CancellationToken ct = default) =>
        _reads.GetBookFacetsAsync(query, ct);

    public Task<NoteSummaryDto> GetSummaryAsync(CancellationToken ct = default) =>
        _reads.GetSummaryAsync(ct);

    public Task<PassageDto?> GetPassageAsync(Guid noteId, CancellationToken ct = default) =>
        _reads.GetPassageAsync(noteId, ct);

    // ------------------------------------------------------------------
    // Search: delegated to NoteSearchService
    // ------------------------------------------------------------------

    public Task<IReadOnlyList<NoteSearchHitDto>> SearchAsync(
        string query,
        int limit,
        CancellationToken ct = default) =>
        _search.SearchAsync(query, limit, ct);

    public Task<IReadOnlyList<NoteSearchHitDto>> SearchAsync(
        string query,
        int limit,
        IReadOnlyList<Guid>? bookIds,
        CancellationToken ct = default) =>
        _search.SearchAsync(query, limit, bookIds, ct);

    // ------------------------------------------------------------------
    // Topic linking: delegated to NoteTopicLinkService
    // ------------------------------------------------------------------

    public Task<NoteCommandResult<TopicLinkResultDto>> LinkTopicsAsync(
        IReadOnlyList<string> topics, IReadOnlyList<Guid> noteIds, CancellationToken ct = default) =>
        _links.LinkTopicsAsync(topics, noteIds, ct);

    public Task<NoteCommandResult<TopicLinkResultDto>> UnlinkTopicsAsync(
        IReadOnlyList<string> topics, IReadOnlyList<Guid> noteIds, CancellationToken ct = default) =>
        _links.UnlinkTopicsAsync(topics, noteIds, ct);

    public Task<NoteCommandResult<NoteDto>> LinkToExistingTopicAsync(
        Guid noteId,
        Guid topicId,
        CancellationToken ct = default) =>
        _links.LinkToExistingTopicAsync(noteId, topicId, ct);

    public Task<NoteReviewDto?> GetForReviewAsync(Guid noteId, CancellationToken ct = default) =>
        _reads.GetForReviewAsync(noteId, ct);

    // ------------------------------------------------------------------
    // Post-processing lifecycle: delegated to NoteProcessingLifecycleService
    // ------------------------------------------------------------------

    public Task<NoteCommandResult<NoteDto>> ReprocessAsync(
        Guid noteId,
        string processingMode,
        CancellationToken ct = default) =>
        _lifecycle.ReprocessAsync(noteId, processingMode, ct);

    public Task<NoteCommandResult<NoteDto>> RewriteCaptureAsync(
        Guid noteId,
        string clientId,
        CancellationToken ct = default) =>
        _lifecycle.RewriteCaptureAsync(noteId, clientId, ct);

    public Task<NoteCommandResult<NoteRawTranscriptDto>> GetRawAsync(
        Guid noteId,
        CancellationToken ct = default) =>
        _lifecycle.GetRawAsync(noteId, ct);

    public Task<NoteCommandResult<NoteDto>> RestoreRawAsync(
        Guid noteId,
        CancellationToken ct = default) =>
        _lifecycle.RestoreRawAsync(noteId, ct);

    // ------------------------------------------------------------------
    // Mutations
    // ------------------------------------------------------------------

    /// <summary>
    /// Creates a note. With neither key this is today's non-idempotent create;
    /// with both it is exactly-once: a repeated pair replays the stored result
    /// without touching the notes table (issue #260 §3). The keys are
    /// all-or-nothing — supplying only one is an error rather than a silent
    /// downgrade to a non-replayable command.
    /// </summary>
    public Task<NoteCommandResult<NoteDto>> CreateAsync(
        Guid bookId,
        CreateNoteDto dto,
        string? clientId = null,
        string? idempotencyKey = null,
        CancellationToken ct = default) =>
        CreateAsync(bookId, dto, clientId, idempotencyKey, createdAt: null, ct);

    // `createdAt` is only for an import that keeps its source's date (issue
    // #1367); it is not part of the public note contract.
    private async Task<NoteCommandResult<NoteDto>> CreateAsync(
        Guid bookId,
        CreateNoteDto dto,
        string? clientId,
        string? idempotencyKey,
        DateTime? createdAt,
        CancellationToken ct)
    {
        var hasClient = !string.IsNullOrWhiteSpace(clientId);
        var hasKey = !string.IsNullOrWhiteSpace(idempotencyKey);

        if (!hasClient && !hasKey)
            return await CreateCoreAsync(bookId, dto, createdAt, ct);

        if (!hasClient || !hasKey)
            return NoteCommandResult<NoteDto>.Fail(
                NoteErrorCodes.InvalidIdempotency,
                "ClientId and IdempotencyKey must be supplied together.");

        if (clientId!.Length > 64 || idempotencyKey!.Length > 128)
            return NoteCommandResult<NoteDto>.Fail(
                NoteErrorCodes.InvalidIdempotency,
                "ClientId is limited to 64 characters and IdempotencyKey to 128.");

        return await CreateIdempotentAsync(bookId, dto, clientId, idempotencyKey, createdAt, ct);
    }

    public Task<NoteCommandResult<NoteDto>> CaptureAsync(
        CaptureNoteRequest request,
        CancellationToken ct = default)
    {
        // One implementation: CaptureAsync is the canonical in-process entry
        // point and funnels into the same keyed create the REST endpoint uses.
        var dto = new CreateNoteDto(
            Content: request.Content,
            CfiRange: request.CfiRange,
            SelectedText: request.SelectedText,
            RawContent: request.RawContent,
            CaptureSource: request.CaptureSource,
            ProcessingMode: request.ProcessingMode,
            SourceAnchorKind: request.SourceAnchorKind,
            SourceAnchorValue: request.SourceAnchorValue,
            AnchorVerified: request.AnchorVerified,
            HighlightColour: request.HighlightColour);

        return CreateAsync(request.BookId, dto, request.ClientId, request.IdempotencyKey, request.CreatedAt, ct);
    }

    private async Task<NoteCommandResult<NoteDto>> CreateCoreAsync(
        Guid bookId,
        CreateNoteDto dto,
        DateTime? createdAt,
        CancellationToken ct)
    {
        var book = await _books.GetByIdAsync(bookId, ct);
        if (book is null)
            return NoteCommandResult<NoteDto>.Fail(
                NoteErrorCodes.BookNotFound, $"Book {bookId} not found.");

        var kind = NoteKinds.Normalize(dto.Kind);
        if (kind is null)
            return InvalidPassage($"Kind must be one of: {NoteKinds.Note}, {NoteKinds.Passage}.");

        if (kind == NoteKinds.Passage)
        {
            var passageError = ValidatePassage(dto);
            if (passageError is not null)
                return InvalidPassage(passageError);
        }
        else if (string.IsNullOrWhiteSpace(dto.Content)
            && string.IsNullOrWhiteSpace(dto.SelectedText)
            && dto.Topics is not { Count: > 0 })
        {
            return NoteCommandResult<NoteDto>.Fail(
                NoteErrorCodes.EmptyNote, "Note must have content or selected text.");
        }

        if (!TryNormalizeHighlightColour(dto.HighlightColour, out var colour))
            return InvalidHighlightColour();

        if (!ValidTopics(dto.Topics))
            return InvalidTopics();

        var model = dto.ToModel(bookId);
        model.HighlightColour = colour;
        model.Kind = kind;
        if (createdAt is { } sourceDate)
            model.CreatedAt = sourceDate;
        if (kind == NoteKinds.Passage)
        {
            model.PassageTitle = dto.PassageTitle!.Trim();
            model.PassageEnd = dto.PassageEnd!.Trim();
            model.SourceAnchorValue = dto.SourceAnchorValue!.Trim();
            model.PassageHtml = string.IsNullOrWhiteSpace(dto.PassageHtml)
                ? null
                : PassageText.Sanitize(dto.PassageHtml);
            // Its two ends were taken from the open book, not inferred.
            model.AnchorVerified = true;
            // Never a highlight in the book: the passage viewer reads it.
            model.CfiRange = null;
        }
        else
        {
            model.PassageTitle = null;
            model.PassageEnd = null;
            model.PassageHtml = null;
        }

        // Between the raw transcript and the stored note (issue #262 §7): a
        // non-verbatim mode rewrites Content from the raw capture, which is kept
        // in RawContent. Verbatim is a no-op that makes no provider call.
        await _lifecycle.ApplyCaptureProcessingAsync(model, dto.ProcessingMode, ct);

        // Pill topics are written as text after any rewrite, so a capture mode
        // that rewrites the words cannot drop them.
        if (dto.Topics is { Count: > 0 })
            model.Content = TopicReferences.AppendAll(model.Content, dto.Topics);

        // A reader's place becomes the note's anchor (issue #1381).
        LocationAnchor.Apply(model);

        await _notes.AddAsync(model, ct);

        // EF Core tracks 'model', so the processor can add links to it before we save.
        await _processor.ProcessNoteAsync(model, ct: ct);

        await _notes.SaveChangesAsync(ct);

        return NoteCommandResult<NoteDto>.Ok(await _reads.SavedNoteAsync(model.Id, ct));
    }

    /// <summary>
    /// The exactly-once path: hold the in-process gate, replay a prior receipt
    /// if the pair was seen, otherwise run the command inside a transaction and
    /// commit the note together with its receipt. Only a successful capture is
    /// receipted — a rejected one stays retryable with the same key (deliberate
    /// divergence from the library, where failures are stored too).
    /// </summary>
    private async Task<NoteCommandResult<NoteDto>> CreateIdempotentAsync(
        Guid bookId,
        CreateNoteDto dto,
        string clientId,
        string idempotencyKey,
        DateTime? createdAt,
        CancellationToken ct)
    {
        await CommandGate.WaitAsync(ct);
        try
        {
            var prior = await _notes.GetReceiptAsync(clientId, idempotencyKey, ct);
            if (prior is not null)
                return Deserialize(prior.ResultJson);

            await using var transaction = await _db.Database.BeginTransactionAsync(ct);

            var result = await CreateCoreAsync(bookId, dto, createdAt, ct);
            if (!result.Success)
            {
                // No receipt for a rejection: the client can correct the reason
                // (empty note, unknown book) and retry with the SAME key.
                await transaction.RollbackAsync(ct);
                return result;
            }

            var json = Serialize(result);
            if (json.Length > MaxReceiptJsonLength)
                throw new InvalidOperationException(
                    $"Note command receipt would be {json.Length} characters, over the {MaxReceiptJsonLength} cap.");

            await _notes.AddReceiptAsync(new NoteCommandReceipt
            {
                ClientId = clientId,
                IdempotencyKey = idempotencyKey,
                Command = CaptureCommand,
                ResultJson = json,
                CreatedAtUtc = DateTime.UtcNow,
            }, ct);

            try
            {
                await _notes.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return result;
            }
            catch (DbUpdateException)
            {
                // Another process inserted the same pair first. Re-read its
                // receipt and return that stored result rather than throwing.
                await transaction.RollbackAsync(ct);
                _db.ChangeTracker.Clear();
                var raced = await _notes.GetReceiptAsync(clientId, idempotencyKey, ct);
                if (raced is not null)
                    return Deserialize(raced.ResultJson);
                throw;
            }
        }
        finally
        {
            CommandGate.Release();
        }
    }

    public async Task<NoteCommandResult<NoteDto>> UpdateAsync(Guid noteId, UpdateNoteDto dto, CancellationToken ct = default)
    {
        var existing = await _notes.GetByIdWithTopicsAsync(noteId, ct);
        if (existing is null)
            return NoteCommandResult<NoteDto>.Fail(NoteErrorCodes.NoteNotFound, "Note not found.");

        // Optimistic concurrency (issue #1404): a save made from a copy older
        // than the stored note is refused, not written over it. Compared to the
        // millisecond: PostgreSQL keeps microseconds, SQLite 100 ns, and the
        // client echoes back what it was given.
        if (dto.BaseUpdatedAt is { } baseUpdatedAt
            && !SameInstantToMillisecond(baseUpdatedAt, existing.UpdatedAt ?? existing.CreatedAt))
        {
            var current = await _reads.SavedNoteAsync(noteId, ct);
            return new NoteCommandResult<NoteDto>(
                false, NoteErrorCodes.EditConflict, "The note changed since this copy was loaded.", current);
        }

        if (!TryNormalizeHighlightColour(dto.HighlightColour, out var colour))
            return InvalidHighlightColour();

        if (!ValidTopics(dto.Topics))
            return InvalidTopics();

        if (existing.IsPassage() && dto.PassageTitle is not null)
        {
            var title = dto.PassageTitle.Trim();
            if (title.Length == 0 || title.Length > PassageTitleMaxLength)
                return InvalidPassage($"A passage title is 1 to {PassageTitleMaxLength} characters.");
        }

        existing.Apply(dto with { HighlightColour = colour });
        // A pill topic missing from the list is gone from the text, and the
        // processor drops a link whose reference the edit removed.
        if (dto.Topics is not null)
            existing.Content = TopicReferences.AppendAll(existing.Content, dto.Topics);

        // Notes saved before the anchor was derived get it on their next edit.
        LocationAnchor.Apply(existing);

        // Re-process topics (the processor clears old links then re-adds). An
        // edit that sent its topics sent all of them: an old link the text
        // never declared was shown as a pill, and one left out was removed.
        await _processor.ProcessNoteAsync(existing, textIsComplete: dto.Topics is not null, ct: ct);

        await _notes.SaveChangesAsync(ct);

        return NoteCommandResult<NoteDto>.Ok(await _reads.SavedNoteAsync(noteId, ct));
    }

    /// <summary>
    /// A pen is optional, but one that is sent must be a known name: storing an
    /// unknown value would be drawn as amber and read as a saved choice.
    /// </summary>
    private static bool TryNormalizeHighlightColour(string? value, out string? colour)
    {
        colour = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        colour = NoteHighlightColours.Normalize(value);
        return colour is not null;
    }

    private const int MaxTopicsPerNote = 50;

    /// <summary>Pill topics are optional; sent ones must each read back as themselves.</summary>
    private static bool ValidTopics(IReadOnlyList<string>? topics) =>
        topics is null
        || (topics.Count <= MaxTopicsPerNote && topics.All(TopicReferences.IsValidName));

    private static NoteCommandResult<NoteDto> InvalidTopics() =>
        NoteCommandResult<NoteDto>.Fail(
            NoteErrorCodes.InvalidTopicLink,
            $"A note takes at most {MaxTopicsPerNote} topics. {TopicReferences.InvalidNameMessage}");

    private static bool SameInstantToMillisecond(DateTime a, DateTime b) =>
        a.ToUniversalTime().Ticks / TimeSpan.TicksPerMillisecond
        == b.ToUniversalTime().Ticks / TimeSpan.TicksPerMillisecond;

    private const int PassageTitleMaxLength = 300;
    private const int PassageAnchorMaxLength = 1024;

    // The terms a passage's two ends can be given in, one per reader.
    private static readonly HashSet<string> PassageAnchorKinds =
        new(StringComparer.Ordinal) { "epub_cfi", "pdf_page", "audio_timestamp" };

    /// <summary>Why a passage cannot be saved, or null when it can.</summary>
    private static string? ValidatePassage(CreateNoteDto dto)
    {
        var title = dto.PassageTitle?.Trim();
        if (string.IsNullOrEmpty(title) || title.Length > PassageTitleMaxLength)
            return $"A passage title is 1 to {PassageTitleMaxLength} characters.";

        if (!PassageAnchorKinds.Contains(dto.SourceAnchorKind))
            return $"A passage's ends are given as one of: {string.Join(", ", PassageAnchorKinds)}.";

        var start = dto.SourceAnchorValue?.Trim();
        var end = dto.PassageEnd?.Trim();
        if (string.IsNullOrEmpty(start) || string.IsNullOrEmpty(end)
            || start.Length > PassageAnchorMaxLength || end.Length > PassageAnchorMaxLength)
            return "A passage needs both ends.";

        if (dto.SourceAnchorKind == "pdf_page")
        {
            if (!int.TryParse(start, out var first) || !int.TryParse(end, out var last)
                || first < 1 || last < first)
                return "A PDF passage is a range of pages, first to last.";
        }
        else if (dto.SourceAnchorKind == "audio_timestamp")
        {
            if (!double.TryParse(start, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var from)
                || !double.TryParse(end, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var to)
                || from < 0 || to <= from)
                return "An audio passage runs from an earlier time to a later one.";
        }
        else if (string.IsNullOrWhiteSpace(dto.SelectedText))
        {
            return "An EPUB passage carries its text.";
        }

        if (dto.SelectedText is { Length: > PassageText.MaxTextLength }
            || dto.PassageHtml is { Length: > PassageText.MaxHtmlLength })
            return "That is too long to save as one passage.";

        return null;
    }

    private static NoteCommandResult<NoteDto> InvalidPassage(string message) =>
        NoteCommandResult<NoteDto>.Fail(NoteErrorCodes.InvalidPassage, message);

    private static NoteCommandResult<NoteDto> InvalidHighlightColour() =>
        NoteCommandResult<NoteDto>.Fail(
            NoteErrorCodes.InvalidHighlightColour,
            $"Highlight colour must be one of: {string.Join(", ", NoteHighlightColours.All)}.");

    /// <summary>
    /// Saves where a reader found a note's text in the book. An imported
    /// highlight may be given a place, and so may a quote Ask Nostos saved
    /// (<see cref="FoundNoteLocation"/>). A place is set once: the first one
    /// is kept, and a note that already has one is answered as it stands.
    /// </summary>
    public async Task<NoteCommandResult<NoteDto>> SetFoundLocationAsync(
        Guid noteId,
        string? location,
        CancellationToken ct = default)
    {
        var found = location?.Trim() ?? string.Empty;
        if (!FoundNoteLocation.IsWellFormed(found))
        {
            return NoteCommandResult<NoteDto>.Fail(
                NoteErrorCodes.LocationNotAllowed, "An EPUB CFI range or the rectangles of a PDF highlight are required.");
        }

        var existing = await _notes.GetByIdWithTopicsAsync(noteId, ct);
        if (existing is null)
            return NoteCommandResult<NoteDto>.Fail(NoteErrorCodes.NoteNotFound, "Note not found.");

        if (!FoundNoteLocation.Allows(existing, found))
        {
            return NoteCommandResult<NoteDto>.Fail(
                NoteErrorCodes.LocationNotAllowed,
                "Only an imported highlight or a quote saved by Ask Nostos can be located.");
        }

        if (FoundNoteLocation.Fills(existing, found))
        {
            existing.CfiRange = found;
            LocationAnchor.Apply(existing);
            await _notes.SaveChangesAsync(ct);
        }

        return NoteCommandResult<NoteDto>.Ok(await _reads.SavedNoteAsync(noteId, ct));
    }

    public async Task<NoteCommandResult<bool>> DeleteAsync(Guid noteId, CancellationToken ct = default)
    {
        var existing = await _notes.GetByIdAsync(noteId, ct);
        if (existing is null)
            return NoteCommandResult<bool>.Fail(NoteErrorCodes.NoteNotFound, "Note not found.");

        // Remove topic links first: the join table's note FK is Restrict.
        await _notes.DeleteTopicLinksAsync(noteId, ct);

        await _notes.DeleteAsync(existing, ct);

        return NoteCommandResult<bool>.Ok(true);
    }

    // ------------------------------------------------------------------
    // Stored receipt helpers
    // ------------------------------------------------------------------

    /// <summary>Stored note command result. Successes only, so this always represents a created note.</summary>
    private static string Serialize(NoteCommandResult<NoteDto> result) =>
        JsonSerializer.Serialize(result, ReceiptJson);

    private static NoteCommandResult<NoteDto> Deserialize(string json) =>
        JsonSerializer.Deserialize<NoteCommandResult<NoteDto>>(json, ReceiptJson)
        ?? throw new InvalidOperationException("Stored note command receipt is invalid.");
}

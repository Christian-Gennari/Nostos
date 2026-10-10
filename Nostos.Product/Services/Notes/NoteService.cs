using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Mapping;
using Nostos.Backend.Search;
using Nostos.Backend.Services.Ai;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Services.Notes;

/// <summary>
/// Canonical note service (issue #260 §1, §5). Owns every note write —
/// including topic processing — so an endpoint (or a future assistant
/// capability) can never orchestrate the repositories itself. The scoped
/// <see cref="NostosDbContext"/> is shared with the repositories and the
/// <see cref="NoteProcessorService"/>, so a create/update stays one unit of
/// work exactly as it did when the handler held the repositories.
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
    private readonly ITopicRepository _topics;
    private readonly NoteProcessorService _processor;
    private readonly IThoughtProcessor _thoughts;
    private readonly NostosDbContext _db;
    private readonly ILogger<NoteService> _logger;

    public NoteService(
        INoteRepository notes,
        IBookRepository books,
        ITopicRepository topics,
        NoteProcessorService processor,
        IThoughtProcessor thoughts,
        NostosDbContext db,
        ILogger<NoteService> logger)
    {
        _notes = notes;
        _books = books;
        _topics = topics;
        _processor = processor;
        _thoughts = thoughts;
        _db = db;
        _logger = logger;
    }

    // ------------------------------------------------------------------
    // Read-only
    // ------------------------------------------------------------------

    public async Task<IReadOnlyList<NoteDto>> GetByBookAsync(Guid bookId, CancellationToken ct = default)
    {
        var notes = await _notes.GetByBookIdAsync(bookId);
        return notes.Select(n => n.ToDto()).ToList();
    }

    public async Task<NoteSearchHitDto?> GetAsync(Guid noteId, CancellationToken ct = default)
    {
        var note = await _db.Notes
            .AsNoTracking()
            .Include(n => n.Book)
            .Include(n => n.NoteTopics)
            .ThenInclude(nc => nc.Topic)
            .FirstOrDefaultAsync(n => n.Id == noteId, ct);

        return note is null ? null : ByText(note, null);
    }

    public async Task<NoteSearchPageDto> GetUnlinkedAsync(int limit, int offset, CancellationToken ct = default)
    {
        var take = Clamp(limit);
        var skip = Math.Max(offset, 0);
        var total = await _notes.CountWithoutTopicsAsync();
        var notes = await _notes.GetWithoutTopicsAsync(take, skip);
        return new NoteSearchPageDto(notes.Select(n => ByText(n, null)).ToList(), total, skip, take);
    }

    public async Task<NoteSearchPageDto> BrowseAsync(
        string? query, Guid? bookId, bool withoutTopics, bool oldestFirst,
        int limit, int offset, CancellationToken ct = default)
    {
        var take = Clamp(limit);
        var skip = Math.Max(offset, 0);
        var (items, total) = await _notes.BrowseAsync(
            query, bookId, withoutTopics, oldestFirst, take, skip);
        return new NoteSearchPageDto(items.Select(n => ByText(n, query?.Trim())).ToList(), total, skip, take);
    }

    public Task<IReadOnlyList<NoteSearchHitDto>> SearchAsync(
        string query,
        int limit,
        CancellationToken ct = default) =>
        SearchAsync(query, limit, bookIds: null, ct);

    public async Task<IReadOnlyList<NoteSearchHitDto>> SearchAsync(
        string query,
        int limit,
        IReadOnlyList<Guid>? bookIds,
        CancellationToken ct = default)
    {
        var take = Clamp(limit);
        var variants = LexicalQueryPlanner.Build(query);
        if (variants.Count == 0)
            return [];

        // Keep enough candidates per variant for deterministic fusion without
        // letting one broad token expand into an unbounded note scan.
        var candidateLimit = Math.Clamp(take * 3, 20, 60);
        var fused = new Dictionary<Guid, NoteFusionCandidate>();

        foreach (var variant in variants)
        {
            var hits = await _notes.SearchByTextAsync(variant.Text, candidateLimit, bookIds);
            for (var rank = 0; rank < hits.Count; rank++)
            {
                var note = hits[rank];
                var score = (variant.Weight * 100) + Math.Max(0, candidateLimit - rank);

                if (!fused.TryGetValue(note.Id, out var current))
                {
                    fused[note.Id] = new NoteFusionCandidate(
                        note,
                        score,
                        variant.Text,
                        MatchedVariants: 1);
                    continue;
                }

                fused[note.Id] = current with
                {
                    Score = current.Score + score,
                    MatchedVariants = current.MatchedVariants + 1,
                    // Prefer the more specific variant for the snippet.
                    SnippetTerm = variant.Weight > SpecificityWeight(current.SnippetTerm, variants)
                        ? variant.Text
                        : current.SnippetTerm,
                };
            }
        }

        return fused.Values
            .OrderByDescending(candidate => candidate.MatchedVariants)
            .ThenByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Note.CreatedAt)
            .ThenBy(candidate => candidate.Note.Id)
            .Take(take)
            .Select(candidate => ByText(candidate.Note, candidate.SnippetTerm))
            .ToList();
    }

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
    public async Task<NoteCommandResult<NoteDto>> CreateAsync(
        Guid bookId,
        CreateNoteDto dto,
        string? clientId = null,
        string? idempotencyKey = null,
        CancellationToken ct = default)
    {
        var hasClient = !string.IsNullOrWhiteSpace(clientId);
        var hasKey = !string.IsNullOrWhiteSpace(idempotencyKey);

        if (!hasClient && !hasKey)
            return await CreateCoreAsync(bookId, dto, ct);

        if (!hasClient || !hasKey)
            return NoteCommandResult<NoteDto>.Fail(
                NoteErrorCodes.InvalidIdempotency,
                "ClientId and IdempotencyKey must be supplied together.");

        if (clientId!.Length > 64 || idempotencyKey!.Length > 128)
            return NoteCommandResult<NoteDto>.Fail(
                NoteErrorCodes.InvalidIdempotency,
                "ClientId is limited to 64 characters and IdempotencyKey to 128.");

        return await CreateIdempotentAsync(bookId, dto, clientId, idempotencyKey, ct);
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
            AnchorVerified: request.AnchorVerified);

        return CreateAsync(request.BookId, dto, request.ClientId, request.IdempotencyKey, ct);
    }

    private async Task<NoteCommandResult<NoteDto>> CreateCoreAsync(
        Guid bookId,
        CreateNoteDto dto,
        CancellationToken ct)
    {
        var book = await _books.GetByIdAsync(bookId);
        if (book is null)
            return NoteCommandResult<NoteDto>.Fail(
                NoteErrorCodes.BookNotFound, $"Book {bookId} not found.");

        if (string.IsNullOrWhiteSpace(dto.Content) && string.IsNullOrWhiteSpace(dto.SelectedText))
            return NoteCommandResult<NoteDto>.Fail(
                NoteErrorCodes.EmptyNote, "Note must have content or selected text.");

        var model = dto.ToModel(bookId);

        // Between the raw transcript and the stored note (issue #262 §7): a
        // non-verbatim mode rewrites Content from the raw capture, which is kept
        // in RawContent. Verbatim is a no-op that makes no provider call.
        await ApplyCaptureProcessingAsync(model, dto.ProcessingMode, ct);

        await _notes.AddAsync(model);

        // EF Core tracks 'model', so the processor can add links to it before we save.
        await _processor.ProcessNoteAsync(model);

        await _notes.SaveChangesAsync();

        // Reload to get the Book Title for the response.
        var createdNote = await _notes.GetByIdWithBookAsync(model.Id);

        return NoteCommandResult<NoteDto>.Ok(createdNote!.ToDto());
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
        CancellationToken ct)
    {
        await CommandGate.WaitAsync(ct);
        try
        {
            var prior = await _notes.GetReceiptAsync(clientId, idempotencyKey);
            if (prior is not null)
                return Deserialize(prior.ResultJson);

            await using var transaction = await _db.Database.BeginTransactionAsync(ct);

            var result = await CreateCoreAsync(bookId, dto, ct);
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
            });

            try
            {
                await _notes.SaveChangesAsync();
                await transaction.CommitAsync(ct);
                return result;
            }
            catch (DbUpdateException)
            {
                // Another process inserted the same pair first. Re-read its
                // receipt and return that stored result rather than throwing.
                await transaction.RollbackAsync(ct);
                _db.ChangeTracker.Clear();
                var raced = await _notes.GetReceiptAsync(clientId, idempotencyKey);
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
        var existing = await _notes.GetByIdWithTopicsAsync(noteId);
        if (existing is null)
            return NoteCommandResult<NoteDto>.Fail(NoteErrorCodes.NoteNotFound, "Note not found.");

        existing.Apply(dto);

        // Re-process topics (the processor clears old links then re-adds).
        await _processor.ProcessNoteAsync(existing);

        await _notes.SaveChangesAsync();

        return NoteCommandResult<NoteDto>.Ok(existing.ToDto());
    }

    public async Task<NoteCommandResult<bool>> DeleteAsync(Guid noteId, CancellationToken ct = default)
    {
        var existing = await _notes.GetByIdAsync(noteId);
        if (existing is null)
            return NoteCommandResult<bool>.Fail(NoteErrorCodes.NoteNotFound, "Note not found.");

        // Remove topic links first: the join table's note FK is Restrict.
        await _notes.DeleteTopicLinksAsync(noteId);

        await _notes.DeleteAsync(existing);

        return NoteCommandResult<bool>.Ok(true);
    }

    // ------------------------------------------------------------------
    // Capabilities for the note-review flow (issue #261)
    // ------------------------------------------------------------------

    public async Task<NoteCommandResult<NoteDto>> LinkToExistingTopicAsync(
        Guid noteId,
        Guid topicId,
        CancellationToken ct = default)
    {
        var note = await _notes.GetByIdWithTopicsAsync(noteId);
        if (note is null)
            return NoteCommandResult<NoteDto>.Fail(NoteErrorCodes.NoteNotFound, "Note not found.");

        // Never creates a topic: the link target must already exist.
        var topic = await _db.Topics.AsNoTracking().FirstOrDefaultAsync(c => c.Id == topicId, ct);
        if (topic is null)
            return NoteCommandResult<NoteDto>.Fail(NoteErrorCodes.TopicNotFound, "Topic not found.");

        // Idempotent. The join is keyed (NoteId, TopicId); inserting a second
        // row would be a duplicate key. The check runs against the database, not
        // the tracked navigation, so a repeated call sees the committed link.
        var existingNames = await _db.NoteTopics
            .AsNoTracking()
            .Where(nc => nc.NoteId == noteId)
            .Select(nc => new { nc.TopicId, Name = nc.Topic!.Topic })
            .ToListAsync(ct);
        var alreadyLinked = existingNames.Any(link => link.TopicId == topicId);

        // The note body is the canonical source of membership on every later
        // edit. A join-only Act link would disappear when UpdateAsync reprocesses
        // that body. Normalize older join-only links on this note as well, so
        // accepting a new proposal does not make those disappear on the next edit.
        var references = Regex.Matches(note.Content, @"\[\[(.*?)\]\]").Cast<Match>()
            .Select(match => match.Groups[1].Value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var contentChanged = false;
        foreach (var name in existingNames.Select(link => link.Name).Append(topic.Topic))
        {
            if (!references.Add(name)) continue;
            var body = note.Content.TrimEnd();
            note.Content = body.Length == 0 ? $"[[{name}]]" : $"{body}\n\n[[{name}]]";
            contentChanged = true;
        }

        if (!alreadyLinked)
        {
            // Keep other links and save the text and join in one unit of work.
            _topics.AddNoteLink(new NoteTopicModel { NoteId = noteId, TopicId = topicId });
        }
        if (!alreadyLinked || contentChanged)
            await _notes.SaveChangesAsync();

        return NoteCommandResult<NoteDto>.Ok(note.ToDto());
    }

    public async Task<NoteReviewDto?> GetForReviewAsync(Guid noteId, CancellationToken ct = default)
    {
        var note = await _db.Notes
            .AsNoTracking()
            .Include(n => n.Book)
            .Include(n => n.NoteTopics)
            .ThenInclude(nc => nc.Topic)
            .FirstOrDefaultAsync(n => n.Id == noteId, ct);

        if (note is null)
            return null;

        return new NoteReviewDto(
            note.Id,
            note.BookId,
            note.Book?.Title,
            note.Content,
            note.SelectedText,
            note.NoteTopics
                .Where(nc => nc.Topic is not null)
                .Select(nc => nc.Topic!.Topic)
                .OrderBy(name => name)
                .ToList(),
            note.NoteTopics.Select(link => link.TopicId).ToList());
    }

    // ------------------------------------------------------------------
    // Post-processing (issue #262 §7, §8)
    // ------------------------------------------------------------------

    public async Task<NoteCommandResult<NoteDto>> ReprocessAsync(
        Guid noteId,
        string processingMode,
        CancellationToken ct = default)
    {
        if (!ThoughtProcessingModes.IsSupported(processingMode))
        {
            return NoteCommandResult<NoteDto>.Fail(
                NoteErrorCodes.InvalidProcessingMode,
                $"Unknown processing mode '{processingMode}'.");
        }

        var note = await _notes.GetByIdWithTopicsAsync(noteId);
        if (note is null)
            return NoteCommandResult<NoteDto>.Fail(NoteErrorCodes.NoteNotFound, "Note not found.");

        var mode = ThoughtProcessingModes.Normalize(processingMode);

        // The original transcript is the ONLY source of a re-run: a second pass
        // (say, clarify after light polish) must never transform transformed
        // prose. A note that predates the mode feature has no RawContent yet, so
        // its current text IS the original and becomes the raw transcript now.
        var raw = note.RawContent;
        if (string.IsNullOrWhiteSpace(raw) && !string.IsNullOrWhiteSpace(note.Content))
        {
            raw = note.Content;
            note.RawContent = raw;
        }

        if (mode == ThoughtProcessingModes.Verbatim || string.IsNullOrWhiteSpace(raw))
        {
            // Verbatim restore (or a quote-only capture with no thought text):
            // the raw words are the stored words, and no provider is called.
            note.Content = raw ?? note.Content;
            note.ProcessingMode = ThoughtProcessingModes.Verbatim;
        }
        else
        {
            var result = await _thoughts.ProcessAsync(raw, mode, ct);
            note.Content = result.Text;
            note.ProcessingMode = result.Mode;
        }

        // Content may have changed, so the topic links are re-derived from it.
        await _processor.ProcessNoteAsync(note);
        await _notes.SaveChangesAsync();

        return NoteCommandResult<NoteDto>.Ok(note.ToDto());
    }

    public async Task<NoteCommandResult<NoteRawTranscriptDto>> GetRawAsync(
        Guid noteId,
        CancellationToken ct = default)
    {
        var note = await _notes.GetByIdAsync(noteId);
        if (note is null)
            return NoteCommandResult<NoteRawTranscriptDto>.Fail(NoteErrorCodes.NoteNotFound, "Note not found.");

        return NoteCommandResult<NoteRawTranscriptDto>.Ok(
            new NoteRawTranscriptDto(note.Id, note.RawContent, note.Content, note.ProcessingMode));
    }

    public async Task<NoteCommandResult<NoteDto>> RestoreRawAsync(
        Guid noteId,
        CancellationToken ct = default)
    {
        var note = await _notes.GetByIdWithTopicsAsync(noteId);
        if (note is null)
            return NoteCommandResult<NoteDto>.Fail(NoteErrorCodes.NoteNotFound, "Note not found.");

        if (string.IsNullOrWhiteSpace(note.RawContent))
        {
            return NoteCommandResult<NoteDto>.Fail(
                NoteErrorCodes.NoRawTranscript,
                "This note has no raw transcript to restore.");
        }

        // Restore the original words and record the honest mode. RawContent is
        // left exactly as it was — restoring is not a way to erase the capture.
        note.Content = note.RawContent;
        note.ProcessingMode = ThoughtProcessingModes.Verbatim;

        await _processor.ProcessNoteAsync(note);
        await _notes.SaveChangesAsync();

        return NoteCommandResult<NoteDto>.Ok(note.ToDto());
    }

    /// <summary>
    /// Applies the capture's mode to the model before it is stored. The raw
    /// transcript is preserved in <see cref="NoteModel.RawContent"/> and is never
    /// overwritten; a provider failure keeps the raw words rather than losing the
    /// thought.
    /// </summary>
    private async Task ApplyCaptureProcessingAsync(NoteModel model, string mode, CancellationToken ct)
    {
        var normalized = ThoughtProcessingModes.Normalize(mode);
        if (normalized == ThoughtProcessingModes.Verbatim)
            return;

        // A quote-only capture has no user thought to process. The quote lives in
        // SelectedText and is never handed to the processor, so it cannot be
        // rewritten by any mode. Nothing was processed, so the honest mode is
        // verbatim rather than the requested one.
        if (string.IsNullOrWhiteSpace(model.Content))
        {
            model.ProcessingMode = ThoughtProcessingModes.Verbatim;
            return;
        }

        // The raw transcript is the source of truth. A request that asks for a
        // processed mode but supplies no raw transcript means the incoming
        // Content IS the raw transcript — so it is kept before Content changes.
        model.RawContent ??= model.Content;

        try
        {
            var result = await _thoughts.ProcessAsync(model.RawContent, normalized, ct);
            model.Content = result.Text;
            model.ProcessingMode = result.Mode;
        }
        catch (LlmException ex)
        {
            // A thought is never lost to a provider failure: the raw transcript
            // becomes the content and the stored mode says so honestly.
            _logger.LogWarning(
                ex,
                "Post-processing capture {NoteId} in mode {Mode} failed; keeping the raw transcript.",
                model.Id,
                normalized);
            model.Content = model.RawContent;
            model.ProcessingMode = ThoughtProcessingModes.Verbatim;
        }
    }

    // ------------------------------------------------------------------
    // Helpers moved verbatim from NotesEndpoints
    // ------------------------------------------------------------------

    /// <summary>A small page keeps the index responsive; the cap is a guard on the caller.</summary>
    private static int Clamp(int limit) => Math.Clamp(limit, 1, 200);

    /// <summary>Stored note command result. Successes only, so this always represents a created note.</summary>
    private static string Serialize(NoteCommandResult<NoteDto> result) =>
        JsonSerializer.Serialize(result, ReceiptJson);

    private static NoteCommandResult<NoteDto> Deserialize(string json) =>
        JsonSerializer.Deserialize<NoteCommandResult<NoteDto>>(json, ReceiptJson)
        ?? throw new InvalidOperationException("Stored note command receipt is invalid.");

    private static int SpecificityWeight(
        string term,
        IReadOnlyList<LexicalQueryVariant> variants) =>
        variants.FirstOrDefault(
            variant => string.Equals(variant.Text, term, StringComparison.OrdinalIgnoreCase))?.Weight ?? 0;

    private sealed record NoteFusionCandidate(
        NoteModel Note,
        int Score,
        string SnippetTerm,
        int MatchedVariants);

    private static NoteSearchHitDto ByText(NoteModel n, string? term) => new(
        n.Id,
        n.BookId,
        n.Book?.Title,
        n.Content,
        n.SelectedText,
        Snippet(n.Content, n.SelectedText, term),
        n.NoteTopics
            .Where(nc => nc.Topic != null)
            .Select(nc => nc.Topic!.Topic)
            .OrderBy(name => name)
            .ToList(),
        n.CreatedAt,
        n.CfiRange,
        n.SourceAnchorKind,
        n.SourceAnchorValue,
        n.AnchorVerified,
        n.CaptureSource,
        n.ProcessingMode,
        !string.IsNullOrWhiteSpace(n.RawContent));

    /// <summary>
    /// The fragment around the match, so a row can show WHY it matched. Built here
    /// rather than in the client so the whole note body never has to travel.
    /// </summary>
    private static string? Snippet(string content, string? quote, string? term)
    {
        // Prefer the quotation: it is the passage from the book either way.
        var text = string.IsNullOrWhiteSpace(quote) ? content : quote!;
        if (string.IsNullOrWhiteSpace(text)) return null;

        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (flat.Length <= 160) return flat;

        // Centre on the match when there is one, so the row shows WHY it matched
        // rather than the opening words of every hit.
        var at = term is null ? -1 : flat.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return flat[..157] + "…";

        var start = Math.Max(0, at - 60);
        var end = Math.Min(flat.Length, start + 157);
        start = Math.Max(0, Math.Min(start, end - 40));
        return (start > 0 ? "…" : string.Empty) + flat[start..end] + (end < flat.Length ? "…" : string.Empty);
    }
}

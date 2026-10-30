using System.Text.Json;
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

        return note is null ? null : ByText(note, null, await EditionCountsAsync([note], ct));
    }

    public async Task<IReadOnlyList<NoteSearchHitDto>> GetManyAsync(
        IReadOnlyCollection<Guid> noteIds, CancellationToken ct = default)
    {
        var ids = noteIds.Distinct().Take(MaxNotesByIds).ToList();
        if (ids.Count == 0)
            return [];

        var notes = await _db.Notes
            .AsNoTracking()
            .Include(n => n.Book)
            .Include(n => n.NoteTopics)
            .ThenInclude(nc => nc.Topic)
            .Where(n => ids.Contains(n.Id))
            .ToListAsync(ct);

        var order = ids.Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index);
        var editions = await EditionCountsAsync(notes, ct);
        return notes.OrderBy(n => order[n.Id]).Select(n => ByText(n, null, editions)).ToList();
    }

    /// <summary>The most notes one <see cref="GetManyAsync"/> call answers for.</summary>
    public const int MaxNotesByIds = 200;

    public async Task<NoteSearchPageDto> GetUnlinkedAsync(int limit, int offset, CancellationToken ct = default)
    {
        var take = Clamp(limit);
        var skip = Math.Max(offset, 0);
        var total = await _notes.CountWithoutTopicsAsync();
        var notes = await _notes.GetWithoutTopicsAsync(take, skip);
        var editions = await EditionCountsAsync(notes, ct);
        return new NoteSearchPageDto(notes.Select(n => ByText(n, null, editions)).ToList(), total, skip, take);
    }

    public async Task<NoteSearchPageDto> BrowseAsync(
        NoteBrowseQuery query, int limit, int offset, CancellationToken ct = default)
    {
        var take = Clamp(limit);
        var skip = Math.Max(offset, 0);
        var (items, total) = await _notes.BrowseAsync(Normalized(query), take, skip);
        var editions = await EditionCountsAsync(items, ct);
        return new NoteSearchPageDto(items.Select(n => ByText(n, query.Text?.Trim(), editions)).ToList(), total, skip, take);
    }

    public async Task<IReadOnlyList<NoteBookFacetDto>> GetBookFacetsAsync(
        NoteBrowseQuery query, CancellationToken ct = default)
    {
        return await _notes.GetBookFacetsAsync(Normalized(query));
    }

    public async Task<NoteSummaryDto> GetSummaryAsync(CancellationToken ct = default)
    {
        return await _notes.GetSummaryAsync();
    }

    // An unknown kind matches nothing rather than everything.
    private static NoteBrowseQuery Normalized(NoteBrowseQuery query) =>
        string.IsNullOrWhiteSpace(query.Kind)
            ? query with { Kind = null }
            : query with { Kind = NoteKinds.Normalize(query.Kind) ?? "unknown" };

    public Task<NoteCommandResult<TopicLinkResultDto>> LinkTopicsAsync(
        IReadOnlyList<string> topics, IReadOnlyList<Guid> noteIds, CancellationToken ct = default) =>
        ChangeTopicReferencesAsync(topics, noteIds, TopicReferences.Append, unlink: false, ct);

    public Task<NoteCommandResult<TopicLinkResultDto>> UnlinkTopicsAsync(
        IReadOnlyList<string> topics, IReadOnlyList<Guid> noteIds, CancellationToken ct = default) =>
        ChangeTopicReferencesAsync(topics, noteIds, TopicReferences.Remove, unlink: true, ct);

    private const int MaxTopicsPerLink = 20;
    private const int MaxNotesPerLink = 200;

    /// <summary>
    /// Rewrites each listed note's body with <paramref name="change"/> for every
    /// named topic, re-derives its links, and saves all of it at once: either
    /// every note changes or none does.
    /// </summary>
    private async Task<NoteCommandResult<TopicLinkResultDto>> ChangeTopicReferencesAsync(
        IReadOnlyList<string>? topics,
        IReadOnlyList<Guid>? noteIds,
        Func<string, string, string> change,
        bool unlink,
        CancellationToken ct)
    {
        var names = (topics ?? [])
            .Select(name => name?.Trim() ?? string.Empty)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var ids = (noteIds ?? []).Distinct().ToList();

        if (names.Count == 0 || ids.Count == 0)
            return InvalidTopicLink("Name at least one topic and one note.");
        if (names.Count > MaxTopicsPerLink || ids.Count > MaxNotesPerLink)
            return InvalidTopicLink($"At most {MaxTopicsPerLink} topics and {MaxNotesPerLink} notes at once.");
        if (!names.All(TopicReferences.IsValidName))
            return InvalidTopicLink(TopicReferences.InvalidNameMessage);

        var notes = await _db.Notes
            .Include(n => n.Book)
            .Include(n => n.NoteTopics).ThenInclude(link => link.Topic)
            .Where(n => ids.Contains(n.Id))
            .ToListAsync(ct);
        if (notes.Count != ids.Count)
            return NoteCommandResult<TopicLinkResultDto>.Fail(NoteErrorCodes.NoteNotFound, "Note not found.");

        var byId = notes.ToDictionary(n => n.Id);
        var ordered = ids.Select(id => byId[id]).ToList();
        var changed = new List<Guid>();
        foreach (var note in ordered)
        {
            var content = names.Aggregate(note.Content, change);
            // A link-only topic (no reference in the text) changes the links,
            // not the text, so the links are compared too.
            var linked = note.NoteTopics
                .Select(link => link.Topic?.Topic)
                .OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var linksChange = unlink ? names.Any(linked.Contains) : names.Any(name => !linked.Contains(name));
            if (content == note.Content && !linksChange) continue;

            note.Content = content;
            await _processor.ProcessNoteAsync(note, unlink ? names : null);
            changed.Add(note.Id);
        }

        if (changed.Count > 0)
            await _db.SaveChangesAsync(ct);

        var editions = await EditionCountsAsync(ordered, ct);
        return NoteCommandResult<TopicLinkResultDto>.Ok(
            new TopicLinkResultDto(changed, ordered.Select(n => ByText(n, null, editions)).ToList()));
    }

    private static NoteCommandResult<TopicLinkResultDto> InvalidTopicLink(string message) =>
        NoteCommandResult<TopicLinkResultDto>.Fail(NoteErrorCodes.InvalidTopicLink, message);

    public Task<IReadOnlyList<NoteSearchHitDto>> SearchAsync(
        string query,
        int limit,
        CancellationToken ct = default) =>
        SearchAsync(query, limit, bookIds: null, ct);

    // Above the planner's phrase weight (4): every word present beats the exact
    // phrase being absent, and the phrase is rare in a short "@" query anyway.
    private const int AllTermsWeight = 5;

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

        // Several words: a note holding every one of them, across its text and
        // its book's title and author, outranks one that shares only a single
        // word (issue #1038). "schopenhauer will" names an author and a subject.
        var tokens = variants
            .Where(variant => variant.Kind == LexicalQueryVariantKind.Token)
            .Select(variant => variant.Text)
            .ToList();
        var searches = variants
            .Select(variant => (variant.Text, variant.Weight, Terms: (IReadOnlyList<string>)[variant.Text]))
            .ToList();
        if (tokens.Count >= 2)
            searches.Insert(0, (string.Join(' ', tokens), AllTermsWeight, tokens));

        var matchedAllTerms = new HashSet<Guid>();
        foreach (var variant in searches)
        {
            var hits = await _notes.SearchByAllTermsAsync(variant.Terms, candidateLimit, bookIds);
            if (variant.Weight == AllTermsWeight)
                matchedAllTerms.UnionWith(hits.Select(note => note.Id));
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

        // With several words: a note holding every word comes first, and among
        // those a word found in the note itself says more than one found only in
        // its book's title. "schopenhauer will" is about the will, not about
        // every note from "The World as Will and Representation".
        // Single-word queries keep their existing order exactly.
        var multiWord = tokens.Count >= 2;
        var ranked = fused.Values
            .OrderByDescending(candidate => multiWord && matchedAllTerms.Contains(candidate.Note.Id))
            .ThenByDescending(candidate => multiWord ? OwnTextMatches(candidate.Note, tokens) : 0)
            .ThenByDescending(candidate => candidate.MatchedVariants)
            .ThenByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Note.CreatedAt)
            .ThenBy(candidate => candidate.Note.Id)
            .Take(take)
            .ToList();
        var editions = await EditionCountsAsync(ranked.Select(candidate => candidate.Note), ct);
        return ranked
            .Select(candidate => ByText(candidate.Note, SnippetTermFor(candidate, tokens), editions))
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
            AnchorVerified: request.AnchorVerified,
            HighlightColour: request.HighlightColour);

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

        var kind = NoteKinds.Normalize(dto.Kind);
        if (kind is null)
            return InvalidPassage($"Kind must be one of: {NoteKinds.Note}, {NoteKinds.Passage}.");

        if (kind == NoteKinds.Passage)
        {
            var passageError = ValidatePassage(dto);
            if (passageError is not null)
                return InvalidPassage(passageError);
        }
        else if (string.IsNullOrWhiteSpace(dto.Content) && string.IsNullOrWhiteSpace(dto.SelectedText))
        {
            return NoteCommandResult<NoteDto>.Fail(
                NoteErrorCodes.EmptyNote, "Note must have content or selected text.");
        }

        if (!TryNormalizeHighlightColour(dto.HighlightColour, out var colour))
            return InvalidHighlightColour();

        var model = dto.ToModel(bookId);
        model.HighlightColour = colour;
        model.Kind = kind;
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

        if (!TryNormalizeHighlightColour(dto.HighlightColour, out var colour))
            return InvalidHighlightColour();

        if (existing.IsPassage() && dto.PassageTitle is not null)
        {
            var title = dto.PassageTitle.Trim();
            if (title.Length == 0 || title.Length > PassageTitleMaxLength)
                return InvalidPassage($"A passage title is 1 to {PassageTitleMaxLength} characters.");
        }

        existing.Apply(dto with { HighlightColour = colour });

        // Re-process topics (the processor clears old links then re-adds).
        await _processor.ProcessNoteAsync(existing);

        await _notes.SaveChangesAsync();

        return NoteCommandResult<NoteDto>.Ok(existing.ToDto());
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

    public async Task<PassageDto?> GetPassageAsync(Guid noteId, CancellationToken ct = default)
    {
        var note = await _db.Notes
            .AsNoTracking()
            .Include(n => n.Book)
            .FirstOrDefaultAsync(n => n.Id == noteId, ct);

        return note is null || !note.IsPassage() ? null : note.ToPassageDto();
    }

    private static NoteCommandResult<NoteDto> InvalidHighlightColour() =>
        NoteCommandResult<NoteDto>.Fail(
            NoteErrorCodes.InvalidHighlightColour,
            $"Highlight colour must be one of: {string.Join(", ", NoteHighlightColours.All)}.");

    public async Task<NoteCommandResult<NoteDto>> SetImportedLocationAsync(
        Guid noteId,
        string? cfiRange,
        CancellationToken ct = default)
    {
        var location = cfiRange?.Trim() ?? string.Empty;
        if (!location.StartsWith("epubcfi(", StringComparison.Ordinal)
            || !location.EndsWith(')')
            || location.Length > 1024)
        {
            return NoteCommandResult<NoteDto>.Fail(
                NoteErrorCodes.LocationNotAllowed, "An EPUB CFI range is required.");
        }

        var existing = await _notes.GetByIdWithTopicsAsync(noteId);
        if (existing is null)
            return NoteCommandResult<NoteDto>.Fail(NoteErrorCodes.NoteNotFound, "Note not found.");

        if (existing.CaptureSource != "import")
        {
            return NoteCommandResult<NoteDto>.Fail(
                NoteErrorCodes.LocationNotAllowed, "Only an imported highlight can be located.");
        }

        if (string.IsNullOrWhiteSpace(existing.CfiRange))
        {
            existing.CfiRange = location;
            await _notes.SaveChangesAsync();
        }

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
        var before = note.Content;
        note.Content = TopicReferences.AppendAll(note.Content, existingNames.Select(link => link.Name).Append(topic.Topic));
        var contentChanged = note.Content != before;

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

    public async Task<NoteCommandResult<NoteDto>> RewriteCaptureAsync(
        Guid noteId,
        string clientId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientId) || !await _notes.HasReceiptForNoteAsync(clientId, noteId))
        {
            return NoteCommandResult<NoteDto>.Fail(
                NoteErrorCodes.NoteNotFound,
                "No note captured in this conversation has that id.");
        }

        return await ReprocessAsync(noteId, ThoughtProcessingModes.Clarify, ct);
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

    /// <summary>
    /// The all-words search records the whole query as its term, which rarely
    /// occurs verbatim when its words matched different fields. Centre the
    /// snippet on a word the note's own text really contains instead.
    /// </summary>
    private static string? SnippetTermFor(NoteFusionCandidate candidate, IReadOnlyList<string> tokens)
    {
        bool InText(string term) =>
            candidate.Note.Content.Contains(term, StringComparison.OrdinalIgnoreCase)
            || (candidate.Note.SelectedText?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false);

        if (candidate.SnippetTerm is { } term && InText(term)) return term;
        return tokens.FirstOrDefault(InText) ?? candidate.SnippetTerm;
    }

    private static int OwnTextMatches(NoteModel note, IReadOnlyList<string> tokens) =>
        tokens.Count(token =>
            note.Content.Contains(token, StringComparison.OrdinalIgnoreCase)
            || (note.SelectedText?.Contains(token, StringComparison.OrdinalIgnoreCase) ?? false)
            || (note.PassageTitle?.Contains(token, StringComparison.OrdinalIgnoreCase) ?? false));

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

    /// <summary>
    /// How many editions each listed note's book has, in one query for the whole
    /// page (never one per note), keyed by work.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, int>> EditionCountsAsync(
        IEnumerable<NoteModel> notes, CancellationToken ct)
    {
        var workIds = notes.Where(n => n.Book is not null).Select(n => n.Book!.WorkId).Distinct().ToList();
        if (workIds.Count == 0) return new Dictionary<Guid, int>();
        return await _db.Books
            .AsNoTracking()
            .Where(b => workIds.Contains(b.WorkId))
            .GroupBy(b => b.WorkId)
            .Select(g => new { WorkId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(row => row.WorkId, row => row.Count, ct);
    }

    private static NoteSearchHitDto ByText(
        NoteModel n, string? term, IReadOnlyDictionary<Guid, int>? editions = null) => new(
        n.Id,
        n.BookId,
        n.Book?.Title,
        n.Content,
        // A passage travels as its opening; the snippet still finds the match
        // anywhere in it.
        n.IsPassage() ? PassageText.Opening(n.SelectedText) : n.SelectedText,
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
        !string.IsNullOrWhiteSpace(n.RawContent),
        n.Kind,
        n.PassageTitle,
        n.PassageEnd,
        n.IsPassage() ? PassageText.WordCount(n.SelectedText) : null,
        n.Book?.Author,
        n.HighlightColour,
        FormatOf(n.Book),
        n.Book is not null && editions is not null && editions.TryGetValue(n.Book.WorkId, out var count) ? count : 1,
        n.Book?.WorkId);

    /// <summary>The format glyph's name for an edition: audiobook, pdf, ebook or physical.</summary>
    private static string? FormatOf(BookModel? book) => book switch
    {
        null => null,
        AudioBookModel => "audiobook",
        PhysicalBookModel => "physical",
        _ when book.FileDetails.FileName?.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) == true => "pdf",
        _ => "ebook",
    };

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

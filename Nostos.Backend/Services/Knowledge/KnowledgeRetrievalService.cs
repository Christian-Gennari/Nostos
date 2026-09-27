using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Search;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Services.Notes;
using Nostos.Product.BookText;
using Nostos.Shared.Dtos;
using Nostos.Shared.Enums;

namespace Nostos.Backend.Services.Knowledge;

public static class KnowledgeEvidenceKinds
{
    public const string Note = "note";
    public const string Concept = "concept";
    public const string BookText = "book_text";
}

/// <summary>
/// Stable canonical pointer to evidence. Handles are references only: assistant
/// mutations still require their normal canonical ids, trust class and approval
/// path. A handle never grants write authority.
/// </summary>
public sealed record KnowledgeEvidenceHandle(
    string Kind,
    Guid? NoteId = null,
    Guid? ConceptId = null,
    Guid? BookId = null,
    string? SourceSha256 = null,
    string? ExtractorVersion = null,
    int? Ordinal = null);

public sealed record KnowledgeSearchRequest(
    string Query,
    IReadOnlyList<Guid>? BookIds = null,
    Guid? CollectionId = null,
    int? MaxPerSource = null);

public sealed record KnowledgeNoteEvidence(
    KnowledgeEvidenceHandle Handle,
    Guid NoteId,
    Guid BookId,
    string? BookTitle,
    string Snippet,
    IReadOnlyList<string> ConceptNames,
    DateTime CreatedAt,
    string? CfiRange,
    string SourceAnchorKind,
    string? SourceAnchorValue,
    bool AnchorVerified);

public sealed record KnowledgeConceptSupportingNote(
    KnowledgeEvidenceHandle Handle,
    Guid NoteId,
    Guid BookId,
    string? BookTitle,
    string Snippet);

public sealed record KnowledgeConceptEvidence(
    KnowledgeEvidenceHandle Handle,
    Guid ConceptId,
    string Name,
    int UsageCount,
    int NoteMatchCount,
    string? MatchSnippet,
    IReadOnlyList<KnowledgeConceptSupportingNote> SupportingNotes);

public sealed record KnowledgeBookEvidence(
    KnowledgeEvidenceHandle Handle,
    Guid BookId,
    string BookTitle,
    string? BookAuthor,
    string SourceSha256,
    string ExtractorVersion,
    BookTextSourceFormat Format,
    int Ordinal,
    string Text,
    IReadOnlyList<string> HeadingPath,
    IReadOnlyList<BookTextSourceSegment> SourceSegments);

public sealed record KnowledgeSearchResponse(
    IReadOnlyList<string> QueryVariants,
    IReadOnlyList<KnowledgeNoteEvidence> Notes,
    IReadOnlyList<KnowledgeConceptEvidence> Concepts,
    IReadOnlyList<KnowledgeBookEvidence> BookPassages,
    IReadOnlyList<BookTextIngestionState> BookTextStates,
    bool EvidenceAvailable);

public sealed record KnowledgeBookNoteCount(Guid BookId, string BookTitle, int NoteCount);

public sealed record KnowledgeOverview(
    int TotalNotes,
    int UnlinkedNotes,
    int TotalConcepts,
    int TotalConceptReferences,
    IReadOnlyList<ConceptDto> TopConcepts,
    IReadOnlyList<KnowledgeBookNoteCount> TopBooksByNoteCount);

public sealed record KnowledgeReadResponse(
    KnowledgeEvidenceHandle Handle,
    KnowledgeNoteRead? Note = null,
    KnowledgeConceptRead? Concept = null,
    KnowledgeBookEvidence? BookPassage = null);

public sealed record KnowledgeNoteRead(
    Guid NoteId,
    Guid BookId,
    string? BookTitle,
    string Content,
    string? SelectedText,
    IReadOnlyList<string> ConceptNames,
    DateTime CreatedAt,
    string? CfiRange,
    string SourceAnchorKind,
    string? SourceAnchorValue,
    bool AnchorVerified);

public sealed record KnowledgeConceptRead(
    Guid ConceptId,
    string Name,
    int UsageCount,
    IReadOnlyList<KnowledgeConceptSupportingNote> Notes);

/// <summary>
/// Optional provider-neutral extension point for future semantic/hybrid
/// retrieval. Implementations return canonical handles only; Nostos resolves
/// those handles back through canonical data before exposing evidence.
/// </summary>
public interface IKnowledgeRetrievalContributor
{
    string Name { get; }

    Task<IReadOnlyList<KnowledgeEvidenceHandle>> SearchAsync(
        KnowledgeSearchRequest request,
        CancellationToken ct = default);
}

public interface IKnowledgeRetrievalService
{
    Task<KnowledgeSearchResponse> SearchAsync(
        KnowledgeSearchRequest request,
        CancellationToken ct = default);

    Task<KnowledgeOverview> OverviewAsync(CancellationToken ct = default);

    Task<KnowledgeReadResponse?> ReadAsync(
        KnowledgeEvidenceHandle handle,
        CancellationToken ct = default);
}

public sealed class NoOpKnowledgeRetrievalService : IKnowledgeRetrievalService
{
    public static NoOpKnowledgeRetrievalService Instance { get; } = new();

    private NoOpKnowledgeRetrievalService() { }

    public Task<KnowledgeSearchResponse> SearchAsync(
        KnowledgeSearchRequest request,
        CancellationToken ct = default) =>
        Task.FromResult(new KnowledgeSearchResponse([], [], [], [], [], false));

    public Task<KnowledgeOverview> OverviewAsync(CancellationToken ct = default) =>
        Task.FromResult(new KnowledgeOverview(0, 0, 0, 0, [], []));

    public Task<KnowledgeReadResponse?> ReadAsync(
        KnowledgeEvidenceHandle handle,
        CancellationToken ct = default) =>
        Task.FromResult<KnowledgeReadResponse?>(null);
}

public sealed class KnowledgeRetrievalService(
    INoteService notes,
    INoteRepository noteRepository,
    IConceptRepository concepts,
    ILibraryService library,
    IBookTextSearchService bookText,
    IBookTextIndex bookTextIndex,
    IEnumerable<IKnowledgeRetrievalContributor> contributors) : IKnowledgeRetrievalService
{
    private const int DefaultMaxPerSource = 6;
    private const int MaximumMaxPerSource = 8;
    private const int SupportingNotesPerConcept = 3;
    private const int OverviewTopCount = 12;

    public async Task<KnowledgeSearchResponse> SearchAsync(
        KnowledgeSearchRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Query))
            return new KnowledgeSearchResponse([], [], [], [], [], false);

        var variants = LexicalQueryPlanner.Build(request.Query);
        if (variants.Count == 0)
            return new KnowledgeSearchResponse([], [], [], [], [], false);

        var max = Math.Clamp(
            request.MaxPerSource ?? DefaultMaxPerSource,
            1,
            MaximumMaxPerSource);

        var scopedBookIds = await ResolveScopedBookIdsAsync(request, ct);

        var noteHits = await notes.SearchAsync(
            request.Query,
            max,
            scopedBookIds,
            ct);

        var conceptHits = await concepts.SearchByNoteTextAsync(
            request.Query,
            scopedBookIds,
            max);

        var bookResponse = await bookText.SearchAsync(
            new BookTextSearchRequest(
                request.Query,
                request.BookIds,
                request.CollectionId,
                max),
            ct);

        var noteEvidence = noteHits
            .Select(ToNoteEvidence)
            .ToList();

        var conceptEvidence = new List<KnowledgeConceptEvidence>(conceptHits.Count);
        foreach (var concept in conceptHits)
        {
            conceptEvidence.Add(await ToConceptEvidenceAsync(
                concept,
                variants,
                scopedBookIds,
                ct));
        }

        var bookEvidence = bookResponse.Passages
            .Select(ToBookEvidence)
            .ToList();

        // Optional contributors can add canonical candidates (for example a
        // future embedding index). Their output is never trusted as evidence on
        // its own: resolve every handle through current Nostos data first.
        foreach (var contributor in contributors)
        {
            var handles = await contributor.SearchAsync(request, ct);
            foreach (var handle in handles.Take(max * 3))
            {
                var resolved = await ReadAsync(handle, ct);
                if (resolved is null)
                    continue;

                if (resolved.Note is { } note
                    && IsBookAllowed(note.BookId, scopedBookIds)
                    && noteEvidence.Count < max
                    && noteEvidence.All(item => item.NoteId != note.NoteId))
                {
                    noteEvidence.Add(ToNoteEvidence(note));
                }
                else if (resolved.Concept is { } concept
                    && conceptEvidence.Count < max
                    && conceptEvidence.All(item => item.ConceptId != concept.ConceptId))
                {
                    var scopedConcept = ToConceptEvidence(concept, scopedBookIds);
                    if (scopedConcept is not null)
                        conceptEvidence.Add(scopedConcept);
                }
                else if (resolved.BookPassage is { } passage
                    && IsBookAllowed(passage.BookId, scopedBookIds)
                    && bookEvidence.Count < max
                    && bookEvidence.All(item => item.Handle != passage.Handle))
                {
                    bookEvidence.Add(passage);
                }
            }
        }

        return new KnowledgeSearchResponse(
            variants.Select(variant => variant.Text).ToList(),
            noteEvidence.Take(max).ToList(),
            conceptEvidence.Take(max).ToList(),
            bookEvidence.Take(max).ToList(),
            bookResponse.States,
            noteEvidence.Count > 0 || conceptEvidence.Count > 0 || bookEvidence.Count > 0);
    }

    public async Task<KnowledgeOverview> OverviewAsync(CancellationToken ct = default)
    {
        var totalNotes = await noteRepository.CountAsync();
        var unlinkedNotes = await noteRepository.CountWithoutConceptsAsync();
        var conceptStats = await concepts.GetStatsAsync();
        var topConcepts = (await concepts.GetAllWithUsageCountAsync())
            .Take(OverviewTopCount)
            .ToList();
        var topBooks = await noteRepository.GetBookCountsAsync(OverviewTopCount);

        return new KnowledgeOverview(
            totalNotes,
            unlinkedNotes,
            conceptStats.TotalConcepts,
            conceptStats.TotalReferences,
            topConcepts,
            topBooks
                .Select(item => new KnowledgeBookNoteCount(
                    item.BookId,
                    item.BookTitle,
                    item.NoteCount))
                .ToList());
    }

    public async Task<KnowledgeReadResponse?> ReadAsync(
        KnowledgeEvidenceHandle handle,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (string.Equals(handle.Kind, KnowledgeEvidenceKinds.Note, StringComparison.Ordinal))
        {
            if (handle.NoteId is not { } noteId)
                return null;

            var note = await noteRepository.GetByIdWithConceptsAsync(noteId);
            return note is null
                ? null
                : new KnowledgeReadResponse(handle, Note: ToNoteRead(note));
        }

        if (string.Equals(handle.Kind, KnowledgeEvidenceKinds.Concept, StringComparison.Ordinal))
        {
            if (handle.ConceptId is not { } conceptId)
                return null;

            var concept = await concepts.GetByIdWithNotesAsync(conceptId);
            if (concept is null)
                return null;

            return new KnowledgeReadResponse(
                handle,
                Concept: ToConceptRead(concept));
        }

        if (string.Equals(handle.Kind, KnowledgeEvidenceKinds.BookText, StringComparison.Ordinal))
        {
            var passage = await ReadBookPassageAsync(handle, ct);
            return passage is null
                ? null
                : new KnowledgeReadResponse(handle, BookPassage: passage);
        }

        return null;
    }

    private async Task<IReadOnlyList<Guid>?> ResolveScopedBookIdsAsync(
        KnowledgeSearchRequest request,
        CancellationToken ct)
    {
        if (request.BookIds is { Count: > 0 })
        {
            var valid = new List<Guid>();
            foreach (var bookId in request.BookIds.Distinct().Take(32))
            {
                var result = await library.GetBookAsync(bookId, ct);
                if (result.Data is BookDto)
                    valid.Add(bookId);
            }
            return valid;
        }

        if (request.CollectionId is not { } collectionId)
            return null;

        const int pageSize = 100;
        var page = 1;
        var ids = new List<Guid>();
        while (ids.Count < 500)
        {
            var result = await library.ListBooksAsync(
                BookFilter.All,
                BookSort.Title,
                search: null,
                page,
                pageSize,
                collectionId,
                groupByWork: false,
                format: null,
                ct);

            if (result.Data is not PaginatedResponse<BookDto> batch)
                break;

            var items = batch.Items.ToList();
            ids.AddRange(items.Select(item => item.Id));
            if (items.Count == 0 || ids.Count >= batch.TotalCount)
                break;
            page++;
        }

        return ids;
    }

    private async Task<KnowledgeConceptEvidence> ToConceptEvidenceAsync(
        ConceptDto concept,
        IReadOnlyList<LexicalQueryVariant> variants,
        IReadOnlyList<Guid>? scopedBookIds,
        CancellationToken ct)
    {
        var model = await concepts.GetByIdWithNotesAsync(concept.Id);
        IReadOnlyList<KnowledgeConceptSupportingNote> supporting = model is null
            ? []
            : SupportingNotes(
                model,
                variants.Select(variant => variant.Text).ToList(),
                scopedBookIds,
                SupportingNotesPerConcept);

        return new KnowledgeConceptEvidence(
            ConceptHandle(concept.Id),
            concept.Id,
            concept.Name,
            concept.UsageCount,
            concept.NoteMatchCount,
            concept.NoteMatchSnippet,
            supporting);
    }

    private static IReadOnlyList<KnowledgeConceptSupportingNote> SupportingNotes(
        ConceptModel concept,
        IReadOnlyList<string> variants,
        IReadOnlyList<Guid>? scopedBookIds,
        int limit)
    {
        return concept.NoteConcepts
            .Select(link => link.Note)
            .Where(note => note is not null)
            .Cast<NoteModel>()
            .Where(note => scopedBookIds is null || scopedBookIds.Contains(note.BookId))
            .Select(note => new
            {
                Note = note,
                MatchCount = variants.Count(variant => Matches(note, variant)),
            })
            .Where(candidate => candidate.MatchCount > 0)
            .OrderByDescending(candidate => candidate.MatchCount)
            .ThenByDescending(candidate => candidate.Note.CreatedAt)
            .ThenBy(candidate => candidate.Note.Id)
            .Take(limit)
            .Select(candidate => new KnowledgeConceptSupportingNote(
                NoteHandle(candidate.Note.Id),
                candidate.Note.Id,
                candidate.Note.BookId,
                candidate.Note.Book?.Title,
                Snippet(candidate.Note, variants)))
            .ToList();
    }

    private static bool Matches(NoteModel note, string variant) =>
        note.Content.Contains(variant, StringComparison.OrdinalIgnoreCase)
        || (!string.IsNullOrWhiteSpace(note.SelectedText)
            && note.SelectedText.Contains(variant, StringComparison.OrdinalIgnoreCase))
        || (!string.IsNullOrWhiteSpace(note.Book?.Title)
            && note.Book.Title.Contains(variant, StringComparison.OrdinalIgnoreCase));

    private async Task<KnowledgeBookEvidence?> ReadBookPassageAsync(
        KnowledgeEvidenceHandle handle,
        CancellationToken ct)
    {
        if (handle.BookId is not { } bookId
            || string.IsNullOrWhiteSpace(handle.SourceSha256)
            || string.IsNullOrWhiteSpace(handle.ExtractorVersion)
            || handle.Ordinal is not { } ordinal)
            return null;

        var state = await bookTextIndex.GetStateAsync(bookId, ct);
        if (state is null
            || state.Status != BookTextIngestionStatus.Ready
            || !string.Equals(state.SourceSha256, handle.SourceSha256, StringComparison.Ordinal)
            || !string.Equals(state.ExtractorVersion, handle.ExtractorVersion, StringComparison.Ordinal))
            return null;

        // Existing neighbor API is revision-scoped and includes the requested
        // ordinal. Selecting the exact ordinal gives a stable reread without
        // broadening the public book-text index contract.
        var nearby = await bookTextIndex.GetNeighborsAsync(
            bookId,
            handle.SourceSha256,
            handle.ExtractorVersion,
            ordinal,
            radius: 1,
            ct);
        var chunk = nearby.SingleOrDefault(item => item.Ordinal == ordinal);
        if (chunk is null)
            return null;

        var bookResult = await library.GetBookAsync(bookId, ct);
        if (bookResult.Data is not BookDto book)
            return null;

        return new KnowledgeBookEvidence(
            BookHandle(chunk),
            chunk.BookId,
            book.Title,
            book.Author,
            chunk.SourceSha256,
            chunk.ExtractorVersion,
            chunk.Format,
            chunk.Ordinal,
            chunk.Text,
            chunk.HeadingPath,
            chunk.SourceSegments);
    }

    private static KnowledgeNoteEvidence ToNoteEvidence(NoteSearchHitDto note) =>
        new(
            NoteHandle(note.Id),
            note.Id,
            note.BookId,
            note.BookTitle,
            note.Snippet ?? Clip(note.SelectedText ?? note.Content, 240),
            note.ConceptNames,
            note.CreatedAt,
            note.CfiRange,
            note.SourceAnchorKind,
            note.SourceAnchorValue,
            note.AnchorVerified);

    private static KnowledgeNoteEvidence ToNoteEvidence(KnowledgeNoteRead note) =>
        new(
            NoteHandle(note.NoteId),
            note.NoteId,
            note.BookId,
            note.BookTitle,
            Clip(note.SelectedText ?? note.Content, 240),
            note.ConceptNames,
            note.CreatedAt,
            note.CfiRange,
            note.SourceAnchorKind,
            note.SourceAnchorValue,
            note.AnchorVerified);

    private static KnowledgeConceptEvidence? ToConceptEvidence(
        KnowledgeConceptRead concept,
        IReadOnlyList<Guid>? scopedBookIds)
    {
        var notes = concept.Notes
            .Where(note => IsBookAllowed(note.BookId, scopedBookIds))
            .ToList();

        if (scopedBookIds is not null && notes.Count == 0)
            return null;

        return new KnowledgeConceptEvidence(
            ConceptHandle(concept.ConceptId),
            concept.ConceptId,
            concept.Name,
            concept.UsageCount,
            notes.Count,
            notes.FirstOrDefault()?.Snippet,
            notes);
    }

    private static bool IsBookAllowed(
        Guid bookId,
        IReadOnlyList<Guid>? scopedBookIds) =>
        scopedBookIds is null || scopedBookIds.Contains(bookId);

    private static KnowledgeBookEvidence ToBookEvidence(BookTextSearchPassage passage) =>
        new(
            new KnowledgeEvidenceHandle(
                KnowledgeEvidenceKinds.BookText,
                BookId: passage.BookId,
                SourceSha256: passage.SourceSha256,
                ExtractorVersion: passage.ExtractorVersion,
                Ordinal: passage.Ordinal),
            passage.BookId,
            passage.BookTitle,
            passage.BookAuthor,
            passage.SourceSha256,
            passage.ExtractorVersion,
            passage.Format,
            passage.Ordinal,
            passage.Text,
            passage.HeadingPath,
            passage.SourceSegments);

    private static KnowledgeNoteRead ToNoteRead(NoteModel note) =>
        new(
            note.Id,
            note.BookId,
            note.Book?.Title,
            note.Content,
            note.SelectedText,
            note.NoteConcepts
                .Where(link => link.Concept is not null)
                .Select(link => link.Concept!.Concept)
                .OrderBy(name => name)
                .ToList(),
            note.CreatedAt,
            note.CfiRange,
            note.SourceAnchorKind,
            note.SourceAnchorValue,
            note.AnchorVerified);

    private static KnowledgeConceptRead ToConceptRead(ConceptModel concept)
    {
        var notes = concept.NoteConcepts
            .Select(link => link.Note)
            .Where(note => note is not null)
            .Cast<NoteModel>()
            .OrderByDescending(note => note.CreatedAt)
            .ThenBy(note => note.Id)
            .Take(12)
            .Select(note => new KnowledgeConceptSupportingNote(
                NoteHandle(note.Id),
                note.Id,
                note.BookId,
                note.Book?.Title,
                Clip(note.SelectedText ?? note.Content, 240)))
            .ToList();

        return new KnowledgeConceptRead(
            concept.Id,
            concept.Concept,
            concept.NoteConcepts.Count,
            notes);
    }

    private static KnowledgeEvidenceHandle NoteHandle(Guid noteId) =>
        new(KnowledgeEvidenceKinds.Note, NoteId: noteId);

    private static KnowledgeEvidenceHandle ConceptHandle(Guid conceptId) =>
        new(KnowledgeEvidenceKinds.Concept, ConceptId: conceptId);

    private static KnowledgeEvidenceHandle BookHandle(BookTextIndexedChunk chunk) =>
        new(
            KnowledgeEvidenceKinds.BookText,
            BookId: chunk.BookId,
            SourceSha256: chunk.SourceSha256,
            ExtractorVersion: chunk.ExtractorVersion,
            Ordinal: chunk.Ordinal);

    private static string Snippet(NoteModel note, IReadOnlyList<string> variants)
    {
        var source = string.IsNullOrWhiteSpace(note.SelectedText)
            ? note.Content
            : note.SelectedText!;

        var flat = string.Join(
            ' ',
            source.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        var match = variants
            .Select(variant => (Variant: variant, Index: flat.IndexOf(variant, StringComparison.OrdinalIgnoreCase)))
            .Where(item => item.Index >= 0)
            .OrderBy(item => item.Index)
            .FirstOrDefault();

        if (match.Variant is null)
            return Clip(flat, 240);

        var start = Math.Max(0, match.Index - 80);
        var length = Math.Min(240, flat.Length - start);
        var snippet = flat.Substring(start, length);
        return (start > 0 ? "…" : string.Empty)
            + snippet
            + (start + length < flat.Length ? "…" : string.Empty);
    }

    private static string Clip(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;
        var flat = string.Join(
            ' ',
            text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return flat.Length <= max ? flat : flat[..(max - 1)] + "…";
    }
}

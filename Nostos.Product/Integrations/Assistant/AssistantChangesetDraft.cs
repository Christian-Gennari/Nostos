using System.Text.Json;
using Nostos.Backend.Services.Library;
using Nostos.Shared.Dtos;
using Nostos.Shared.Enums;

namespace Nostos.Backend.Integrations.Assistant;

/// <summary>One staged operation, with the sentence the user reviews it by.</summary>
public sealed record AssistantStagedStep(LibraryChangeOp Op, string Summary, IReadOnlyList<int> DependsOn);

/// <summary>
/// The library changes one turn is building. Stage capabilities add to it; it
/// never writes. Every addition is normalised and previewed together with what
/// is already staged, through the canonical library code, so the model learns at
/// once that a collection does not exist or a step clashes, and can correct
/// itself in the same turn. What it holds when the turn ends is what the change
/// policy judges.
/// </summary>
public sealed class AssistantChangesetDraft(ILibraryChangesetService changesets, ILibraryService library)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly LibraryChangesetCountsDto EmptyCounts = new(0, 0, 0, 0, 0, 0, 0, 0);

    private readonly List<AssistantStagedStep> _steps = [];
    private readonly Dictionary<string, (LibraryCollectionRef Ref, int? Step)> _namedCollections = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<Guid, CollectionDto>? _collections;
    private readonly Dictionary<Guid, BookDto?> _books = [];
    private readonly Dictionary<Guid, IReadOnlyList<Guid>> _directMembers = [];

    public IReadOnlyList<AssistantStagedStep> Steps => _steps;

    /// <summary>The diff of everything staged so far, from the last successful preview.</summary>
    public LibraryChangesetDiffDto? Diff { get; private set; }

    public string? DiffHash { get; private set; }

    public bool IsEmpty => _steps.Count == 0;

    /// <summary>
    /// Reads a collection argument: an existing collection's id, or the name
    /// (or <c>ref</c>) given to a collection created or reused earlier in this turn.
    /// </summary>
    public bool TryResolveCollection(string? value, out LibraryCollectionRef reference, out int? createdBy)
    {
        reference = default;
        createdBy = null;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        if (Guid.TryParse(trimmed, out var id))
        {
            reference = LibraryCollectionRef.Existing(id);
            return true;
        }

        if (_namedCollections.TryGetValue(trimmed, out var entry))
        {
            reference = entry.Ref;
            createdBy = entry.Step;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Stages one operation. On success the result tells the model what the
    /// whole changeset would now do; on failure nothing is staged and the
    /// canonical failure is returned as-is.
    /// </summary>
    public Task<AssistantToolResult> StageAsync(
        LibraryChangeOp op,
        IReadOnlyList<int> dependsOn,
        CancellationToken ct) =>
        StageAsync([(op, dependsOn)], ct);

    /// <summary>
    /// Stages several operations as consecutive steps, normalised and previewed
    /// once. Either all of them are staged or none is.
    /// </summary>
    public async Task<AssistantToolResult> StageAsync(
        IReadOnlyList<(LibraryChangeOp Op, IReadOnlyList<int> DependsOn)> additions,
        CancellationToken ct)
    {
        var rawCandidate = _steps.Select(s => s.Op).Concat(additions.Select(a => a.Op)).ToList();
        var initialCollections = await CollectionsAsync(ct);
        var normalized = await AssistantChangesetNormalizer.NormalizeAsync(
            rawCandidate,
            initialCollections,
            TryGetBookAsync,
            DirectMembersAsync,
            ct);

        if (normalized.Ops.Count == 0)
        {
            _steps.Clear();
            RebuildNamedCollections(normalized.RefAliases);
            Diff = null;
            DiffHash = null;

            return AssistantToolResult.Ok(JsonSerializer.SerializeToElement(new
            {
                status = "no_change",
                steps = Array.Empty<string>(),
                note = "No library change was needed: the library already matches what you asked for.",
                changesetSoFar = EmptyCounts,
            }, Json));
        }

        var preview = await changesets.PreviewAsync(normalized.Ops, ct);
        if (!preview.Success)
        {
            var failedStep = preview.FailedOperation is { } index && index >= _steps.Count
                ? $" (in the change you just asked for, part {index - _steps.Count + 1})"
                : string.Empty;
            var message = (preview.ErrorMessage ?? "That change could not be made.") + failedStep;
            return AssistantToolResult.Fail(
                preview.ErrorCode ?? "changeset_invalid",
                message,
                JsonSerializer.SerializeToElement(new { status = "not_staged", reason = message }, Json));
        }

        var createStepByRef = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < normalized.Ops.Count; i++)
        {
            if (normalized.Ops[i] is CreateCollectionOp create)
                createStepByRef[create.Ref] = i;
        }

        var rebuilt = new List<AssistantStagedStep>(normalized.Ops.Count);
        foreach (var op in normalized.Ops)
        {
            var dependsOn = ReferencesUsedBy(op)
                .Where(r => r.Ref is not null)
                .Select(r => createStepByRef.TryGetValue(r.Ref!, out var stepIndex) ? stepIndex : -1)
                .Where(stepIndex => stepIndex >= 0)
                .Distinct()
                .Order()
                .ToList();
            var summary = await SummarizeAsync(op, normalized.Ops, ct);
            rebuilt.Add(new AssistantStagedStep(op, summary, dependsOn));
        }

        _steps.Clear();
        _steps.AddRange(rebuilt);
        RebuildNamedCollections(normalized.RefAliases);

        Diff = preview.Diff;
        DiffHash = preview.DiffHash;

        if (preview.Diff!.IsEmpty)
        {
            return AssistantToolResult.Ok(JsonSerializer.SerializeToElement(new
            {
                status = "no_change",
                steps = Array.Empty<string>(),
                note = "No library change was needed: the library already matches what you asked for.",
                changesetSoFar = preview.Diff.Counts,
            }, Json));
        }

        return AssistantToolResult.Ok(JsonSerializer.SerializeToElement(new
        {
            status = "staged",
            steps = _steps.Select(s => s.Summary).ToList(),
            note = "Staged, not applied. Stage every change the request needs; Nostos applies or presents them together when you finish.",
            changesetSoFar = preview.Diff.Counts,
        }, Json));
    }

    private void RebuildNamedCollections(IReadOnlyDictionary<string, LibraryCollectionRef> refAliases)
    {
        var preservedExisting = _namedCollections
            .Where(kv => kv.Value.Ref.Id is not null)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        _namedCollections.Clear();
        foreach (var (name, entry) in preservedExisting)
            _namedCollections[name] = entry;

        var createStepByRef = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < _steps.Count; i++)
        {
            if (_steps[i].Op is CreateCollectionOp create)
            {
                createStepByRef[create.Ref] = i;
                _namedCollections[create.Ref] = (LibraryCollectionRef.Created(create.Ref), i);
            }
        }

        foreach (var (alias, mapped) in refAliases)
        {
            if (mapped.Id is not null)
                _namedCollections[alias] = (mapped, null);
            else if (mapped.Ref is { } targetRef && createStepByRef.TryGetValue(targetRef, out var stepIndex))
                _namedCollections[alias] = (mapped, stepIndex);
        }
    }

    private static IEnumerable<LibraryCollectionRef> ReferencesUsedBy(LibraryChangeOp op) => op switch
    {
        CreateCollectionOp { Parent: { } parent } => [parent],
        RenameCollectionOp rename => [rename.Collection],
        MoveCollectionOp { NewParent: { } parent } move => [move.Collection, parent],
        MoveCollectionOp move => [move.Collection],
        DeleteCollectionOp delete => [delete.Collection],
        SetBookCollectionsOp set => set.Collections,
        AddBooksToCollectionOp add => [add.Collection],
        RemoveBooksFromCollectionOp remove => [remove.Collection],
        _ => [],
    };

    // ------------------------------------------------------------------
    // Step sentences
    // ------------------------------------------------------------------

    private async Task<string> SummarizeAsync(
        LibraryChangeOp op,
        IReadOnlyList<LibraryChangeOp> ops,
        CancellationToken ct)
    {
        switch (op)
        {
            case CreateCollectionOp create:
                return create.Parent is { } parent
                    ? $"Create collection “{create.Name}” inside “{await NameAsync(parent, ops, ct)}”"
                    : $"Create collection “{create.Name}”";
            case RenameCollectionOp rename:
                return $"Rename “{await NameAsync(rename.Collection, ops, ct)}” to “{rename.Name}”";
            case MoveCollectionOp move:
                return move.NewParent is { } newParent
                    ? $"Move “{await NameAsync(move.Collection, ops, ct)}” into “{await NameAsync(newParent, ops, ct)}”"
                    : $"Move “{await NameAsync(move.Collection, ops, ct)}” to the top level";
            case DeleteCollectionOp delete:
            {
                var name = await NameAsync(delete.Collection, ops, ct);
                var descendants = delete.Collection.Id is { } id ? await CountDescendantsAsync(id, ct) : 0;
                return descendants switch
                {
                    0 => $"Delete collection “{name}”",
                    1 => $"Delete collection “{name}” and its subcollection",
                    _ => $"Delete collection “{name}” and its {descendants} subcollections",
                };
            }
            case AddBooksToCollectionOp add:
                return $"Add {await BooksPhraseAsync(add.BookIds, ct)} to “{await NameAsync(add.Collection, ops, ct)}”";
            case RemoveBooksFromCollectionOp remove:
                return $"Remove {await BooksPhraseAsync(remove.BookIds, ct)} from “{await NameAsync(remove.Collection, ops, ct)}”";
            case SetBookCollectionsOp set:
            {
                var title = await BookTitleAsync(set.BookId, ct);
                if (set.Collections.Count == 0)
                    return $"Take “{title}” out of every collection";
                var names = new List<string>();
                foreach (var collection in set.Collections)
                    names.Add($"“{await NameAsync(collection, ops, ct)}”");
                return $"Put “{title}” in {string.Join(", ", names)} only";
            }
            case UpdateBookOp update:
            {
                var fields = new List<string>();
                if (update.Title is not null) fields.Add("title");
                if (update.Author is not null) fields.Add("author");
                if (update.Description is not null) fields.Add("description");
                if (update.Rating is not null) fields.Add("rating");
                if (update.EditionRating is not null) fields.Add("edition rating");
                if (update.IsFavorite is not null) fields.Add(update.IsFavorite.Value ? "mark favourite" : "unmark favourite");
                if (update.PersonalReview is not null) fields.Add("review");
                if (update.IsFinished is not null) fields.Add(update.IsFinished.Value ? "mark finished" : "mark not finished");
                return $"Edit “{await BookTitleAsync(update.BookId, ct)}”: {string.Join(", ", fields)}";
            }
            default:
                return "Change the library";
        }
    }

    private async Task<string> NameAsync(
        LibraryCollectionRef reference,
        IReadOnlyList<LibraryChangeOp> ops,
        CancellationToken ct)
    {
        if (reference.Ref is { } created)
        {
            return ops.OfType<CreateCollectionOp>()
                .FirstOrDefault(c => c.Ref == created)?.Name ?? created;
        }

        var collections = await CollectionsAsync(ct);
        return collections.TryGetValue(reference.Id!.Value, out var dto) ? dto.Name : "a collection";
    }

    private async Task<int> CountDescendantsAsync(Guid id, CancellationToken ct)
    {
        var collections = await CollectionsAsync(ct);
        var count = 0;
        var frontier = new List<Guid> { id };
        while (frontier.Count > 0)
        {
            frontier = collections.Values.Where(c => c.ParentId is { } p && frontier.Contains(p)).Select(c => c.Id).ToList();
            count += frontier.Count;
        }
        return count;
    }

    private async Task<string> BooksPhraseAsync(IReadOnlyList<Guid> bookIds, CancellationToken ct)
    {
        var distinct = bookIds.Distinct().ToList();
        return distinct.Count == 1
            ? $"“{await BookTitleAsync(distinct[0], ct)}”"
            : $"{distinct.Count} books";
    }

    private async Task<string> BookTitleAsync(Guid bookId, CancellationToken ct)
    {
        var book = await TryGetBookAsync(bookId, ct);
        return book?.Title ?? "a book";
    }

    private async Task<BookDto?> TryGetBookAsync(Guid bookId, CancellationToken ct)
    {
        if (_books.TryGetValue(bookId, out var cached))
            return cached;

        var result = await library.GetBookAsync(bookId, ct);
        var book = result.Data as BookDto;
        _books[bookId] = book;
        return book;
    }

    private async Task<IReadOnlyList<Guid>> DirectMembersAsync(Guid collectionId, CancellationToken ct)
    {
        if (_directMembers.TryGetValue(collectionId, out var cached))
            return cached;

        var members = new List<Guid>();
        const int pageSize = 100;
        var page = 1;
        while (true)
        {
            var result = await library.ListBooksAsync(
                BookFilter.All, BookSort.Title, search: null, page, pageSize,
                collectionId, groupByWork: false, format: null, ct);
            if (result.Data is not PaginatedResponse<BookDto> batch)
                break;

            var pageCount = 0;
            foreach (var book in batch.Items)
            {
                pageCount++;
                _books.TryAdd(book.Id, book);
                if (book.CollectionIds is not null && book.CollectionIds.Contains(collectionId))
                    members.Add(book.Id);
            }

            if (pageCount == 0 || page * pageSize >= batch.TotalCount)
                break;
            page++;
        }

        IReadOnlyList<Guid> distinct = members.Distinct().ToList();
        _directMembers[collectionId] = distinct;
        return distinct;
    }

    private async Task<Dictionary<Guid, CollectionDto>> CollectionsAsync(CancellationToken ct)
    {
        if (_collections is not null)
            return _collections;

        var result = await library.ListCollectionsAsync(ct);
        _collections = result.Data is IEnumerable<CollectionDto> items
            ? items.ToDictionary(c => c.Id)
            : [];
        return _collections;
    }
}

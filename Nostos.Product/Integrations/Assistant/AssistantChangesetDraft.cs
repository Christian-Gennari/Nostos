using System.Text.Json;
using Nostos.Backend.Services.Library;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Integrations.Assistant;

/// <summary>One staged operation, with the sentence the user reviews it by.</summary>
public sealed record AssistantStagedStep(LibraryChangeOp Op, string Summary, IReadOnlyList<int> DependsOn);

/// <summary>
/// The library changes one turn is building. Stage capabilities add to it; it
/// never writes. Every addition is previewed together with what is already
/// staged, through the canonical library code, so the model learns at once
/// that a collection does not exist or a step clashes, and can correct itself
/// in the same turn. What it holds when the turn ends is what the change
/// policy judges.
/// </summary>
public sealed class AssistantChangesetDraft(ILibraryChangesetService changesets, ILibraryService library)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly List<AssistantStagedStep> _steps = [];
    private readonly Dictionary<string, (string Ref, int Step)> _createdByName = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<Guid, CollectionDto>? _collections;

    public IReadOnlyList<AssistantStagedStep> Steps => _steps;

    /// <summary>The diff of everything staged so far, from the last successful preview.</summary>
    public LibraryChangesetDiffDto? Diff { get; private set; }

    public string? DiffHash { get; private set; }

    public bool IsEmpty => _steps.Count == 0;

    /// <summary>
    /// Reads a collection argument: an existing collection's id, or the name
    /// (or <c>ref</c>) given to a collection created earlier in this turn.
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

        if (_createdByName.TryGetValue(trimmed, out var created))
        {
            reference = LibraryCollectionRef.Created(created.Ref);
            createdBy = created.Step;
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
    /// Stages several operations as consecutive steps, previewed once. Either
    /// all of them are staged or none is.
    /// </summary>
    public async Task<AssistantToolResult> StageAsync(
        IReadOnlyList<(LibraryChangeOp Op, IReadOnlyList<int> DependsOn)> additions,
        CancellationToken ct)
    {
        var candidate = _steps.Select(s => s.Op).Concat(additions.Select(a => a.Op)).ToList();
        var preview = await changesets.PreviewAsync(candidate, ct);
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

        var summaries = new List<string>();
        foreach (var (op, dependsOn) in additions)
        {
            var summary = await SummarizeAsync(op, ct);
            summaries.Add(summary);
            _steps.Add(new AssistantStagedStep(op, summary, dependsOn.Distinct().Order().ToList()));
            if (op is CreateCollectionOp create)
                _createdByName[create.Ref] = (create.Ref, _steps.Count - 1);
        }

        Diff = preview.Diff;
        DiffHash = preview.DiffHash;

        return AssistantToolResult.Ok(JsonSerializer.SerializeToElement(new
        {
            status = "staged",
            steps = summaries,
            note = "Staged, not applied. Stage every change the request needs; Nostos applies or presents them together when you finish.",
            changesetSoFar = preview.Diff!.Counts,
        }, Json));
    }

    // ------------------------------------------------------------------
    // Step sentences
    // ------------------------------------------------------------------

    private async Task<string> SummarizeAsync(LibraryChangeOp op, CancellationToken ct)
    {
        switch (op)
        {
            case CreateCollectionOp create:
                return create.Parent is { } parent
                    ? $"Create collection “{create.Name}” inside “{await NameAsync(parent, ct)}”"
                    : $"Create collection “{create.Name}”";
            case RenameCollectionOp rename:
                return $"Rename “{await NameAsync(rename.Collection, ct)}” to “{rename.Name}”";
            case MoveCollectionOp move:
                return move.NewParent is { } newParent
                    ? $"Move “{await NameAsync(move.Collection, ct)}” into “{await NameAsync(newParent, ct)}”"
                    : $"Move “{await NameAsync(move.Collection, ct)}” to the top level";
            case DeleteCollectionOp delete:
            {
                var name = await NameAsync(delete.Collection, ct);
                var descendants = delete.Collection.Id is { } id ? await CountDescendantsAsync(id, ct) : 0;
                return descendants switch
                {
                    0 => $"Delete collection “{name}”",
                    1 => $"Delete collection “{name}” and its subcollection",
                    _ => $"Delete collection “{name}” and its {descendants} subcollections",
                };
            }
            case AddBooksToCollectionOp add:
                return $"Add {await BooksPhraseAsync(add.BookIds, ct)} to “{await NameAsync(add.Collection, ct)}”";
            case RemoveBooksFromCollectionOp remove:
                return $"Remove {await BooksPhraseAsync(remove.BookIds, ct)} from “{await NameAsync(remove.Collection, ct)}”";
            case SetBookCollectionsOp set:
            {
                var title = await BookTitleAsync(set.BookId, ct);
                if (set.Collections.Count == 0)
                    return $"Take “{title}” out of every collection";
                var names = new List<string>();
                foreach (var collection in set.Collections)
                    names.Add($"“{await NameAsync(collection, ct)}”");
                return $"Put “{title}” in {string.Join(", ", names)} only";
            }
            case UpdateBookOp update:
            {
                var fields = new List<string>();
                if (update.Title is not null) fields.Add("title");
                if (update.Author is not null) fields.Add("author");
                if (update.Description is not null) fields.Add("description");
                if (update.Rating is not null) fields.Add("rating");
                if (update.IsFavorite is not null) fields.Add(update.IsFavorite.Value ? "mark favourite" : "unmark favourite");
                if (update.PersonalReview is not null) fields.Add("review");
                if (update.IsFinished is not null) fields.Add(update.IsFinished.Value ? "mark finished" : "mark not finished");
                return $"Edit “{await BookTitleAsync(update.BookId, ct)}”: {string.Join(", ", fields)}";
            }
            default:
                return "Change the library";
        }
    }

    private async Task<string> NameAsync(LibraryCollectionRef reference, CancellationToken ct)
    {
        if (reference.Ref is { } created)
        {
            return _steps.Select(s => s.Op).OfType<CreateCollectionOp>()
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
        var result = await library.GetBookAsync(bookId, ct);
        return result.Data is BookDto book ? book.Title : "a book";
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

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Services.Library;

/// <summary>
/// What a changeset did: the before/after snapshot, the diff the user reviews
/// and the undo record. Extracted from the changeset service (issue #1611) so
/// the diff rules — including the hash a reviewed changeset is pinned to — are
/// one pure, testable unit rather than 250 lines inside a command runner.
/// </summary>
internal static class LibraryChangesetDiff
{
    private static readonly JsonSerializerOptions HashJson = new(JsonSerializerDefaults.Web);

    /// <summary>The library's committed state version; a database without the row reports "0".</summary>
    public static async Task<string> CurrentVersionAsync(NostosDbContext db, CancellationToken ct) =>
        await db.LibraryStates.AsNoTracking().Select(s => s.StateVersion).FirstOrDefaultAsync(ct) ?? "0";

    public sealed record LibrarySnapshot(
        string Version,
        IReadOnlyDictionary<Guid, CollectionState> Collections,
        IReadOnlyDictionary<Guid, HashSet<Guid>> Memberships,
        IReadOnlyDictionary<Guid, BookFieldsState> Books,
        IReadOnlyDictionary<Guid, TopicKindState> TopicKinds,
        IReadOnlyDictionary<Guid, TopicKindAssignmentState> TopicKindAssignments);

    public static async Task<LibrarySnapshot> SnapshotAsync(
        NostosDbContext db,
        IReadOnlyCollection<Guid> bookIds,
        IReadOnlyCollection<Guid> topicIds,
        CancellationToken ct)
    {
        var collections = await db.Collections.AsNoTracking()
            .Select(c => new { c.Id, c.Name, c.ParentId })
            .ToListAsync(ct);

        var memberships = (await db.BookCollections.AsNoTracking()
                .Select(bc => new { bc.BookId, bc.CollectionId })
                .ToListAsync(ct))
            .GroupBy(m => m.BookId)
            .ToDictionary(g => g.Key, g => g.Select(m => m.CollectionId).ToHashSet());

        var books = bookIds.Count == 0
            ? new Dictionary<Guid, BookFieldsState>()
            : (await db.Books.AsNoTracking()
                    .Where(b => bookIds.Contains(b.Id))
                    .Select(b => new
                    {
                        b.Id,
                        b.Title,
                        b.Author,
                        b.Metadata.Description,
                        // The book's values: they live on the work.
                        b.Work!.Rating,
                        b.Work.IsFavorite,
                        b.Work.PersonalReview,
                        b.Progress.FinishedAt,
                        b.Progress.ProgressPercent,
                        EditionRating = b.Progress.Rating,
                    })
                    .ToListAsync(ct))
                .ToDictionary(b => b.Id, b => new BookFieldsState(
                    b.Title, b.Author, b.Description, b.Rating, b.IsFavorite,
                    b.PersonalReview, b.FinishedAt, b.ProgressPercent, b.EditionRating));

        return new LibrarySnapshot(
            await CurrentVersionAsync(db, ct),
            collections.ToDictionary(c => c.Id, c => new CollectionState(c.Name, c.ParentId)),
            memberships,
            books,
            await TopicKindsAsync(db, ct),
            await TopicKindAssignmentsAsync(db, topicIds, ct));
    }

    private static async Task<Dictionary<Guid, TopicKindState>> TopicKindsAsync(
        NostosDbContext db,
        CancellationToken ct) =>
        (await db.TopicKinds.AsNoTracking()
                .Select(k => new { k.Id, k.Name, k.ColorKey, k.SortOrder })
                .ToListAsync(ct))
            .ToDictionary(k => k.Id, k => new TopicKindState(k.Name, k.ColorKey, k.SortOrder));

    private static async Task<Dictionary<Guid, TopicKindAssignmentState>> TopicKindAssignmentsAsync(
        NostosDbContext db,
        IReadOnlyCollection<Guid> topicIds,
        CancellationToken ct)
    {
        if (topicIds.Count == 0)
            return [];

        return (await db.Topics.AsNoTracking()
                .Where(t => topicIds.Contains(t.Id))
                .Select(t => new { t.Id, t.Topic, t.KindId, t.KindSource })
                .ToListAsync(ct))
            .ToDictionary(t => t.Id, t => new TopicKindAssignmentState(t.Topic, t.KindId, t.KindSource));
    }

    public static async Task<LibraryChangesetDiffDto> BuildAsync(
        NostosDbContext db,
        LibrarySnapshot before,
        LibrarySnapshot after,
        CancellationToken ct)
    {
        string? NameOf(Guid? id) =>
            id is not { } value ? null
            : after.Collections.TryGetValue(value, out var a) ? a.Name
            : before.Collections.TryGetValue(value, out var b) ? b.Name
            : null;

        int MembersOf(LibrarySnapshot snapshot, Guid collectionId) =>
            snapshot.Memberships.Values.Count(set => set.Contains(collectionId));

        var collections = new List<LibraryCollectionChangeDto>();
        foreach (var (id, state) in after.Collections)
        {
            if (!before.Collections.TryGetValue(id, out var previous))
            {
                collections.Add(new LibraryCollectionChangeDto(id, "created", state.Name, null,
                    NameOf(state.ParentId), null, false, MembersOf(after, id)));
            }
            else if (previous.Name != state.Name || previous.ParentId != state.ParentId)
            {
                var parentChanged = previous.ParentId != state.ParentId;
                collections.Add(new LibraryCollectionChangeDto(id, "updated", state.Name,
                    previous.Name != state.Name ? previous.Name : null,
                    NameOf(state.ParentId),
                    parentChanged ? NameOf(previous.ParentId) : null,
                    parentChanged,
                    MembersOf(after, id)));
            }
        }

        foreach (var (id, previous) in before.Collections)
        {
            if (!after.Collections.ContainsKey(id))
                collections.Add(new LibraryCollectionChangeDto(id, "deleted", previous.Name, null,
                    NameOf(previous.ParentId), null, false, MembersOf(before, id)));
        }

        var changedMembership = new List<(Guid BookId, List<Guid> Added, List<Guid> Removed)>();
        foreach (var bookId in before.Memberships.Keys.Union(after.Memberships.Keys))
        {
            var was = before.Memberships.GetValueOrDefault(bookId) ?? [];
            var now = after.Memberships.GetValueOrDefault(bookId) ?? [];
            var added = now.Except(was).ToList();
            var removed = was.Except(now).ToList();
            if (added.Count > 0 || removed.Count > 0)
                changedMembership.Add((bookId, added, removed));
        }

        var bookIds = changedMembership.Select(m => m.BookId)
            .Concat(after.Books.Keys)
            .Distinct()
            .ToList();
        var titles = bookIds.Count == 0
            ? new Dictionary<Guid, (string Title, string? Author)>()
            : (await db.Books.AsNoTracking()
                    .Where(b => bookIds.Contains(b.Id))
                    .Select(b => new { b.Id, b.Title, b.Author })
                    .ToListAsync(ct))
                .ToDictionary(b => b.Id, b => (b.Title, b.Author));

        var memberships = changedMembership
            .Select(m => new LibraryMembershipChangeDto(
                m.BookId,
                titles.GetValueOrDefault(m.BookId).Title ?? "Untitled",
                titles.GetValueOrDefault(m.BookId).Author,
                m.Added.Select(id => NameOf(id) ?? "Unknown collection").Order(StringComparer.CurrentCulture).ToList(),
                m.Removed.Select(id => NameOf(id) ?? "Unknown collection").Order(StringComparer.CurrentCulture).ToList()))
            .OrderBy(m => m.Title, StringComparer.CurrentCulture)
            .ToList();

        var edits = new List<LibraryBookEditDto>();
        foreach (var (bookId, now) in after.Books)
        {
            if (!before.Books.TryGetValue(bookId, out var was))
                continue;
            var fields = FieldChanges(was, now);
            if (fields.Count > 0)
                edits.Add(new LibraryBookEditDto(bookId, was.Title, fields));
        }
        edits.Sort((a, b) => StringComparer.CurrentCulture.Compare(a.Title, b.Title));

        collections.Sort((a, b) =>
        {
            var byKind = KindOrder(a.Kind).CompareTo(KindOrder(b.Kind));
            return byKind != 0 ? byKind : StringComparer.CurrentCulture.Compare(a.Name, b.Name);
        });

        var topicKinds = BuildTopicKindChanges(before, after);
        var topicKindAssignments = BuildTopicKindAssignments(before, after);

        var counts = new LibraryChangesetCountsDto(
            CollectionsCreated: collections.Count(c => c.Kind == "created"),
            CollectionsDeleted: collections.Count(c => c.Kind == "deleted"),
            CollectionsUpdated: collections.Count(c => c.Kind == "updated"),
            BooksTouched: memberships.Select(m => m.BookId).Concat(edits.Select(e => e.BookId)).Distinct().Count(),
            MembershipsAdded: memberships.Sum(m => m.Added.Count),
            MembershipsRemoved: memberships.Sum(m => m.Removed.Count),
            BooksLosingMembership: memberships.Count(m => m.Removed.Count > 0),
            BookEdits: edits.Count,
            TopicKindsCreated: topicKinds.Count(k => k.Kind == "created"),
            TopicKindChanges: topicKindAssignments.Count);

        return new LibraryChangesetDiffDto(collections, memberships, edits, counts, topicKinds, topicKindAssignments);
    }

    private static List<LibraryTopicKindChangeDto> BuildTopicKindChanges(
        LibrarySnapshot before,
        LibrarySnapshot after)
    {
        var changes = new List<LibraryTopicKindChangeDto>();
        foreach (var (id, kind) in after.TopicKinds)
        {
            if (!before.TopicKinds.TryGetValue(id, out var previous))
            {
                changes.Add(new LibraryTopicKindChangeDto(
                    id, "created", kind.Name, null, kind.ColorKey, 0));
            }
            else if (previous != kind)
            {
                changes.Add(new LibraryTopicKindChangeDto(
                    id, "updated", kind.Name, previous.Name, kind.ColorKey, 0));
            }
        }

        var counts = after.TopicKindAssignments
            .GroupBy(pair => pair.Value.KindId)
            .Where(group => group.Key is not null)
            .ToDictionary(group => group.Key!.Value, group => group.Count());
        for (var index = 0; index < changes.Count; index++)
        {
            changes[index] = changes[index] with
            {
                TopicCount = counts.GetValueOrDefault(changes[index].Id),
            };
        }

        changes.Sort((a, b) =>
        {
            var byKind = KindOrder(a.Kind).CompareTo(KindOrder(b.Kind));
            return byKind != 0 ? byKind : StringComparer.CurrentCulture.Compare(a.Name, b.Name);
        });
        return changes;
    }

    private static List<LibraryTopicKindAssignmentDto> BuildTopicKindAssignments(
        LibrarySnapshot before,
        LibrarySnapshot after)
    {
        string? KindName(Guid? kindId) =>
            kindId is { } id
                ? after.TopicKinds.TryGetValue(id, out var kind) ? kind.Name : null
                : null;

        string? PreviousKindName(Guid? kindId) =>
            kindId is { } id
                ? before.TopicKinds.TryGetValue(id, out var kind) ? kind.Name : null
                : null;

        var changed = new List<LibraryTopicKindAssignmentDto>();
        foreach (var topicId in before.TopicKindAssignments.Keys.Union(after.TopicKindAssignments.Keys))
        {
            var was = before.TopicKindAssignments.GetValueOrDefault(topicId);
            var now = after.TopicKindAssignments.GetValueOrDefault(topicId);
            if (was == now)
                continue;

            var afterKindId = now?.KindId;
            changed.Add(new LibraryTopicKindAssignmentDto(
                topicId,
                now?.TopicName ?? was?.TopicName ?? "Topic",
                afterKindId,
                KindName(afterKindId),
                PreviousKindName(was?.KindId),
                now?.Source));
        }

        changed.Sort((a, b) =>
        {
            var byKind = StringComparer.CurrentCulture.Compare(
                a.KindName ?? "\uFFFF", b.KindName ?? "\uFFFF");
            if (byKind != 0)
                return byKind;
            var byTopic = StringComparer.CurrentCulture.Compare(a.TopicName, b.TopicName);
            return byTopic != 0 ? byTopic : a.TopicId.CompareTo(b.TopicId);
        });
        return changed;
    }

    private static int KindOrder(string kind) => kind switch
    {
        "deleted" => 0,
        "created" => 1,
        _ => 2,
    };

    private static List<LibraryFieldChangeDto> FieldChanges(BookFieldsState was, BookFieldsState now)
    {
        var fields = new List<LibraryFieldChangeDto>();
        void Add(string field, string? before, string? after)
        {
            if (!string.Equals(before, after, StringComparison.Ordinal))
                fields.Add(new LibraryFieldChangeDto(field, before, after));
        }

        Add("Title", was.Title, now.Title);
        Add("Author", was.Author, now.Author);
        Add("Description", was.Description, now.Description);
        Add("Rating", was.Rating.ToString(CultureInfo.InvariantCulture), now.Rating.ToString(CultureInfo.InvariantCulture));
        if (was.EditionRating is { } wasEdition && now.EditionRating is { } nowEdition)
            Add("Edition rating", wasEdition.ToString(CultureInfo.InvariantCulture), nowEdition.ToString(CultureInfo.InvariantCulture));
        Add("Favorite", was.IsFavorite ? "Yes" : "No", now.IsFavorite ? "Yes" : "No");
        Add("Review", was.PersonalReview, now.PersonalReview);
        Add("Finished", was.FinishedAt is null ? "No" : "Yes", now.FinishedAt is null ? "No" : "Yes");
        return fields;
    }

    /// <summary>
    /// A stable fingerprint of what the changeset does. Collections the
    /// changeset creates get a new id on every run, so they are identified by
    /// name and place; everything else by id.
    /// </summary>
    public static string Hash(LibraryChangesetDiffDto diff)
    {
        var createdKindIds = diff.TopicKinds
            .Where(k => k.Kind == "created")
            .Select(k => k.Id)
            .ToHashSet();
        var canonical = new
        {
            collections = diff.Collections.Select(c => new
            {
                id = c.Kind == "created" ? null : c.Id.ToString(),
                c.Kind, c.Name, c.PreviousName, c.ParentName, c.PreviousParentName, c.ParentChanged, c.BookCount,
            }),
            memberships = diff.Memberships.Select(m => new { m.BookId, m.Added, m.Removed }),
            edits = diff.BookEdits.Select(e => new { e.BookId, e.Fields }),
            topicKinds = diff.TopicKinds.Select(k => new
            {
                id = k.Kind == "created" ? null : k.Id.ToString(),
                k.Kind, k.Name, k.PreviousName, k.ColorKey, k.TopicCount,
            }),
            topicKindAssignments = diff.TopicKindAssignments.Select(a => new
            {
                a.TopicId,
                // A kind created in this changeset gets a new id on every run.
                after = a.KindId is { } id && createdKindIds.Contains(id) ? null : a.KindId?.ToString(),
                a.KindName,
                a.PreviousKindName,
                a.Source,
            }),
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(canonical, HashJson);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public static LibraryUndoSnapshot BuildUndo(LibrarySnapshot before, LibrarySnapshot after)
    {
        var collections = before.Collections.Keys.Union(after.Collections.Keys)
            .Select(id => new CollectionSnapshot(
                id,
                before.Collections.GetValueOrDefault(id),
                after.Collections.GetValueOrDefault(id)))
            .Where(s => s.Before != s.After)
            .ToList();

        var memberships = before.Memberships.Keys.Union(after.Memberships.Keys)
            .Select(bookId => new MembershipSnapshot(
                bookId,
                (before.Memberships.GetValueOrDefault(bookId) ?? []).Order().ToList(),
                (after.Memberships.GetValueOrDefault(bookId) ?? []).Order().ToList()))
            .Where(s => !s.Before.SequenceEqual(s.After))
            .ToList();

        var books = after.Books
            .Where(pair => before.Books.TryGetValue(pair.Key, out var was) && was != pair.Value)
            .Select(pair => new BookFieldsSnapshot(pair.Key, before.Books[pair.Key], pair.Value))
            .ToList();

        var topicKinds = before.TopicKinds.Keys.Union(after.TopicKinds.Keys)
            .Select(id => new TopicKindSnapshot(
                id,
                before.TopicKinds.GetValueOrDefault(id),
                after.TopicKinds.GetValueOrDefault(id)))
            .Where(snapshot => snapshot.Before != snapshot.After)
            .ToList();

        var topicKindAssignments = before.TopicKindAssignments.Keys.Union(after.TopicKindAssignments.Keys)
            .Where(topicId => before.TopicKindAssignments.GetValueOrDefault(topicId)
                != after.TopicKindAssignments.GetValueOrDefault(topicId))
            .Select(topicId => new TopicKindAssignmentSnapshot(
                topicId,
                after.TopicKindAssignments.GetValueOrDefault(topicId)?.TopicName
                    ?? before.TopicKindAssignments.GetValueOrDefault(topicId)?.TopicName
                    ?? "Topic",
                before.TopicKindAssignments.GetValueOrDefault(topicId)
                    is { } was ? new TopicKindAssignmentState(was.TopicName, was.KindId, was.Source)
                    : new TopicKindAssignmentState("Topic", null, null),
                after.TopicKindAssignments.GetValueOrDefault(topicId)
                    is { } now ? new TopicKindAssignmentState(now.TopicName, now.KindId, now.Source)
                    : new TopicKindAssignmentState("Topic", null, null)))
            .ToList();

        return new LibraryUndoSnapshot(collections, memberships, books, topicKinds, topicKindAssignments);
    }
}

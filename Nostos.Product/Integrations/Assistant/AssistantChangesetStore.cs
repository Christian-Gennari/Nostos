using System.Security.Cryptography;
using Nostos.Backend.Services.Library;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Integrations.Assistant;

/// <summary>The lifecycle of a turn's library changes.</summary>
public enum AssistantChangesetStatus
{
    /// <summary>Prepared and waiting for the user; nothing is applied.</summary>
    Review,
    Applied,
    Undone,
    Discarded,
}

/// <summary>
/// One turn's changes as the server holds them. Only the server's copy of the
/// steps is ever applied: a client names a changeset and presents its token,
/// it never sends operations.
/// </summary>
public sealed class StoredAssistantChangeset
{
    public required string ChangesetId { get; init; }
    public required string OwnerKey { get; init; }
    public required string ConversationKey { get; init; }
    public required string TurnId { get; init; }
    public required string ActionToken { get; init; }
    public required IReadOnlyList<AssistantStagedStep> Steps { get; init; }

    /// <summary>
    /// The diff as the review first described it: every step's effect, whether
    /// or not the reader has since left it out. A preview recomputes
    /// <see cref="Diff"/> without the excluded steps, so the card's kind groups
    /// read names, colours and "was …" notes from here and their tick state
    /// from the steps.
    /// </summary>
    public required LibraryChangesetDiffDto Baseline { get; init; }

    public AssistantChangesetStatus Status { get; set; }

    /// <summary>
    /// Steps the user themselves unticked (plus their dependants). Kept separate
    /// from server-kept steps in <see cref="Kept"/> so unticking a topic never
    /// conflates with a server skip.
    /// </summary>
    public HashSet<int> Excluded { get; set; } = [];

    /// <summary>
    /// Steps the server itself permanently left out of the review (for example,
    /// the reader set that topic's kind themselves). Grows monotonically for the
    /// life of the review and never enters <see cref="Excluded"/>.
    /// </summary>
    public Dictionary<int, string> Kept { get; set; } = [];

    public required LibraryChangesetDiffDto Diff { get; set; }
    public required string DiffHash { get; set; }
    public IReadOnlyList<string> ReviewReasons { get; set; } = [];
    public LibraryUndoSnapshot? Undo { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// Why the server left a step out on the last recompute: every entry in
    /// <see cref="Kept"/> together with any created kind that has no active
    /// topic left. When the user ticks one of those topics back on, the create
    /// step drops out of <see cref="SkippedReasons"/> while <see cref="Kept"/>
    /// keeps the claimed siblings.
    /// </summary>
    public Dictionary<int, string> SkippedReasons { get; set; } = [];

    /// <summary>
    /// The two turn conditions the change policy judged, kept so a preview can
    /// recompute the reasons against the library as it is now instead of
    /// keeping a reason whose change is gone.
    /// </summary>
    public bool ReadUntrustedContent { get; init; }

    public bool TurnIncomplete { get; init; }

    /// <summary>Set while an apply or undo for this changeset is running.</summary>
    public bool Busy { get; set; }

    public IReadOnlyList<LibraryChangeOp> IncludedOps =>
        Steps.Where((_, index) => !Excluded.Contains(index)
                && !Kept.ContainsKey(index)
                && !SkippedReasons.ContainsKey(index))
            .Select(s => s.Op)
            .ToList();

    public AssistantChangesetDto ToDto() => new(
        ChangesetId,
        Status switch
        {
            AssistantChangesetStatus.Review => "review",
            AssistantChangesetStatus.Applied => "applied",
            AssistantChangesetStatus.Undone => "undone",
            _ => "discarded",
        },
        Steps.Select((step, index) => new AssistantChangeStepDto(
            index,
            step.Summary,
            !Excluded.Contains(index) && !Kept.ContainsKey(index) && !SkippedReasons.ContainsKey(index),
            step.DependsOn,
            StepTopicKind(index),
            SkippedReasons.GetValueOrDefault(index) ?? Kept.GetValueOrDefault(index))).ToList(),
        WithStepIndexes(),
        DiffHash,
        ActionToken,
        ReviewReasons,
        Status == AssistantChangesetStatus.Applied && Undo is not null);

    /// <summary>
    /// One step's topic-kind effect, read from the baseline diff so a step the
    /// reader has left out keeps its name, colour and "was …" note on the card.
    /// Null for a step that does not touch topic kinds.
    /// </summary>
    private AssistantStepTopicKindDto? StepTopicKind(int index)
    {
        switch (Steps[index].Op)
        {
            case CreateTopicKindOp create:
            {
                var created = Baseline.TopicKinds.FirstOrDefault(kind =>
                    kind.Kind == "created" && string.Equals(kind.Name, create.Name, StringComparison.Ordinal));
                return new AssistantStepTopicKindDto(
                    KindKey: created?.Id.ToString() ?? create.Ref,
                    KindName: create.Name,
                    ColorKey: created?.ColorKey ?? create.ColorKey,
                    CreatesKind: true,
                    IsNewKind: true,
                    TopicId: null,
                    TopicName: null,
                    PreviousKindName: null);
            }
            case SetTopicKindOp setKind:
            {
                if (setKind.TopicIds.Count != 1)
                    return null;

                var topicId = setKind.TopicIds[0];
                var assignment = Baseline.TopicKindAssignments
                    .FirstOrDefault(candidate => candidate.TopicId == topicId);
                if (assignment is null)
                    return null;

                return new AssistantStepTopicKindDto(
                    KindKey: assignment.KindId?.ToString() ?? "none",
                    KindName: assignment.KindName ?? "No kind",
                    ColorKey: assignment.KindColorKey,
                    CreatesKind: false,
                    IsNewKind: assignment.KindId is { } kindId
                        && Baseline.TopicKinds.Any(kind => kind.Id == kindId),
                    TopicId: assignment.TopicId,
                    TopicName: assignment.TopicName,
                    PreviousKindName: assignment.PreviousKindName);
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// Names the review step each topic-kind assignment belongs to, so the
    /// review card can tick or untick one topic without the whole group. The
    /// diff is recomputed on every preview, so this is worked out from the
    /// steps that are present now, never stored.
    /// </summary>
    private LibraryChangesetDiffDto WithStepIndexes()
    {
        if (Diff.TopicKindAssignments.Count == 0)
            return Diff;

        var stepByTopic = new Dictionary<Guid, int>();
        for (var index = 0; index < Steps.Count; index++)
        {
            if (Steps[index].Op is not SetTopicKindOp setKind)
                continue;
            foreach (var topicId in setKind.TopicIds)
                stepByTopic[topicId] = index;
        }

        var assignments = Diff.TopicKindAssignments
            .Select(assignment => assignment.StepIndex is null
                && stepByTopic.TryGetValue(assignment.TopicId, out var index)
                    ? assignment with { StepIndex = index }
                    : assignment)
            .ToList();

        var stepByKindName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < Steps.Count; index++)
        {
            if (Steps[index].Op is CreateTopicKindOp createKind)
                stepByKindName[createKind.Name] = index;
        }

        var kinds = Diff.TopicKinds
            .Select(kind => kind.StepIndex is null
                && kind.Kind == "created"
                && stepByKindName.TryGetValue(kind.Name, out var index)
                    ? kind with { StepIndex = index }
                    : kind)
            .ToList();

        return assignments.SequenceEqual(Diff.TopicKindAssignments)
            && kinds.SequenceEqual(Diff.TopicKinds)
            ? Diff
            : Diff with { TopicKindAssignments = assignments, TopicKinds = kinds };
    }
}

/// <summary>
/// Process-local, bounded store of turn changesets. A review waits an hour; an
/// applied changeset can be undone for a day. One review per conversation: a
/// newer one discards the older, so a stale card can never be applied by
/// mistake. Undo records are a convenience held in memory, not history: they
/// do not survive a restart, and the card then says undo is no longer available.
///
/// Every changeset belongs to an owner (<see cref="IAssistantOwner"/>), and
/// conversations are matched within it. Each owner has its own capacity, so
/// an owner that adds past it only expires its own changesets. When the
/// process as a whole is full, room comes from whichever owner holds the
/// most, so one account cannot push out another that holds fewer.
/// </summary>
public sealed class AssistantChangesetStore(TimeProvider? time = null)
{
    public static readonly TimeSpan ReviewLifetime = TimeSpan.FromHours(1);
    public static readonly TimeSpan UndoLifetime = TimeSpan.FromHours(24);
    public const int Capacity = 500;
    public const int OwnerCapacity = 100;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, StoredAssistantChangeset> _byId = new(StringComparer.Ordinal);

    private DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>Stores a new changeset, discarding any older review in the same conversation.</summary>
    public StoredAssistantChangeset Add(
        string ownerKey,
        string conversationKey,
        string turnId,
        IReadOnlyList<AssistantStagedStep> steps,
        LibraryChangesetDiffDto diff,
        string diffHash,
        AssistantChangesetStatus status,
        IReadOnlyList<string> reviewReasons,
        LibraryUndoSnapshot? undo,
        bool readUntrustedContent = false,
        bool turnIncomplete = false)
    {
        lock (_gate)
        {
            RemoveExpired();
            MakeRoom(ownerKey);
            if (status == AssistantChangesetStatus.Review)
            {
                foreach (var older in _byId.Values.Where(c =>
                    c.OwnerKey == ownerKey
                    && c.ConversationKey == conversationKey
                    && c.Status == AssistantChangesetStatus.Review
                    && !c.Busy))
                {
                    older.Status = AssistantChangesetStatus.Discarded;
                }
            }

            var stored = new StoredAssistantChangeset
            {
                ChangesetId = Guid.NewGuid().ToString("N"),
                OwnerKey = ownerKey,
                ConversationKey = conversationKey,
                TurnId = turnId,
                ActionToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant(),
                Steps = steps,
                Status = status,
                Baseline = diff,
                Diff = diff,
                DiffHash = diffHash,
                ReviewReasons = reviewReasons,
                Undo = undo,
                ReadUntrustedContent = readUntrustedContent,
                TurnIncomplete = turnIncomplete,
                ExpiresAt = Now + (status == AssistantChangesetStatus.Review ? ReviewLifetime : UndoLifetime),
            };
            _byId[stored.ChangesetId] = stored;
            return stored;
        }
    }

    /// <summary>
    /// The changeset a turn already applied, so a retried delivery of the same
    /// turn reports it instead of applying the changes a second time.
    /// </summary>
    public StoredAssistantChangeset? FindAppliedForTurn(string ownerKey, string conversationKey, string turnId)
    {
        lock (_gate)
        {
            return _byId.Values.FirstOrDefault(c =>
                c.OwnerKey == ownerKey
                && c.ConversationKey == conversationKey
                && c.TurnId == turnId
                && c.Status is AssistantChangesetStatus.Applied or AssistantChangesetStatus.Undone);
        }
    }

    public enum Lookup { Found, NotFound, TokenMismatch }

    public Lookup Find(string? changesetId, string? token, out StoredAssistantChangeset? changeset)
    {
        lock (_gate)
        {
            RemoveExpired();
            changeset = null;
            if (string.IsNullOrWhiteSpace(changesetId) || !_byId.TryGetValue(changesetId, out var found))
                return Lookup.NotFound;

            if (string.IsNullOrEmpty(token)
                || !CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(token),
                    System.Text.Encoding.UTF8.GetBytes(found.ActionToken)))
            {
                return Lookup.TokenMismatch;
            }

            changeset = found;
            return Lookup.Found;
        }
    }

    /// <summary>
    /// Claims a changeset for one apply or undo. Two clicks, two tabs or a retry
    /// racing the first request cannot both run it.
    /// </summary>
    public bool TryBegin(StoredAssistantChangeset changeset, AssistantChangesetStatus expected)
    {
        lock (_gate)
        {
            if (changeset.Busy || changeset.Status != expected)
                return false;
            changeset.Busy = true;
            return true;
        }
    }

    public void End(StoredAssistantChangeset changeset, Action<StoredAssistantChangeset> update)
    {
        lock (_gate)
        {
            update(changeset);
            changeset.Busy = false;
            changeset.ExpiresAt = Now + (changeset.Status == AssistantChangesetStatus.Review
                ? ReviewLifetime
                : UndoLifetime);
        }
    }

    public void Update(StoredAssistantChangeset changeset, Action<StoredAssistantChangeset> update)
    {
        lock (_gate)
            update(changeset);
    }

    private void RemoveExpired()
    {
        var now = Now;
        foreach (var expired in _byId.Values.Where(c => !c.Busy && c.ExpiresAt <= now).ToList())
            _byId.Remove(expired.ChangesetId);
    }

    /// <summary>
    /// Leaves room for one more changeset from <paramref name="ownerKey"/>.
    /// Only adding makes room: a lookup never evicts anything.
    /// </summary>
    private void MakeRoom(string ownerKey)
    {
        while (_byId.Values.Count(c => c.OwnerKey == ownerKey) >= OwnerCapacity)
        {
            if (!EvictOldestIdle(ownerKey))
                break;
        }

        while (_byId.Count >= Capacity)
        {
            // The owner holding the most gives up its oldest. On a tie the
            // adding owner goes first, so adding never costs an equal peer.
            var largest = _byId.Values
                .GroupBy(c => c.OwnerKey)
                .Where(g => g.Any(c => !c.Busy))
                .OrderByDescending(g => g.Count())
                .ThenByDescending(g => g.Key == ownerKey)
                .Select(g => g.Key)
                .FirstOrDefault();
            if (largest is null || !EvictOldestIdle(largest))
                break;
        }
    }

    private bool EvictOldestIdle(string ownerKey)
    {
        var oldest = _byId.Values
            .Where(c => c.OwnerKey == ownerKey && !c.Busy)
            .MinBy(c => c.ExpiresAt);
        return oldest is not null && _byId.Remove(oldest.ChangesetId);
    }
}

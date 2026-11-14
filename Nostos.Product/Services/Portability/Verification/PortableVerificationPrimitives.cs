using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Services.Portability.Verification;

/// <summary>An entity property checked by an executable comparison entry.</summary>
internal sealed record EntityField(string Entity, string Property);

/// <summary>
/// Coverage view projected from the executable comparison tables. A record
/// property is covered when an entry with that property name exists; there is no
/// way to declare coverage without also declaring the comparison delegate.
/// </summary>
internal sealed record RecordComparisonSpec(
    IReadOnlyList<string> ComparedProperties,
    IReadOnlyList<EntityField> CandidateFields,
    IReadOnlyDictionary<string, string> NotComparedProperties);

internal sealed record ComparisonEntry<TContext>(
    IReadOnlyList<string> ArchiveProperties,
    IReadOnlyList<EntityField> CandidateFields,
    Action<TContext, FailureList> Compare);

/// <summary>
/// Retains a bounded number of specific mismatch details while counting every
/// detected mismatch, so a heavily divergent candidate cannot exhaust memory.
/// </summary>
internal sealed class FailureList : List<PortableLibraryVerificationFailure>
{
    internal long TotalCount { get; private set; }

    internal new void Add(PortableLibraryVerificationFailure failure)
    {
        TotalCount++;
        if (Count < PortableVerificationPrimitives.MaxRetainedFailures)
        {
            base.Add(failure);
        }
    }
}

internal sealed record WorkContext(WorkModel Row, PortableWork Expected);

internal sealed record BookContext(
    BookModel Row,
    PortableBook Expected,
    IReadOnlyDictionary<(Guid BookId, string Kind), PortableArchiveMediaEntry> Media);

internal sealed record BookMetadataContext(Guid BookId, BookMetadata Row, PortableBookMetadata Expected);

internal sealed record BookProgressContext(Guid BookId, ReadingProgress Row, PortableReadingProgress Expected);

internal sealed record CollectionContext(CollectionModel Row, PortableCollection Expected);

internal sealed record NoteContext(NoteModel Row, PortableNote Expected);

internal sealed record TopicContext(TopicModel Row, PortableTopic Expected);

internal sealed record WritingContext(WritingModel Row, PortableWriting Expected);

internal sealed record AcquisitionContext(BookAcquisitionModel Row, PortableBookAcquisition Expected);

internal sealed record ImportLinkContext(NoteImportBookLink Row, PortableNoteImportBookLink Expected);

internal sealed record ReadingListContext(ReadingListEntryModel Row, PortableReadingListEntry Expected);

internal sealed record AssistantContext(AssistantSettingsModel Row, PortableAssistantSettings Expected);

internal sealed record MembershipContext(
    IReadOnlyList<BookCollectionModel> Rows,
    IReadOnlyList<PortableBookCollection> Expected);

internal sealed record NoteTopicContext(
    IReadOnlyList<NoteTopicModel> Rows,
    IReadOnlyList<PortableNoteTopic> Expected);

internal sealed record WritingNoteContext(
    IReadOnlyList<WritingNoteModel> Rows,
    IReadOnlyList<PortableWritingNote> Expected);

/// <summary>
/// Shared comparison primitives: canonical equality, id/field/join-pair
/// comparison and the executable comparison-entry plumbing every domain
/// table uses. Behaviour here is the verifier contract; move code, not rules.
/// </summary>
internal static class PortableVerificationPrimitives
{
    /// <summary>
    /// Maximum number of specific failure details retained in a report. Additional
    /// mismatches are counted in <see cref="PortableLibraryVerificationReport.FailureCount"/>
    /// but not retained, so a heavily divergent candidate cannot exhaust memory.
    /// </summary>
    internal const int MaxRetainedFailures = 64;

    internal static PortableLibraryVerificationFailure Failure(
        string code,
        string entity,
        string? entityId,
        string? field,
        string detail) => new(code, entity, entityId, field, detail);

    internal static void CompareIdSets(
        string entity,
        IEnumerable<Guid> expected,
        IEnumerable<Guid> actual,
        FailureList failures)
    {
        var expectedSet = expected.ToHashSet();
        var actualSet = actual.ToHashSet();
        foreach (var missing in expectedSet.Except(actualSet).OrderBy(id => id))
        {
            failures.Add(Failure(
                PortableLibraryVerificationErrorCodes.MissingEntity,
                entity,
                missing.ToString("D"),
                null,
                $"A {entity} from the prepared import is missing from the candidate database."));
        }

        foreach (var extra in actualSet.Except(expectedSet).OrderBy(id => id))
        {
            failures.Add(Failure(
                PortableLibraryVerificationErrorCodes.UnexpectedEntity,
                entity,
                extra.ToString("D"),
                null,
                $"The candidate database contains a {entity} the prepared import does not declare."));
        }
    }

    internal static void CompareField<T>(
        FailureList failures,
        string entity,
        Guid id,
        string field,
        T actual,
        T expected) =>
        CompareField(failures, entity, id.ToString("D"), field, actual, expected);

    internal static void CompareField<T>(
        FailureList failures,
        string entity,
        string? id,
        string field,
        T actual,
        T expected)
    {
        if (!CanonicalValuesEqual(actual, expected))
        {
            failures.Add(Failure(
                PortableLibraryVerificationErrorCodes.FieldMismatch,
                entity,
                id,
                field,
                $"{entity} field '{field}' does not match the prepared import."));
        }
    }

    /// <summary>
    /// Structural equality with temporal values canonicalised to UTC microsecond
    /// precision. Some relational providers persist microsecond precision while
    /// .NET keeps 100 ns ticks, so a value written and read back can differ from its
    /// source by up to nine ticks; flooring to microseconds removes exactly that
    /// provider rounding and nothing more. It is not a tolerance: any difference of
    /// one microsecond or more still compares unequal. <see cref="DateTime"/> uses
    /// its wall-clock ticks (matching its own equality); <see cref="DateTimeOffset"/>
    /// uses its UTC ticks (matching its instant equality), so the same instant
    /// expressed with a different offset stays equal.
    /// </summary>
    internal static bool CanonicalValuesEqual<T>(T actual, T expected)
    {
        if (typeof(T) == typeof(DateTime))
        {
            return Canonicalize((DateTime)(object)actual!)
                == Canonicalize((DateTime)(object)expected!);
        }

        if (typeof(T) == typeof(DateTime?))
        {
            return Canonicalize((DateTime?)(object?)actual)
                == Canonicalize((DateTime?)(object?)expected);
        }

        if (typeof(T) == typeof(DateTimeOffset))
        {
            return Canonicalize((DateTimeOffset)(object)actual!)
                == Canonicalize((DateTimeOffset)(object)expected!);
        }

        if (typeof(T) == typeof(DateTimeOffset?))
        {
            return Canonicalize((DateTimeOffset?)(object?)actual)
                == Canonicalize((DateTimeOffset?)(object?)expected);
        }

        return EqualityComparer<T>.Default.Equals(actual, expected);
    }

    internal static DateTime Canonicalize(DateTime value) =>
        new(value.Ticks - value.Ticks % 10, value.Kind);

    internal static DateTime? Canonicalize(DateTime? value) =>
        value is { } present ? Canonicalize(present) : null;

    internal static DateTimeOffset Canonicalize(DateTimeOffset value) =>
        new(value.UtcDateTime.Ticks - value.UtcDateTime.Ticks % 10, TimeSpan.Zero);

    internal static DateTimeOffset? Canonicalize(DateTimeOffset? value) =>
        value is { } present ? Canonicalize(present) : null;

    internal static void CompareField(
        FailureList failures,
        string entity,
        Guid id,
        string field,
        string? actual,
        string? expected,
        bool ignoreCase)
    {
        var equal = ignoreCase
            ? string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)
            : string.Equals(actual, expected, StringComparison.Ordinal);
        if (!equal)
        {
            failures.Add(Failure(
                PortableLibraryVerificationErrorCodes.FieldMismatch,
                entity,
                id.ToString("D"),
                field,
                $"{entity} field '{field}' does not match the prepared import."));
        }
    }

    internal static void CompareJoinPairs<TLeft, TRight>(
        FailureList failures,
        string entity,
        string field,
        IEnumerable<(TLeft LeftId, TRight RightId)> expected,
        IEnumerable<(TLeft LeftId, TRight RightId)> actual)
        where TLeft : notnull
        where TRight : notnull
    {
        var expectedList = expected.ToList();
        var actualList = actual.ToList();
        var expectedSet = expectedList.ToHashSet();
        var actualSet = actualList.ToHashSet();
        if (expectedList.Count == actualList.Count
            && expectedSet.Count == expectedList.Count
            && actualSet.Count == actualList.Count
            && expectedSet.SetEquals(actualSet))
        {
            return;
        }

        failures.Add(Failure(
            PortableLibraryVerificationErrorCodes.RelationshipMismatch,
            entity,
            null,
            field,
            $"The candidate {entity} {field} pairs differ from the prepared import."));
    }

    internal static void CompareAddedAt<TKey>(
        FailureList failures,
        string entity,
        string field,
        IReadOnlyDictionary<TKey, DateTime> expected,
        IEnumerable<(TKey Key, DateTime AddedAt)> actual)
        where TKey : notnull
    {
        var seen = new HashSet<TKey>();
        foreach (var (key, addedAt) in actual)
        {
            seen.Add(key);
            if (!expected.TryGetValue(key, out var expectedAddedAt))
            {
                failures.Add(Failure(
                    PortableLibraryVerificationErrorCodes.RelationshipMismatch,
                    entity,
                    null,
                    field,
                    $"The candidate {entity} contains an unexpected {field} pair."));
                continue;
            }

            if (!CanonicalValuesEqual(addedAt, expectedAddedAt))
            {
                failures.Add(Failure(
                    PortableLibraryVerificationErrorCodes.FieldMismatch,
                    entity,
                    null,
                    field,
                    $"A candidate {entity} {field} value does not match the prepared import."));
            }
        }

        foreach (var missing in expected.Keys)
        {
            if (!seen.Contains(missing))
            {
                failures.Add(Failure(
                    PortableLibraryVerificationErrorCodes.RelationshipMismatch,
                    entity,
                    null,
                    field,
                    $"The candidate {entity} is missing an expected {field} pair."));
            }
        }
    }

    internal static string? ResolveMediaFileName(
        IReadOnlyDictionary<(Guid BookId, string Kind), PortableArchiveMediaEntry> media,
        Guid bookId,
        string kind) =>
        media.TryGetValue((bookId, kind), out var descriptor) ? descriptor.FileName : null;

    internal static string ConcreteType(BookModel book) => book switch
    {
        PhysicalBookModel => "physical",
        EBookModel => "ebook",
        AudioBookModel => "audiobook",
        _ => throw new PortableLibraryVerificationException(
            PortableLibraryVerificationErrorCodes.ExpectedStateInvalid,
            "The candidate database contains an unsupported book type."),
    };

    internal static void Run<TContext>(
        IReadOnlyList<ComparisonEntry<TContext>> entries,
        TContext context,
        FailureList failures)
    {
        foreach (var entry in entries)
        {
            entry.Compare(context, failures);
        }
    }

    internal static IReadOnlyList<EntityField> NoFields => [];

    internal static IReadOnlyList<EntityField> Ef(params (string Entity, string Property)[] fields) =>
        fields.Select(field => new EntityField(field.Entity, field.Property)).ToArray();

    internal static ComparisonEntry<TContext> Entry<TContext>(
        string archiveProperty,
        IReadOnlyList<EntityField> candidateFields,
        Action<TContext, FailureList> compare) =>
        new([archiveProperty], candidateFields, compare);

    internal static ComparisonEntry<TContext> PairEntry<TContext>(
        string leftArchiveProperty,
        string rightArchiveProperty,
        IReadOnlyList<EntityField> candidateFields,
        Action<TContext, FailureList> compare) =>
        new([leftArchiveProperty, rightArchiveProperty], candidateFields, compare);
}

using System.Reflection;
using static Nostos.Backend.Services.Portability.Verification.PortableVerificationPrimitives;

namespace Nostos.Backend.Services.Portability.Verification;

internal sealed record CandidateRowCounts(
    long Works,
    long Books,
    long Collections,
    long Memberships,
    long Notes,
    long Topics,
    long NoteTopics,
    long Writings,
    long WritingNotes,
    long Acquisitions,
    long ImportLinks,
    long AssistantSettings,
    long ReadingListEntries);

internal sealed record CandidateDatabaseComparison(
    FailureList Failures,
    CandidateRowCounts Counts,
    long RowsVerified);

/// <summary>
/// Count reconciliation for every verification stage: manifest vs payload,
/// prepared descriptor vs payload, payload vs candidate database. A count
/// dimension without a mapping is a failure, never a silent skip.
/// </summary>
internal static class PortableVerificationCounts
{
    internal static readonly IReadOnlyDictionary<string, string> ManifestCountToDataCount =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(PortableArchiveCounts.Works)] = nameof(MigrationArchiveCounts.Works),
            [nameof(PortableArchiveCounts.Books)] = nameof(MigrationArchiveCounts.Books),
            [nameof(PortableArchiveCounts.Collections)] = nameof(MigrationArchiveCounts.Collections),
            [nameof(PortableArchiveCounts.BookCollections)] = nameof(MigrationArchiveCounts.CollectionMemberships),
            [nameof(PortableArchiveCounts.Notes)] = nameof(MigrationArchiveCounts.Notes),
            [nameof(PortableArchiveCounts.Topics)] = nameof(MigrationArchiveCounts.Topics),
            [nameof(PortableArchiveCounts.NoteTopics)] = nameof(MigrationArchiveCounts.NoteTopics),
            [nameof(PortableArchiveCounts.Writings)] = nameof(MigrationArchiveCounts.Writings),
            [nameof(PortableArchiveCounts.BookAcquisitions)] = nameof(MigrationArchiveCounts.Acquisitions),
        };

    internal static readonly PropertyInfo[] CountProperties = typeof(MigrationArchiveCounts)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(property => property.CanWrite && property.PropertyType == typeof(long))
        .OrderBy(property => property.Name, StringComparer.Ordinal)
        .ToArray();

    internal static readonly string[] ManifestCountProperties = typeof(PortableArchiveCounts)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(property => property.CanWrite)
        .Select(property => property.Name)
        .OrderBy(name => name, StringComparer.Ordinal)
        .ToArray();

    internal static void CompareManifestCounts(
        PortableArchiveManifest manifest,
        PortableLibraryData data,
        FailureList failures) =>
        CompareManifestCounts(manifest.Counts, data, failures);

    /// <summary>
    /// Compares an archive's declared counts with the relational payload it
    /// describes. Shared by the prepared-import pass and the legacy import, which
    /// only has the staged manifest.
    /// </summary>
    internal static void CompareManifestCounts(
        PortableArchiveCounts counts,
        PortableLibraryData data,
        FailureList failures)
    {
        var dataCounts = PortableLibraryCounts.ComputeCounts(data, mediaEntries: 0);
        foreach (var property in typeof(PortableArchiveCounts)
                     .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(property => property.CanWrite))
        {
            if (!ManifestCountToDataCount.TryGetValue(property.Name, out var dataPropertyName))
            {
                failures.Add(Failure(
                    PortableLibraryVerificationErrorCodes.CountUnverified,
                    "manifest",
                    null,
                    property.Name,
                    "A manifest count dimension is not mapped to portable data verification."));
                continue;
            }

            var actual = Convert.ToInt64(property.GetValue(counts));
            var expected = (long)typeof(MigrationArchiveCounts).GetProperty(dataPropertyName)!.GetValue(dataCounts)!;
            if (actual != expected)
            {
                failures.Add(Failure(
                    PortableLibraryVerificationErrorCodes.CountMismatch,
                    "manifest",
                    null,
                    property.Name,
                    "A manifest count does not match the staged relational payload."));
            }
        }
    }

    internal static void ComparePreparedCounts(
        PortableLibraryData data,
        int mediaEntries,
        PreparedPortableImportMetadata metadata,
        FailureList failures)
    {
        var computed = PortableLibraryCounts.ComputeCounts(data, mediaEntries);
        foreach (var property in CountProperties)
        {
            var advertised = (long)property.GetValue(metadata.Counts)!;
            var actual = (long)property.GetValue(computed)!;
            if (advertised != actual)
            {
                failures.Add(Failure(
                    PortableLibraryVerificationErrorCodes.CountMismatch,
                    "counts",
                    null,
                    property.Name,
                    "The prepared descriptor count does not match the staged relational payload."));
            }
        }
    }

    internal static void ValidatePreparedSingleton(
        MigrationArchiveCounts counts,
        FailureList failures)
    {
        if (counts.AssistantSettings is not (0 or 1))
        {
            failures.Add(Failure(
                PortableLibraryVerificationErrorCodes.SingletonMismatch,
                "assistantSettings",
                null,
                nameof(counts.AssistantSettings),
                "The portable assistant setting must be a singleton or absent."));
        }
    }

    internal static void CompareCandidateCounts(
        MigrationArchiveCounts expectedCounts,
        CandidateRowCounts counts,
        long? mediaFiles,
        FailureList failures)
    {
        var actual = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            [nameof(MigrationArchiveCounts.Works)] = counts.Works,
            [nameof(MigrationArchiveCounts.Books)] = counts.Books,
            [nameof(MigrationArchiveCounts.Notes)] = counts.Notes,
            [nameof(MigrationArchiveCounts.Topics)] = counts.Topics,
            [nameof(MigrationArchiveCounts.NoteTopics)] = counts.NoteTopics,
            [nameof(MigrationArchiveCounts.Writings)] = counts.Writings,
            [nameof(MigrationArchiveCounts.WritingNotes)] = counts.WritingNotes,
            [nameof(MigrationArchiveCounts.Collections)] = counts.Collections,
            [nameof(MigrationArchiveCounts.CollectionMemberships)] = counts.Memberships,
            [nameof(MigrationArchiveCounts.Acquisitions)] = counts.Acquisitions,
            [nameof(MigrationArchiveCounts.AssistantSettings)] = counts.AssistantSettings,
            [nameof(MigrationArchiveCounts.NoteImportBookLinks)] = counts.ImportLinks,
            [nameof(MigrationArchiveCounts.ReadingListEntries)] = counts.ReadingListEntries,
        };

        if (mediaFiles is { } verifiedMediaFiles)
        {
            actual[nameof(MigrationArchiveCounts.MediaEntries)] = verifiedMediaFiles;
        }

        foreach (var property in CountProperties)
        {
            if (mediaFiles is null
                && string.Equals(
                    property.Name,
                    nameof(MigrationArchiveCounts.MediaEntries),
                    StringComparison.Ordinal))
            {
                // Media objects live in the host's asset storage, not in the
                // relational candidate; VerifyMediaAsync owns that dimension.
                continue;
            }

            if (!actual.TryGetValue(property.Name, out var value))
            {
                failures.Add(Failure(
                    PortableLibraryVerificationErrorCodes.CountUnverified,
                    "counts",
                    null,
                    property.Name,
                    "A portable count dimension has no candidate verification mapping."));
                continue;
            }

            var expected = (long)property.GetValue(expectedCounts)!;
            if (value != expected)
            {
                failures.Add(Failure(
                    PortableLibraryVerificationErrorCodes.CountMismatch,
                    "counts",
                    null,
                    property.Name,
                    "A candidate portable count does not match the prepared import."));
            }
        }
    }
}

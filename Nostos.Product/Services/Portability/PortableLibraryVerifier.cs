using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Services.Portability.Verification;

namespace Nostos.Backend.Services.Portability;

/// <summary>
/// Authoritative portable-state verifier and the one orchestration point every
/// verification stage shares. The prepared-import pass reads the staged relational
/// payload, manifest and every media item, re-hashes all of them, and reuses
/// <see cref="PortableArchiveValidation"/> for relational/relationship validation.
/// The candidate pass compares the materialized candidate database and media root
/// against that verified expected state using read-only queries only; host
/// operational state is neither required to match the archive nor mutated.
/// </summary>
/// <remarks>
/// <para>
/// The comparison domains live in <c>Verification/</c>: books/works/editions,
/// progress, collections and membership, notes/topics, writings, the reading list,
/// acquisitions/settings/import links, media, counts and the coverage catalog. This
/// class owns the stage contract and the report each stage returns; it holds no
/// comparison rules of its own.
/// </para>
/// <para>
/// Every candidate comparison is declared in an executable comparison table, and
/// <see cref="CandidateComparisonSpecs"/> projects those same tables so the
/// coverage tests enforce property coverage without a parallel hand-written list.
/// </para>
/// </remarks>
public sealed class PortableLibraryVerifier : IPortableLibraryVerifier
{
    /// <summary>
    /// Maximum number of specific failure details retained in a report. Additional
    /// mismatches are counted in <see cref="PortableLibraryVerificationReport.FailureCount"/>
    /// but not retained, so a heavily divergent candidate cannot exhaust memory.
    /// </summary>
    internal const int MaxRetainedFailures = PortableVerificationPrimitives.MaxRetainedFailures;

    internal static readonly IReadOnlyList<string> CandidateVerifiedKinds =
    [
        nameof(PortableWork),
        nameof(PortableBook),
        nameof(PortableBookMetadata),
        nameof(PortableReadingProgress),
        nameof(PortableCollection),
        nameof(PortableBookCollection),
        nameof(PortableNote),
        nameof(PortableTopic),
        nameof(PortableNoteTopic),
        nameof(PortableWriting),
        nameof(PortableWritingNote),
        nameof(PortableBookAcquisition),
        nameof(PortableAssistantSettings),
        nameof(PortableNoteImportBookLink),
        nameof(PortableReadingListEntry),
        nameof(PortableArchiveMediaEntry),
    ];

    /// <summary>
    /// Kinds verified by <see cref="VerifyCandidateDatabaseAsync"/>: every relational
    /// portable kind. The media entry kind lives in asset storage, not in the
    /// relational candidate, and is verified by <see cref="VerifyMediaAsync"/>.
    /// </summary>
    internal static readonly IReadOnlyList<string> CandidateDatabaseVerifiedKinds =
        CandidateVerifiedKinds
            .Where(kind => !string.Equals(
                kind,
                nameof(PortableArchiveMediaEntry),
                StringComparison.Ordinal))
            .ToArray();

    /// <summary>
    /// A new portable archive record or property makes the archive-coverage test
    /// fail until it is registered in <see cref="PortableComparisonCatalog"/> (or
    /// given a reasoned exclusion), and registering it requires an executable
    /// comparison delegate.
    /// </summary>
    internal static IReadOnlyDictionary<string, RecordComparisonSpec> CandidateComparisonSpecs =>
        PortableComparisonCatalog.Specs;

    public async Task<PortablePreparedImportVerification> VerifyPreparedImportAsync(
        IPortableImportStaging staging,
        IPreparedPortableImport prepared,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(staging);
        ArgumentNullException.ThrowIfNull(prepared);

        return await PortablePreparedImportVerifier
            .VerifyAsync(staging, prepared, ct)
            .ConfigureAwait(false);
    }

    public async Task<PortableLibraryVerificationReport> VerifyCandidateDatabaseAsync(
        NostosDbContext candidateDatabase,
        IPreparedPortableImport prepared,
        PortablePreparedImportVerification expected,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidateDatabase);
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(expected);

        if (ValidateExpectedState(prepared, expected) is { } mismatch)
        {
            return mismatch;
        }

        var comparison = await PortableCandidateDatabaseVerifier.CompareAsync(
            candidateDatabase,
            expected.Data!,
            expected.Descriptors,
            ct).ConfigureAwait(false);
        PortableVerificationCounts.CompareCandidateCounts(
            expected.Metadata.Counts,
            comparison.Counts,
            mediaFiles: null,
            comparison.Failures);

        return new PortableLibraryVerificationReport(
            comparison.Failures.TotalCount == 0,
            comparison.Failures,
            CandidateDatabaseVerifiedKinds,
            comparison.RowsVerified,
            MediaFilesVerified: 0,
            MediaBytesVerified: 0,
            comparison.Failures.TotalCount);
    }

    public async Task<PortableLibraryVerificationReport> VerifyCandidateAsync(
        NostosDbContext candidateDatabase,
        string candidateMediaRoot,
        IPreparedPortableImport prepared,
        PortablePreparedImportVerification expected,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidateDatabase);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateMediaRoot);
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(expected);

        if (ValidateExpectedState(prepared, expected) is { } mismatch)
        {
            return mismatch;
        }

        var comparison = await PortableCandidateDatabaseVerifier.CompareAsync(
            candidateDatabase,
            expected.Data!,
            expected.Descriptors,
            ct).ConfigureAwait(false);
        var (mediaFiles, mediaBytes) = await PortableMediaVerifier
            .VerifyCandidateMediaRootAsync(
                candidateMediaRoot,
                expected.Descriptors,
                comparison.Failures,
                ct)
            .ConfigureAwait(false);
        PortableVerificationCounts.CompareCandidateCounts(
            expected.Metadata.Counts,
            comparison.Counts,
            mediaFiles,
            comparison.Failures);

        return new PortableLibraryVerificationReport(
            comparison.Failures.TotalCount == 0,
            comparison.Failures,
            CandidateVerifiedKinds,
            comparison.RowsVerified,
            mediaFiles,
            mediaBytes,
            comparison.Failures.TotalCount);
    }

    /// <summary>
    /// Verifies only the relational portable state of a database against an
    /// extracted recovery payload. Recovery restore uses this after the swap:
    /// the previous library's media is verified separately against the retained
    /// recovery manifest, which describes every retained file (including
    /// derived thumbnails the portable media inventory never carries).
    /// </summary>
    public async Task<PortableLibraryVerificationReport> VerifyDatabaseAgainstExpectedAsync(
        NostosDbContext database,
        PortableRecoveryPayload expected,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(expected);

        var comparison = await PortableCandidateDatabaseVerifier.CompareAsync(
            database,
            expected.Data,
            expected.PrimaryMedia,
            ct).ConfigureAwait(false);
        PortableVerificationCounts.CompareCandidateCounts(
            expected.Counts,
            comparison.Counts,
            expected.PrimaryMedia.Count,
            comparison.Failures);

        return new PortableLibraryVerificationReport(
            comparison.Failures.TotalCount == 0,
            comparison.Failures,
            CandidateDatabaseVerifiedKinds,
            comparison.RowsVerified,
            MediaFilesVerified: 0,
            MediaBytesVerified: 0,
            comparison.Failures.TotalCount);
    }

    public Task<PortableLibraryVerificationReport> VerifyMediaAsync(
        IBookAssetStorage assets,
        IReadOnlyList<PortableArchiveMediaEntry> expected,
        CancellationToken ct = default) =>
        PortableMediaVerifier.VerifyStorageAsync(assets, tracks: null, expected, ct);

    /// <summary>
    /// Verifies stored media including the tracks of multi-track audiobooks.
    /// Without <paramref name="tracks"/> an expected track is reported as a
    /// failure, never skipped.
    /// </summary>
    public Task<PortableLibraryVerificationReport> VerifyMediaAsync(
        IBookAssetStorage assets,
        IBookTrackStorage? tracks,
        IReadOnlyList<PortableArchiveMediaEntry> expected,
        CancellationToken ct = default) =>
        PortableMediaVerifier.VerifyStorageAsync(assets, tracks, expected, ct);

    private static PortableLibraryVerificationReport? ValidateExpectedState(
        IPreparedPortableImport prepared,
        PortablePreparedImportVerification expected)
    {
        if (!expected.Passed || expected.Data is null)
        {
            throw new PortableLibraryVerificationException(
                PortableLibraryVerificationErrorCodes.ExpectedStateInvalid,
                "Candidate verification requires a prepared import that passed full verification.");
        }

        if (expected.StagingId != prepared.Metadata.StagingId
            || expected.DataBytes != prepared.Metadata.DataBytes
            || !PortableArchiveValidation.FixedHashEquals(
                expected.DataSha256,
                prepared.Metadata.DataSha256))
        {
            var mismatch = new FailureList();
            mismatch.Add(PortableVerificationPrimitives.Failure(
                PortableLibraryVerificationErrorCodes.ExpectedStateMismatch,
                "expected",
                prepared.Metadata.StagingId.Value.ToString("D"),
                nameof(PreparedPortableImportMetadata.DataSha256),
                "The verified expected state does not belong to the supplied prepared import."));
            return new PortableLibraryVerificationReport(
                Passed: false,
                mismatch,
                VerifiedKinds: [],
                PortableRowsVerified: 0,
                MediaFilesVerified: 0,
                MediaBytesVerified: 0,
                FailureCount: mismatch.TotalCount);
        }

        return null;
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
    internal static bool CanonicalValuesEqual<T>(T actual, T expected) =>
        PortableVerificationPrimitives.CanonicalValuesEqual(actual, expected);

    internal static DateTime Canonicalize(DateTime value) =>
        PortableVerificationPrimitives.Canonicalize(value);

    internal static DateTime? Canonicalize(DateTime? value) =>
        PortableVerificationPrimitives.Canonicalize(value);

    internal static DateTimeOffset Canonicalize(DateTimeOffset value) =>
        PortableVerificationPrimitives.Canonicalize(value);

    internal static DateTimeOffset? Canonicalize(DateTimeOffset? value) =>
        PortableVerificationPrimitives.Canonicalize(value);
}

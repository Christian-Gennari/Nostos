using Nostos.Backend.Data;
using static Nostos.Backend.Services.Portability.Verification.PortableVerificationPrimitives;

namespace Nostos.Backend.Services.Portability.Verification;

/// <summary>
/// The post-import verification the legacy compatibility import runs inside its
/// transaction, before it commits and before derived text is scheduled.
/// </summary>
/// <remarks>
/// <para>
/// It deliberately owns no comparison rules: the relational comparison is the
/// same <see cref="PortableCandidateDatabaseVerifier"/> tables the activation
/// candidate pass uses, the count reconciliation is
/// <see cref="PortableVerificationCounts"/>, and stored media is
/// <see cref="PortableMediaVerifier"/>. That is what stops the import's private
/// check from drifting away from the authoritative verifier: there is only one
/// path, and every portable field an activation is admitted on is also
/// verified after a compatibility restore.
/// </para>
/// <para>
/// A failure keeps the import's pre-existing observable contract: an
/// <see cref="PortableArchiveException"/> with code <c>integrity_failed</c>,
/// which rolls the transaction back and removes any media already written.
/// </para>
/// </remarks>
internal static class PortableImportIntegrityVerifier
{
    internal const string FailureCode = "integrity_failed";

    internal static async Task VerifyAppliedAsync(
        NostosDbContext database,
        IBookAssetStorage assets,
        IBookTrackStorage? tracks,
        PortableLibraryData data,
        PortableArchiveCounts expectedCounts,
        IReadOnlyList<PortableArchiveMediaEntry> media,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(expectedCounts);
        ArgumentNullException.ThrowIfNull(media);

        var comparison = await PortableCandidateDatabaseVerifier.CompareAsync(
            database,
            data,
            media,
            ct).ConfigureAwait(false);
        var failures = comparison.Failures;

        PortableVerificationCounts.CompareManifestCounts(expectedCounts, data, failures);
        PortableVerificationCounts.CompareCandidateCounts(
            PortableLibraryCounts.ComputeCounts(data, media.Count),
            comparison.Counts,
            media.Count,
            failures);
        await PortableMediaVerifier.VerifyStorageIntoAsync(
            assets,
            tracks,
            media,
            failures,
            ct).ConfigureAwait(false);

        if (failures.TotalCount > 0)
        {
            throw new PortableArchiveException(
                FailureCode,
                Describe(failures[0]));
        }
    }

    /// <summary>
    /// A compact, content-free summary of the first mismatch: the code, entity
    /// and field identify the failing dimension without echoing user data,
    /// absolute paths or archive entry names.
    /// </summary>
    private static string Describe(PortableLibraryVerificationFailure failure) =>
        $"Imported portable state does not match the archive ({failure.Code} {failure.Entity}"
        + (failure.Field is { Length: > 0 } field ? $" {field}" : string.Empty)
        + ").";
}

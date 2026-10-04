using Nostos.Backend.Data;

namespace Nostos.Backend.Services.Portability;

/// <summary>
/// Stable, provider-neutral failure codes reported by <see cref="IPortableLibraryVerifier"/>.
/// Codes are part of the activation admission vocabulary and must stay stable.
/// </summary>
public static class PortableLibraryVerificationErrorCodes
{
    public const string StagingUnavailable = "portable_verify_staging_unavailable";
    public const string ImportNotIntegrityVerified = "portable_verify_import_not_verified";
    public const string InventoryMismatch = "portable_verify_inventory_mismatch";
    public const string DataMissing = "portable_verify_data_missing";
    public const string DataLengthMismatch = "portable_verify_data_length";
    public const string DataHashMismatch = "portable_verify_data_hash";
    public const string DataMalformed = "portable_verify_data_malformed";
    public const string ManifestMalformed = "portable_verify_manifest_malformed";
    public const string ManifestDisagreement = "portable_verify_manifest_disagreement";
    public const string CountMismatch = "portable_verify_count_mismatch";
    public const string CountUnverified = "portable_verify_count_unverified";
    public const string RelationalInvalid = "portable_verify_relational_invalid";
    public const string MediaMissing = "portable_verify_media_missing";
    public const string MediaLengthMismatch = "portable_verify_media_length";
    public const string MediaHashMismatch = "portable_verify_media_hash";
    public const string MediaTooLarge = "portable_verify_media_too_large";
    public const string MissingEntity = "portable_verify_entity_missing";
    public const string UnexpectedEntity = "portable_verify_entity_unexpected";
    public const string FieldMismatch = "portable_verify_field_mismatch";
    public const string RelationshipMismatch = "portable_verify_relationship_mismatch";
    public const string SingletonMismatch = "portable_verify_singleton_mismatch";
    public const string MediaUnexpectedFile = "portable_verify_media_unexpected";
    public const string ExpectedStateInvalid = "portable_verify_expected_invalid";
    public const string ExpectedStateMismatch = "portable_verify_expected_mismatch";
}

/// <summary>
/// Raised when the verifier is asked to verify against an expected state that is not a
/// successfully verified prepared import. Fails closed instead of reporting a pass.
/// </summary>
public sealed class PortableLibraryVerificationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// One typed, specific mismatch. <see cref="Detail"/> never contains user content,
/// absolute paths, archive entry names, or provider exceptions.
/// </summary>
public sealed record PortableLibraryVerificationFailure(
    string Code,
    string Entity,
    string? EntityId,
    string? Field,
    string Detail);

/// <summary>
/// Typed verification outcome. A report passes only when every recognized portable
/// kind and field was compared and no mismatch was found; any unverifiable
/// dimension is itself a failure. <see cref="Failures"/> retains a bounded number of
/// specific details (<see cref="FailureCount"/> is the total detected), so a
/// heavily divergent candidate cannot exhaust memory with failure records.
/// </summary>
public sealed record PortableLibraryVerificationReport(
    bool Passed,
    IReadOnlyList<PortableLibraryVerificationFailure> Failures,
    IReadOnlyList<string> VerifiedKinds,
    long PortableRowsVerified,
    long MediaFilesVerified,
    long MediaBytesVerified,
    long FailureCount);

/// <summary>
/// Successful (or failed) verification of a prepared import, carrying an opaque
/// expected portable state that later verification stages consume. The expected
/// state is only usable when <see cref="Passed"/> is true; verification is the
/// single authority that decides this.
/// </summary>
public sealed class PortablePreparedImportVerification
{
    internal PortablePreparedImportVerification(
        bool passed,
        IReadOnlyList<PortableLibraryVerificationFailure> failures,
        PreparedPortableImportMetadata metadata,
        IReadOnlyList<PortablePreparedMedia> stagedMedia,
        IReadOnlyList<PortableArchiveMediaEntry> descriptors,
        PortableLibraryData? data)
    {
        Passed = passed;
        Failures = failures;
        Metadata = metadata;
        StagedMedia = stagedMedia;
        Descriptors = descriptors;
        Data = data;
    }

    public bool Passed { get; }

    public IReadOnlyList<PortableLibraryVerificationFailure> Failures { get; }

    public PreparedPortableImportMetadata Metadata { get; }

    public MigrationArchiveCounts Counts => Metadata.Counts;

    public IReadOnlyList<PortableArchiveMediaEntry> Media => Descriptors;

    /// <summary>
    /// The committed staged descriptor this handle verified: staging identifier,
    /// relational payload length and SHA-256. Candidate verification refuses a
    /// handle that does not match the supplied prepared import.
    /// </summary>
    internal PortableStagingId StagingId => Metadata.StagingId;

    internal long DataBytes => Metadata.DataBytes;

    internal string DataSha256 => Metadata.DataSha256;

    internal IReadOnlyList<PortablePreparedMedia> StagedMedia { get; }

    internal IReadOnlyList<PortableArchiveMediaEntry> Descriptors { get; }

    internal PortableLibraryData? Data { get; }
}

/// <summary>
/// Authoritative portable-state verification used before the activation point of no
/// return. <see cref="VerifyPreparedImportAsync"/> re-hashes every staged byte (the
/// staging rebuild contract only re-verifies media lengths, so this re-hash is
/// mandatory). <see cref="VerifyCandidateAsync"/> compares the fully materialized
/// candidate database and media root against the verified prepared import using
/// read-only queries; it never mutates the candidate or any host operational table.
/// The supplied expected state must be bound to the same committed prepared
/// descriptor (staging identifier and relational data hash) as <c>prepared</c>.
/// </summary>
public interface IPortableLibraryVerifier
{
    Task<PortablePreparedImportVerification> VerifyPreparedImportAsync(
        IPortableImportStaging staging,
        IPreparedPortableImport prepared,
        CancellationToken ct = default);

    Task<PortableLibraryVerificationReport> VerifyCandidateAsync(
        NostosDbContext candidateDatabase,
        string candidateMediaRoot,
        IPreparedPortableImport prepared,
        PortablePreparedImportVerification expected,
        CancellationToken ct = default);

    Task<PortableLibraryVerificationReport> VerifyMediaAsync(
        IBookAssetStorage assets,
        IReadOnlyList<PortableArchiveMediaEntry> expected,
        CancellationToken ct = default);
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nostos.Backend.Services.Portability;

namespace Nostos.Backend.Services.Portability.Activation;

public enum SelfHostedActivationPhase
{
    CandidatePrepared = 0,
    ExclusiveEntered = 1,
    DatabaseCheckpointed = 2,
    CutoverPrepared = 3,
    PreviousMediaRetained = 4,
    PreviousDatabaseRetained = 5,
    CandidateMediaActivated = 6,
    CandidateDatabaseActivated = 7,
    PostActivationVerified = 8,
    Committed = 9,
    RollingBack = 10,
    RolledBack = 11,
}

public enum SelfHostedRecoveryAction
{
    Nothing,
    RollBackOriginal,
    RollForwardCandidate,
    FailClosed,
}

/// <summary>
/// Filesystem-side state, never stored solely in the database being switched.
/// Locations derive from configuration and generated IDs in the later cutover engine.
/// A phase is written durably before the next rename: recovery must tolerate a rename
/// completed before its following phase write. Committed is the only durable commit.
/// Unknown fields survive serialization; unknown versions/phases fail closed.
/// </summary>
public sealed record SelfHostedActivationJournal(
    [property: JsonRequired] Guid JobId,
    [property: JsonRequired] Guid OperationId,
    [property: JsonRequired] SelfHostedActivationPhase Phase,
    [property: JsonRequired] string DestinationRevision,
    [property: JsonRequired] bool RetainPreviousLibrary,
    [property: JsonRequired] DateTimeOffset UpdatedAtUtc,
    [property: JsonRequired] int JournalVersion = 1)
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; init; }
}

public sealed record RecoveryMediaDescriptor(Guid BookId, string Kind, string Extension, long Bytes, string Sha256);

/// <summary>
/// Describes a matched previous DB/media generation. A populated replacement's
/// expiry is exactly the contract retention period. Restore consumes portable state
/// from this database; it must not resurrect this copy's host operational rows.
/// <para>
/// <see cref="Status"/> is <see cref="MigrationRecoveryStatus.Creating"/> while the
/// retention renames are still being made and <see cref="MigrationRecoveryStatus.Available"/>
/// once both components are retained and the retention reservation is durable.
/// <see cref="DatabaseRetained"/>/<see cref="MediaRetained"/> record exactly which
/// rename has completed, so a process crash is describable; the activation journal
/// remains the phase authority that decides rollback or roll-forward.
/// <see cref="RetentionReservationId"/> is the claimed, non-expiring capacity
/// reservation that is released only after the retained material is deleted; each
/// replacement retains its own seven-day copy, several can coexist, and each keeps
/// its own claimed reservation until its own cleanup.
/// </para>
/// <para>
/// Media evidence: every <see cref="RecoveryMediaDescriptor.Sha256"/> describes the
/// exact bytes present at the final under-maintenance verification immediately
/// before retention. A file is re-hashed under the exclusive lease when it is new,
/// its length or full-precision last-write time changed, or its last write falls at
/// or after the capture start minus the timestamp-granularity safety window (see
/// <c>SelfHostedMigrationRecoveryService.MediaHashSafetyWindow</c>);
/// <see cref="MediaRehashedCount"/> reports how many were re-hashed. Only a file
/// unchanged in path, length and last-write time and last written strictly before
/// that window keeps its pre-maintenance hash. Deliberate back-dating of timestamps
/// by a local actor is outside the threat model. Restore must re-hash every
/// retained file and fail closed on any mismatch.
/// </para>
/// <para>
/// <see cref="TransferTopUpSettled"/>/<see cref="TransferTopUpReservationId"/>/
/// <see cref="TransferTopUpTargetBytes"/> are the durable absolute target for the
/// one-time offset of the job's unmaterialized transfer claim, recorded before the
/// capacity row is mutated so retries and crashes cannot apply it twice.
/// </para>
/// </summary>
public sealed record SelfHostedRecoveryManifest(
    [property: JsonRequired] Guid JobId,
    [property: JsonRequired] Guid OperationId,
    [property: JsonRequired] DateTimeOffset CreatedAtUtc,
    [property: JsonRequired] DateTimeOffset ExpiresAtUtc,
    [property: JsonRequired] MigrationRecoveryStatus Status,
    [property: JsonRequired] string PreviousDestinationRevision,
    [property: JsonRequired] MigrationExistingCounts Counts,
    [property: JsonRequired] long DatabaseBytes,
    [property: JsonRequired] long MediaBytes,
    [property: JsonRequired] string DatabaseSha256,
    [property: JsonRequired] IReadOnlyList<RecoveryMediaDescriptor> Media,
    [property: JsonRequired] int ManifestVersion = 1,
    [property: JsonRequired] string DatabaseSchemaVersion = "",
    [property: JsonRequired] int DatabaseMigrationCount = 0,
    [property: JsonRequired] bool DatabaseRetained = false,
    [property: JsonRequired] bool MediaRetained = false,
    [property: JsonRequired] Guid? RetentionReservationId = null,
    [property: JsonRequired] int MediaRehashedCount = 0,
    [property: JsonRequired] bool TransferTopUpSettled = false,
    [property: JsonRequired] Guid? TransferTopUpReservationId = null,
    [property: JsonRequired] long TransferTopUpTargetBytes = 0)
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; init; }

    public static DateTimeOffset Expiry(DateTimeOffset createdAtUtc) =>
        createdAtUtc.AddDays(MigrationContractLimits.RecoveryRetentionDays);

    /// <summary>Physical bytes retained by this snapshot on both volumes.</summary>
    public long TotalBytes => DatabaseBytes + MediaBytes;
}

/// <summary>Pure protocol decisions; this class never reads or mutates the live library.</summary>
public static class SelfHostedActivationState
{
    /// <summary>
    /// Total even for absent, malformed or future journal states. File existence may
    /// validate the selected action but must never decide which generation wins.
    /// </summary>
    public static SelfHostedRecoveryAction RecoveryAction(SelfHostedActivationJournal? journal) =>
        journal is null ? SelfHostedRecoveryAction.Nothing
        : journal.JournalVersion != 1 || journal.JobId == Guid.Empty
            || journal.OperationId == Guid.Empty || string.IsNullOrWhiteSpace(journal.DestinationRevision)
            ? SelfHostedRecoveryAction.FailClosed
        : RecoveryAction(journal.Phase);

    public static SelfHostedRecoveryAction RecoveryAction(SelfHostedActivationPhase phase) => phase switch
    {
        SelfHostedActivationPhase.CandidatePrepared => SelfHostedRecoveryAction.Nothing,
        SelfHostedActivationPhase.ExclusiveEntered => SelfHostedRecoveryAction.Nothing,
        SelfHostedActivationPhase.DatabaseCheckpointed => SelfHostedRecoveryAction.Nothing,
        SelfHostedActivationPhase.CutoverPrepared => SelfHostedRecoveryAction.RollBackOriginal,
        SelfHostedActivationPhase.PreviousMediaRetained => SelfHostedRecoveryAction.RollBackOriginal,
        SelfHostedActivationPhase.PreviousDatabaseRetained => SelfHostedRecoveryAction.RollBackOriginal,
        SelfHostedActivationPhase.CandidateMediaActivated => SelfHostedRecoveryAction.RollBackOriginal,
        SelfHostedActivationPhase.CandidateDatabaseActivated => SelfHostedRecoveryAction.RollBackOriginal,
        SelfHostedActivationPhase.PostActivationVerified => SelfHostedRecoveryAction.RollBackOriginal,
        SelfHostedActivationPhase.Committed => SelfHostedRecoveryAction.RollForwardCandidate,
        SelfHostedActivationPhase.RollingBack => SelfHostedRecoveryAction.RollBackOriginal,
        SelfHostedActivationPhase.RolledBack => SelfHostedRecoveryAction.Nothing,
        _ => SelfHostedRecoveryAction.FailClosed,
    };

    public static bool CanTransition(SelfHostedActivationPhase from, SelfHostedActivationPhase to) =>
        (from, to) switch
        {
            (SelfHostedActivationPhase.CandidatePrepared, SelfHostedActivationPhase.ExclusiveEntered) => true,
            (SelfHostedActivationPhase.ExclusiveEntered, SelfHostedActivationPhase.DatabaseCheckpointed) => true,
            (SelfHostedActivationPhase.DatabaseCheckpointed, SelfHostedActivationPhase.CutoverPrepared) => true,
            (SelfHostedActivationPhase.CutoverPrepared, SelfHostedActivationPhase.PreviousMediaRetained) => true,
            (SelfHostedActivationPhase.PreviousMediaRetained, SelfHostedActivationPhase.PreviousDatabaseRetained) => true,
            (SelfHostedActivationPhase.PreviousDatabaseRetained, SelfHostedActivationPhase.CandidateMediaActivated) => true,
            (SelfHostedActivationPhase.CandidateMediaActivated, SelfHostedActivationPhase.CandidateDatabaseActivated) => true,
            (SelfHostedActivationPhase.CandidateDatabaseActivated, SelfHostedActivationPhase.PostActivationVerified) => true,
            (SelfHostedActivationPhase.PostActivationVerified, SelfHostedActivationPhase.Committed) => true,
            (SelfHostedActivationPhase.RollingBack, SelfHostedActivationPhase.RolledBack) => true,
            (>= SelfHostedActivationPhase.CandidatePrepared and <= SelfHostedActivationPhase.PostActivationVerified,
                SelfHostedActivationPhase.RollingBack) => true,
            _ => false,
        };

    public static void ValidateTransition(SelfHostedActivationPhase from, SelfHostedActivationPhase to)
    {
        if (!CanTransition(from, to))
            throw new InvalidOperationException("Illegal activation journal transition.");
    }

    /// <summary>
    /// Adds filesystem completion conditions to the existing authoritative job table.
    /// Cancellation stops at Activating, even before the durable filesystem commit.
    /// A rollback failure must keep traffic closed, not finalize a failed job.
    /// </summary>
    public static void ValidateJobOutcome(MigrationDirection direction, MigrationJobState from,
        MigrationJobState to, SelfHostedActivationPhase phase)
    {
        MigrationJobTransitions.ValidateTransition(direction, from, to);
        if (from != MigrationJobState.Activating) return;
        var safeFailure = phase is SelfHostedActivationPhase.CandidatePrepared
            or SelfHostedActivationPhase.ExclusiveEntered or SelfHostedActivationPhase.DatabaseCheckpointed
            or SelfHostedActivationPhase.RolledBack;
        if ((to == MigrationJobState.Completed && phase != SelfHostedActivationPhase.Committed)
            || (to == MigrationJobState.Failed && !safeFailure))
            throw new InvalidOperationException("The filesystem outcome is not durable and safe.");
    }

    public static bool CanCancel(MigrationDirection direction, MigrationJobState state) =>
        Enum.IsDefined(direction) && MigrationJobTransitions.CanTransition(direction, state, MigrationJobState.Cancelled);
}

/// <summary>
/// Versioned checksummed envelope. The digest covers the exact UTF-8 payload JSON,
/// including unknown fields. This detects corruption, not malicious tampering.
/// Slice 3 must flush a temporary sibling then rename this document into place.
/// </summary>
public static class SelfHostedActivationDocument
{
    public sealed record Envelope(int Version, string PayloadJson, string Sha256);

    public static string Encode<T>(T document)
    {
        var payload = JsonSerializer.Serialize(document);
        return JsonSerializer.Serialize(new Envelope(1, payload, Digest(payload)));
    }

    public static T Decode<T>(string json)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(json);
            if (envelope is null || envelope.Version != 1 || envelope.PayloadJson is null
                || !string.Equals(envelope.Sha256, Digest(envelope.PayloadJson), StringComparison.Ordinal))
                throw Corrupt();
            var result = JsonSerializer.Deserialize<T>(envelope.PayloadJson) ?? throw Corrupt();
            if (result is SelfHostedRecoveryManifest manifest && !IsValid(manifest)) throw Corrupt();
            return result;
        }
        catch (JsonException) { throw Corrupt(); }
    }

    public static SelfHostedRecoveryAction RecoveryAction(string? journalJson)
    {
        if (journalJson is null) return SelfHostedRecoveryAction.Nothing;
        try { return SelfHostedActivationState.RecoveryAction(Decode<SelfHostedActivationJournal>(journalJson)); }
        catch (MigrationActivationException) { return SelfHostedRecoveryAction.FailClosed; }
    }

    private static string Digest(string payload) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

    private static bool IsValid(SelfHostedRecoveryManifest manifest) =>
        manifest.ManifestVersion == 1 && manifest.JobId != Guid.Empty && manifest.OperationId != Guid.Empty
        && Enum.IsDefined(manifest.Status) && !string.IsNullOrWhiteSpace(manifest.PreviousDestinationRevision)
        && manifest.ExpiresAtUtc - manifest.CreatedAtUtc == TimeSpan.FromDays(MigrationContractLimits.RecoveryRetentionDays)
        && manifest.Counts is not null && manifest.DatabaseBytes >= 0 && manifest.MediaBytes >= 0
        && IsDigest(manifest.DatabaseSha256) && manifest.Media is not null
        && manifest.DatabaseSchemaVersion is not null && manifest.DatabaseMigrationCount >= 0
        && manifest.RetentionReservationId != Guid.Empty
        && manifest.MediaRehashedCount >= 0
        && manifest.TransferTopUpReservationId != Guid.Empty
        && manifest.TransferTopUpTargetBytes >= 0
        && manifest.Media.All(m => m is not null && m.BookId != Guid.Empty && m.Bytes >= 0
            && IsDigest(m.Sha256));

    private static bool IsDigest(string? value) => value is { Length: 64 }
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static MigrationActivationException Corrupt() => new(
        MigrationActivationErrorCodes.RecoveryCorrupt, "The activation document is unsupported or corrupt.");
}

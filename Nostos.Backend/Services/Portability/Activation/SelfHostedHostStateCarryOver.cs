using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Services.Portability.Activation;

internal enum HostStateCarryOverKind
{
    /// <summary>Rows are copied from the live database into the candidate at finalization.</summary>
    Carry,

    /// <summary>Rows are deliberately not copied; the rationale records why.</summary>
    Clear,
}

/// <summary>
/// One explicit portability decision for an entity the completeness inventory
/// classifies as excluded host operational state (not portable library content).
/// Activation replaces portable state only, so every excluded entity must have a
/// carry-over decision; <c>SelfHostedHostStateCarryOverTests</c> fails when a new
/// excluded entity appears without one.
/// </summary>
internal sealed record HostStateCarryOverDecision(
    Type EntityType,
    HostStateCarryOverKind Kind,
    string Rationale)
{
    internal string EntityName => EntityType.Name;
}

/// <summary>
/// The candidate database's host-state decision list, derived from the portability
/// completeness inventory. The candidate is built from a fresh bootstrap plus the
/// prepared portable payload, so unlike a whole-file clone this registry is what
/// decides which installation-local operational state survives activation.
/// <c>Carry</c> rows are copied from a live snapshot under the exclusive
/// maintenance lease; <c>Clear</c> rows are deliberately dropped.
/// </summary>
internal static class SelfHostedHostStateCarryOver
{
    /// <summary>
    /// Every entity <c>PortableCompletenessInventoryTests</c> explicitly excludes
    /// must appear exactly once. Adding an excluded entity to the EF model
    /// without a decision here fails the inventory-tied test.
    /// </summary>
    internal static IReadOnlyList<HostStateCarryOverDecision> Decisions { get; } =
    [
        new(
            typeof(NoteImportBatch),
            HostStateCarryOverKind.Clear,
            "Import undo batches describe notes of the replaced library generation; a whole-library replacement must not leave stale batches that claim ownership of the new generation."),
        new(
            typeof(NoteImportBatchNote),
            HostStateCarryOverKind.Clear,
            "Undo batch-to-note links carry the old generation's note identity. A matching GUID is not provenance into the imported library, so all links are cleared with their batches."),
        new(
            typeof(AiProviderSettingsModel),
            HostStateCarryOverKind.Carry,
            "Host credentials and provider configuration are deployment-local, contain no foreign key, and must never be imported from a portable archive."),
        new(
            typeof(ProviderPreferenceModel),
            HostStateCarryOverKind.Carry,
            "Provider enablement choices are deployment-local configuration with no foreign key; replacing the library must not reset which free sources the user turned on or off."),
        new(
            typeof(BackupRecord),
            HostStateCarryOverKind.Carry,
            "Local backup history references deployment-specific archives, has no foreign key, and intentionally may describe older library generations."),
        new(
            typeof(LibraryCommandReceipt),
            HostStateCarryOverKind.Carry,
            "Pure idempotency key plus stored response; no foreign key or id column references portable state. Carrying it prevents a pre-activation retry from replaying a library mutation against the imported generation."),
        new(
            typeof(NoteCommandReceipt),
            HostStateCarryOverKind.Carry,
            "Pure idempotency key plus stored result; no foreign key or id column references portable state. Carrying it prevents a pre-activation note command from replaying against the imported generation."),
        new(
            typeof(LibraryState),
            HostStateCarryOverKind.Carry,
            "Singleton revision row is host infrastructure with no foreign key; finalization advances the revision from the authoritative live value so the candidate represents the newly activated portable state."),
        new(
            typeof(MigrationJobRecord),
            HostStateCarryOverKind.Carry,
            "Migration job lifecycle rows reference only migration operational records (and opaque staging identifiers), and include the job performing this activation. Finalized from the live snapshot after the job transition."),
        new(
            typeof(MigrationSessionRecord),
            HostStateCarryOverKind.Carry,
            "Transfer session bookkeeping; its only foreign key targets MigrationJobRecord, which is also carried."),
        new(
            typeof(MigrationChunkReceiptRecord),
            HostStateCarryOverKind.Carry,
            "Accepted transfer receipts; the foreign key targets MigrationSessionRecord, which is also carried."),
        new(
            typeof(MigrationExportArtifactRecord),
            HostStateCarryOverKind.Carry,
            "Export artifact retention metadata; the foreign key targets MigrationJobRecord, and storage keys refer to host export files outside the library."),
        new(
            typeof(MigrationStorageReservationRecord),
            HostStateCarryOverKind.Carry,
            "Storage admission and reservation accounting; no foreign key and no portability semantics, so it must survive the replacement unchanged."),
    ];

    internal static IReadOnlyList<HostStateCarryOverDecision> CarryDecisions { get; } =
        Decisions.Where(decision => decision.Kind == HostStateCarryOverKind.Carry).ToArray();
}

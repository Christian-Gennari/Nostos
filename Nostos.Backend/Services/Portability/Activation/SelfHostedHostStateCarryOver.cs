using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Services.Portability.Activation;

internal enum HostStateCarryOverKind
{
    /// <summary>Rows are copied from the live database into the candidate.</summary>
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
/// The candidate database's host-state copy list, derived from the portability
/// completeness inventory. The candidate is built from a fresh bootstrap plus the
/// prepared portable payload, so unlike a whole-file clone this registry is what
/// keeps installation-local operational state (settings, credentials, job tables,
/// import undo history) from being lost at activation.
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
            HostStateCarryOverKind.Carry,
            "Import-undo batch history is host operational state; the batch row itself carries no portable reference."),
        new(
            typeof(NoteImportBatchNote),
            HostStateCarryOverKind.Carry,
            "Only batch-to-note links whose note survives the portable replacement are copied; a link to a replaced note cannot be restored without a foreign-key violation."),
        new(
            typeof(AiProviderSettingsModel),
            HostStateCarryOverKind.Carry,
            "Host credentials and provider configuration are deployment-local and must never be imported from a portable archive."),
        new(
            typeof(BackupRecord),
            HostStateCarryOverKind.Carry,
            "Local backup history references deployment-specific archives and remains discoverable after activation."),
        new(
            typeof(LibraryCommandReceipt),
            HostStateCarryOverKind.Carry,
            "Command idempotency receipts must survive activation so a retried command cannot replay a portable mutation."),
        new(
            typeof(NoteCommandReceipt),
            HostStateCarryOverKind.Carry,
            "Note-command idempotency receipts must survive activation so a retried note command cannot replay against the imported library."),
        new(
            typeof(LibraryState),
            HostStateCarryOverKind.Carry,
            "The singleton library revision row is host infrastructure; the candidate advances the revision to represent the newly activated portable state."),
        new(
            typeof(MigrationJobRecord),
            HostStateCarryOverKind.Carry,
            "Migration job lifecycle rows are host control state and include the job performing this activation."),
        new(
            typeof(MigrationSessionRecord),
            HostStateCarryOverKind.Carry,
            "Resumable transfer session bookkeeping is host control state."),
        new(
            typeof(MigrationChunkReceiptRecord),
            HostStateCarryOverKind.Carry,
            "Accepted transfer chunk receipts are host control state."),
        new(
            typeof(MigrationExportArtifactRecord),
            HostStateCarryOverKind.Carry,
            "Export artifact retention metadata is host control state."),
        new(
            typeof(MigrationStorageReservationRecord),
            HostStateCarryOverKind.Carry,
            "Storage admission and reservation accounting is host control state."),
    ];

    internal static IReadOnlyList<HostStateCarryOverDecision> CarryDecisions { get; } =
        Decisions.Where(decision => decision.Kind == HostStateCarryOverKind.Carry).ToArray();
}

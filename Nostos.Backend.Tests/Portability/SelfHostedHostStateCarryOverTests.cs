using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Services.Portability.Activation;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

public sealed class SelfHostedHostStateCarryOverTests
{
    [Fact]
    public void Every_excluded_inventory_entity_has_exactly_one_documented_carry_over_decision()
    {
        var excluded = PortableCompletenessInventoryTests.ExcludedEntityNames;
        var decisions = SelfHostedHostStateCarryOver.Decisions;

        decisions.Select(decision => decision.EntityName)
            .Should().OnlyHaveUniqueItems("a carry-over decision may only appear once");

        // The completeness inventory and the candidate finalizer's copy list are
        // two views of the same fact. A new excluded entity without a decision
        // fails the first assertion; a stale decision fails the second.
        decisions.Select(decision => decision.EntityName)
            .Should().BeSubsetOf(excluded,
                "every carry-over decision must correspond to an excluded inventory entity");
        excluded.Should().BeSubsetOf(decisions.Select(decision => decision.EntityName),
            "a newly excluded host-state entity must be given an explicit carry-over decision");

        foreach (var decision in decisions)
        {
            decision.Rationale.Should().NotBeNullOrWhiteSpace(
                $"{decision.EntityName} must document why it is carried or cleared");
        }
    }

    [Fact]
    public void Every_carry_over_entity_is_mapped_by_the_ef_model()
    {
        using var db = new NostosDbContext(
            new DbContextOptionsBuilder<NostosDbContext>().UseSqlite("Data Source=:memory:").Options);

        foreach (var decision in SelfHostedHostStateCarryOver.Decisions)
        {
            db.Model.FindEntityType(decision.EntityType).Should().NotBeNull(
                $"{decision.EntityName} must remain an EF-mapped entity");
        }
    }

    [Fact]
    public void Carried_entities_are_the_expected_host_operational_state()
    {
        var carried = SelfHostedHostStateCarryOver.CarryDecisions
            .Select(decision => decision.EntityName)
            .ToHashSet(StringComparer.Ordinal);

        carried.Should().BeEquivalentTo(
        [
            nameof(Nostos.Backend.Data.Models.AiProviderSettingsModel),
            nameof(Nostos.Backend.Data.Models.ProviderPreferenceModel),
            nameof(Nostos.Backend.Data.Models.BackupRecord),
            nameof(Nostos.Backend.Data.Models.LibraryCommandReceipt),
            nameof(Nostos.Backend.Data.Models.NoteCommandReceipt),
            nameof(Nostos.Backend.Data.Models.LibraryState),
            nameof(Nostos.Backend.Data.Models.MigrationJobRecord),
            nameof(Nostos.Backend.Data.Models.MigrationSessionRecord),
            nameof(Nostos.Backend.Data.Models.MigrationChunkReceiptRecord),
            nameof(Nostos.Backend.Data.Models.MigrationExportArtifactRecord),
            nameof(Nostos.Backend.Data.Models.MigrationStorageReservationRecord),
        ]);
    }

    [Fact]
    public void Cleared_entities_are_the_import_undo_history()
    {
        var cleared = SelfHostedHostStateCarryOver.Decisions
            .Where(decision => decision.Kind == HostStateCarryOverKind.Clear)
            .Select(decision => decision.EntityName)
            .ToHashSet(StringComparer.Ordinal);

        cleared.Should().BeEquivalentTo(
        [
            nameof(Nostos.Backend.Data.Models.NoteImportBatch),
            nameof(Nostos.Backend.Data.Models.NoteImportBatchNote),
        ]);
    }

    [Fact]
    public void No_carried_entity_declares_a_foreign_key_into_portable_or_cleared_state()
    {
        using var db = new NostosDbContext(
            new DbContextOptionsBuilder<NostosDbContext>().UseSqlite("Data Source=:memory:").Options);
        var carried = SelfHostedHostStateCarryOver.CarryDecisions
            .Select(decision => decision.EntityType)
            .ToHashSet();

        foreach (var decision in SelfHostedHostStateCarryOver.CarryDecisions)
        {
            var entity = db.Model.FindEntityType(decision.EntityType)!;
            var principals = entity.GetForeignKeys()
                .Select(foreignKey => foreignKey.PrincipalEntityType.ClrType)
                .ToArray();
            principals.Should().BeSubsetOf(carried,
                $"{decision.EntityName} must not reference portable or cleared state through a foreign key");
        }
    }
}

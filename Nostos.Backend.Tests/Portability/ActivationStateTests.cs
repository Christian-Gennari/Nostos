using System.Text.Json;
using FluentAssertions;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Activation;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

public sealed class ActivationStateTests
{
    private static readonly IReadOnlyDictionary<SelfHostedActivationPhase, SelfHostedRecoveryAction> RecoveryTable =
        new Dictionary<SelfHostedActivationPhase, SelfHostedRecoveryAction>
        {
            [SelfHostedActivationPhase.CandidatePrepared] = SelfHostedRecoveryAction.Nothing,
            [SelfHostedActivationPhase.ExclusiveEntered] = SelfHostedRecoveryAction.Nothing,
            [SelfHostedActivationPhase.DatabaseCheckpointed] = SelfHostedRecoveryAction.Nothing,
            [SelfHostedActivationPhase.CutoverPrepared] = SelfHostedRecoveryAction.RollBackOriginal,
            [SelfHostedActivationPhase.PreviousMediaRetained] = SelfHostedRecoveryAction.RollBackOriginal,
            [SelfHostedActivationPhase.PreviousDatabaseRetained] = SelfHostedRecoveryAction.RollBackOriginal,
            [SelfHostedActivationPhase.CandidateMediaActivated] = SelfHostedRecoveryAction.RollBackOriginal,
            [SelfHostedActivationPhase.CandidateDatabaseActivated] = SelfHostedRecoveryAction.RollBackOriginal,
            [SelfHostedActivationPhase.PostActivationVerified] = SelfHostedRecoveryAction.RollBackOriginal,
            [SelfHostedActivationPhase.Committed] = SelfHostedRecoveryAction.RollForwardCandidate,
            [SelfHostedActivationPhase.RollingBack] = SelfHostedRecoveryAction.RollBackOriginal,
            [SelfHostedActivationPhase.RolledBack] = SelfHostedRecoveryAction.Nothing,
        };

    public static IEnumerable<object[]> RecoveryCases() => RecoveryTable.Select(p => new object[] { p.Key, p.Value });

    [Fact]
    public void RecoveryTable_IsExhaustive_AndUnknownStatesFailClosed()
    {
        RecoveryTable.Keys.Should().BeEquivalentTo(Enum.GetValues<SelfHostedActivationPhase>());
        foreach (var invalid in new[] { -1, 12, int.MinValue, int.MaxValue })
        {
            SelfHostedActivationState.RecoveryAction((SelfHostedActivationPhase)invalid)
                .Should().Be(SelfHostedRecoveryAction.FailClosed);
            SelfHostedActivationState.RecoveryAction(Journal() with { Phase = (SelfHostedActivationPhase)invalid })
                .Should().Be(SelfHostedRecoveryAction.FailClosed);
        }
        SelfHostedActivationState.RecoveryAction(null).Should().Be(SelfHostedRecoveryAction.Nothing);
    }

    [Theory]
    [MemberData(nameof(RecoveryCases))]
    public void EveryDurablePhase_DecidesRecoveryIncludingRenameBeforeNextPhaseWrite(
        SelfHostedActivationPhase phase, SelfHostedRecoveryAction expected)
    {
        var journal = Journal() with { Phase = phase };
        SelfHostedActivationState.RecoveryAction(journal).Should().Be(expected);
        SelfHostedActivationDocument.RecoveryAction(SelfHostedActivationDocument.Encode(journal)).Should().Be(expected);
    }

    [Fact]
    public void PhaseTransitions_ExhaustivelyEnforceProtocol_AndForbidRollbackAfterCommit()
    {
        var normal = new[]
        {
            SelfHostedActivationPhase.CandidatePrepared, SelfHostedActivationPhase.ExclusiveEntered,
            SelfHostedActivationPhase.DatabaseCheckpointed, SelfHostedActivationPhase.CutoverPrepared,
            SelfHostedActivationPhase.PreviousMediaRetained, SelfHostedActivationPhase.PreviousDatabaseRetained,
            SelfHostedActivationPhase.CandidateMediaActivated, SelfHostedActivationPhase.CandidateDatabaseActivated,
            SelfHostedActivationPhase.PostActivationVerified, SelfHostedActivationPhase.Committed,
        };
        var legal = normal.Zip(normal.Skip(1)).ToHashSet();
        foreach (var phase in normal.Take(9)) legal.Add((phase, SelfHostedActivationPhase.RollingBack));
        legal.Add((SelfHostedActivationPhase.RollingBack, SelfHostedActivationPhase.RolledBack));
        normal.Concat(new[] { SelfHostedActivationPhase.RollingBack, SelfHostedActivationPhase.RolledBack })
            .Should().BeEquivalentTo(Enum.GetValues<SelfHostedActivationPhase>());

        foreach (var from in Enum.GetValues<SelfHostedActivationPhase>().Append((SelfHostedActivationPhase)999))
        foreach (var to in Enum.GetValues<SelfHostedActivationPhase>().Append((SelfHostedActivationPhase)999))
        {
            var allowed = legal.Contains((from, to));
            SelfHostedActivationState.CanTransition(from, to).Should().Be(allowed, $"{from} -> {to}");
            Action transition = () => SelfHostedActivationState.ValidateTransition(from, to);
            if (allowed) transition.Should().NotThrow();
            else transition.Should().Throw<InvalidOperationException>();
        }
    }

    [Fact]
    public void JobOutcomes_UseFrozenDirectionTable_AndDurableCommitOrSafeRollback()
    {
        foreach (var direction in Enum.GetValues<MigrationDirection>())
        foreach (var from in Enum.GetValues<MigrationJobState>())
        foreach (var to in Enum.GetValues<MigrationJobState>())
        foreach (var phase in Enum.GetValues<SelfHostedActivationPhase>())
        {
            var safeFailure = phase is SelfHostedActivationPhase.CandidatePrepared
                or SelfHostedActivationPhase.ExclusiveEntered or SelfHostedActivationPhase.DatabaseCheckpointed
                or SelfHostedActivationPhase.RolledBack;
            var allowed = MigrationJobTransitions.CanTransition(direction, from, to)
                && (from != MigrationJobState.Activating
                    || (to == MigrationJobState.Completed && phase == SelfHostedActivationPhase.Committed)
                    || (to == MigrationJobState.Failed && safeFailure));
            Action transition = () => SelfHostedActivationState.ValidateJobOutcome(direction, from, to, phase);
            if (allowed) transition.Should().NotThrow();
            else transition.Should().Throw<InvalidOperationException>();
        }
        SelfHostedActivationState.CanCancel(MigrationDirection.Import, MigrationJobState.ReadyToActivate).Should().BeTrue();
        SelfHostedActivationState.CanCancel(MigrationDirection.Import, MigrationJobState.Activating).Should().BeFalse();
        SelfHostedActivationState.CanCancel(MigrationDirection.Export, MigrationJobState.ReadyToActivate).Should().BeFalse();
    }

    [Fact]
    public void CorruptionAndFutureVersions_FailClosed_UnknownFieldsRoundTrip()
    {
        var journal = Journal() with
        {
            Extensions = new() { ["futureField"] = JsonSerializer.SerializeToElement(new { value = 7 }) },
        };
        var encoded = SelfHostedActivationDocument.Encode(journal);
        var decoded = SelfHostedActivationDocument.Decode<SelfHostedActivationJournal>(encoded);
        decoded.Extensions!["futureField"].GetProperty("value").GetInt32().Should().Be(7);
        SelfHostedActivationDocument.Encode(decoded).Should().Be(encoded);

        foreach (var invalid in new[] { "", "{", "null", "{}", encoded.Replace("revision-1", "revision-2"),
            encoded.Replace("\"Version\":1", "\"Version\":2"),
            SelfHostedActivationDocument.Encode(journal with { JournalVersion = 2 }),
            SelfHostedActivationDocument.Encode(journal with { JobId = Guid.Empty }),
            SelfHostedActivationDocument.Encode(journal with { OperationId = Guid.Empty }),
            SelfHostedActivationDocument.Encode(journal with { DestinationRevision = null! }) })
            SelfHostedActivationDocument.RecoveryAction(invalid).Should().Be(SelfHostedRecoveryAction.FailClosed);

        // A checksummed payload with a missing phase must not deserialize to the
        // enum default and bypass recovery as CandidatePrepared.
        var missingPhase = new { journal.JobId, journal.OperationId, journal.DestinationRevision,
            journal.RetainPreviousLibrary, journal.UpdatedAtUtc, journal.JournalVersion };
        SelfHostedActivationDocument.RecoveryAction(SelfHostedActivationDocument.Encode(missingPhase))
            .Should().Be(SelfHostedRecoveryAction.FailClosed);
    }

    [Fact]
    public void RecoveryManifest_RoundTripsCountsAndPins_AndExpiresAtSevenDays()
    {
        var created = DateTimeOffset.Parse("2026-10-04T12:00:00Z");
        var manifest = new SelfHostedRecoveryManifest(Guid.NewGuid(), Guid.NewGuid(), created,
            SelfHostedRecoveryManifest.Expiry(created), MigrationRecoveryStatus.Available, "revision-1",
            new MigrationExistingCounts(Books: 3, BookCollections: 6), 42, 123, new string('a', 64),
            new[] { new RecoveryMediaDescriptor(Guid.NewGuid(), "book", ".epub", 123, new string('b', 64)) });
        var roundTrip = SelfHostedActivationDocument.Decode<SelfHostedRecoveryManifest>(SelfHostedActivationDocument.Encode(manifest));
        roundTrip.Should().BeEquivalentTo(manifest);
        (roundTrip.ExpiresAtUtc - roundTrip.CreatedAtUtc).TotalDays.Should().Be(MigrationContractLimits.RecoveryRetentionDays).And.Be(7);
        foreach (var invalid in new[] { manifest with { ManifestVersion = 2 },
            manifest with { ExpiresAtUtc = created.AddDays(1) }, manifest with { DatabaseSha256 = "bad" } })
        {
            Action decode = () => SelfHostedActivationDocument.Decode<SelfHostedRecoveryManifest>(SelfHostedActivationDocument.Encode(invalid));
            decode.Should().Throw<MigrationActivationException>().Which.Code.Should().Be(MigrationActivationErrorCodes.RecoveryCorrupt);
        }
    }

    internal static SelfHostedActivationJournal Journal() => new(Guid.NewGuid(), Guid.NewGuid(),
        SelfHostedActivationPhase.CandidatePrepared, "revision-1", true, DateTimeOffset.UtcNow);
}

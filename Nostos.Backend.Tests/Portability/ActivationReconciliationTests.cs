using FluentAssertions;
using Microsoft.Data.Sqlite;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Activation;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

public sealed class ActivationReconciliationTests
{
    public static IEnumerable<object[]> Cases() => Enum.GetValues<SelfHostedActivationPhase>()
        .SelectMany(p => new[] { new object[] { p, false }, new object[] { p, true } });

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EveryPhase_AndRenameBeforePhaseWrite_SelectsCompleteGeneration_AndRepeatedStartupIsNoOp(
        SelfHostedActivationPhase phase, bool nextRename)
    {
        // Interrupt after EACH recovery rename; reconstruct startup from disk,
        // including the RollingBack journal written before the first repair.
        // This covers between quarantine and restore, between DB and media, and
        // the second run would fail if any move were an unconditional rename.
        for (var failAfter = 0; failAfter <= 4; failAfter++)
        {
            using var files = new ActivationFiles();
            files.Layout(phase, nextRename);
            var moves = 0;
            try
            {
                await files.Reconcile(() => { if (++moves == failAfter) throw new ActivationFileTests.SimulatedCrash(); });
            }
            catch (ActivationFileTests.SimulatedCrash) { await files.Reconcile(); }
            var expected = phase == SelfHostedActivationPhase.Committed ? "candidate" : "original";
            ActivationFiles.DatabaseGeneration(files.Paths.LiveDatabase).Should().Be(expected, $"phase {phase}, next={nextRename}, failure={failAfter}");
            File.ReadAllText(Path.Combine(files.Paths.LiveMedia, "book.epub")).Should().Be(expected);
            var snapshot = files.Snapshot();
            await files.Reconcile();
            files.Snapshot().Should().BeEquivalentTo(snapshot, "a second startup must change no files");
        }
    }

    [Fact]
    public async Task Committed_FinishesMissingCandidateRenames_AndCanRepeatAfterEveryMove()
    {
        for (var failAfter = 0; failAfter <= 2; failAfter++)
        {
            using var files = new ActivationFiles();
            files.Layout(SelfHostedActivationPhase.PreviousDatabaseRetained, false);
            File.WriteAllText(files.Paths.Journal(files.Id), SelfHostedActivationDocument.Encode(files.Journal with { Phase = SelfHostedActivationPhase.Committed }));
            var moves = 0;
            try { await files.Reconcile(() => { if (++moves == failAfter) throw new ActivationFileTests.SimulatedCrash(); }); }
            catch (ActivationFileTests.SimulatedCrash) { await files.Reconcile(); }
            ActivationFiles.DatabaseGeneration(files.Paths.LiveDatabase).Should().Be("candidate");
            File.ReadAllText(Path.Combine(files.Paths.LiveMedia, "book.epub")).Should().Be("candidate");
            ActivationFiles.DatabaseGeneration(files.Paths.PreviousDatabase(files.Id)).Should().Be("original");
        }
    }

    [Fact]
    public async Task Rollback_QuarantinesLiveCandidateSidecars_BeforeOriginalDbReturns_AtEveryCrashPoint()
    {
        for (var failAfter = 0; failAfter <= 6; failAfter++)
        {
            using var files = new ActivationFiles();
            files.Layout(SelfHostedActivationPhase.PostActivationVerified, false);
            // Opaque sidecars prove repair preserves rather than discards them.
            // Real SQLite base files remain independently readable after repair.
            File.WriteAllText(files.Paths.LiveDatabase + "-wal", "candidate-wal");
            File.WriteAllText(files.Paths.LiveDatabase + "-shm", "candidate-shm");
            var moves = 0;
            try { await files.Reconcile(() => { if (++moves == failAfter) throw new ActivationFileTests.SimulatedCrash(); }); }
            catch (ActivationFileTests.SimulatedCrash) { await files.Reconcile(); }
            ActivationFiles.DatabaseGeneration(files.Paths.LiveDatabase).Should().Be("original");
            File.ReadAllText(Path.Combine(files.Paths.LiveMedia, "book.epub")).Should().Be("original");
            File.ReadAllText(files.Paths.CandidateDatabase(files.Id) + "-wal").Should().Be("candidate-wal");
            File.ReadAllText(files.Paths.CandidateDatabase(files.Id) + "-shm").Should().Be("candidate-shm");
            File.Exists(files.Paths.LiveDatabase + "-wal").Should().BeFalse();
            var snapshot = files.Snapshot(); await files.Reconcile(); files.Snapshot().Should().BeEquivalentTo(snapshot);
        }
    }

    [Fact]
    public async Task RealSQLiteWal_IsQuarantinedWithCandidate_AndNeverReplayedIntoOriginal()
    {
        using var files = new ActivationFiles();
        files.Layout(SelfHostedActivationPhase.PostActivationVerified, false);
        var source = Path.Combine(files.Root, "wal-source.sqlite");
        ActivationFiles.CreateDatabase(source, "candidate");
        // Capture a genuine dirty SQLite WAL/base pair while no command is
        // writing it, then close the source connection before invoking recovery.
        using (var connection = new SqliteConnection($"Data Source={source};Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; UPDATE Generation SET Value='candidate-wal';";
            command.ExecuteNonQuery();
            File.Copy(source, files.Paths.LiveDatabase, overwrite: true);
            File.Copy(source + "-wal", files.Paths.LiveDatabase + "-wal");
            File.Copy(source + "-shm", files.Paths.LiveDatabase + "-shm");
        }
        var wal = File.ReadAllBytes(files.Paths.LiveDatabase + "-wal");
        wal.Length.Should().BeGreaterThan(0);
        await files.Reconcile();
        ActivationFiles.DatabaseGeneration(files.Paths.LiveDatabase).Should().Be("original");
        File.ReadAllBytes(files.Paths.CandidateDatabase(files.Id) + "-wal").Should().Equal(wal);
        ActivationFiles.DatabaseGeneration(files.Paths.CandidateDatabase(files.Id)).Should().Be("candidate-wal");
        var store = new SelfHostedActivationJournalStore(files.Paths, new());
        store.ReadResolved(files.Id)!.Phase.Should().Be(SelfHostedActivationPhase.RolledBack);
        var before = files.Snapshot(); await files.Reconcile(); files.Snapshot().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task CorruptOrUnsupportedJournal_FailsClosed_LeavesEveryLiveFileAndMarkerUntouched()
    {
        foreach (var corrupt in new[] { "", "{", "{}", "null", SelfHostedActivationDocument.Encode(ActivationStateTests.Journal() with { JournalVersion = 2 }) })
        {
            using var files = new ActivationFiles();
            files.Layout(SelfHostedActivationPhase.CandidateDatabaseActivated, false);
            File.WriteAllText(files.Paths.Journal(files.Id), corrupt);
            var before = files.Snapshot();
            Func<Task> reconcile = () => files.Reconcile();
            (await reconcile.Should().ThrowAsync<MigrationActivationException>()).Which.Code.Should().Be(MigrationActivationErrorCodes.RecoveryCorrupt);
            files.Snapshot().Should().BeEquivalentTo(before);
        }
    }

    [Fact]
    public async Task UnexpectedLayoutOrSecondUnresolvedJournal_FailsBeforeAnyLiveMutation()
    {
        using var files = new ActivationFiles();
        files.Layout(SelfHostedActivationPhase.CandidateMediaActivated, false);
        // DB would otherwise be repairable, but missing media rollback evidence
        // must be detected BEFORE either component is moved.
        Directory.Delete(files.Paths.PreviousMedia(files.Id), true);
        var snapshot = files.Snapshot();
        Func<Task> reconcile = () => files.Reconcile();
        await reconcile.Should().ThrowAsync<MigrationActivationException>();
        files.Snapshot().Should().BeEquivalentTo(snapshot);
        var second = ActivationStateTests.Journal() with { Phase = SelfHostedActivationPhase.CutoverPrepared };
        files.Paths.Prepare(second.JobId);
        File.WriteAllText(files.Paths.Journal(second.JobId), SelfHostedActivationDocument.Encode(second));
        snapshot = files.Snapshot();
        await reconcile.Should().ThrowAsync<MigrationActivationException>();
        files.Snapshot().Should().BeEquivalentTo(snapshot);
    }

    [Fact]
    public async Task NoJournal_IsNoOp_AndStaleMarkerClearsWithoutChangingLibrary()
    {
        using var files = new ActivationFiles();
        var before = files.Snapshot();
        await files.Reconcile();
        files.Snapshot().Should().BeEquivalentTo(before);
        new LibraryMaintenanceMarker(files.Paths.LiveDatabase).Write(LibraryMaintenanceReason.Activation);
        await files.Reconcile();
        files.Snapshot().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task InitialTornTempWithoutFinal_IsNoCutover_AndOriginalIsUntouched()
    {
        using var files = new ActivationFiles();
        File.WriteAllText(files.Paths.TemporaryJournal(files.Id, Guid.NewGuid()), "{partial");
        var before = files.Snapshot(); await files.Reconcile(); files.Snapshot().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task ZeroLengthLiveDatabase_IsNeverClassifiedAsAbsentOrDeleted()
    {
        foreach (var withCompanion in new[] { false, true })
        {
            using var files = new ActivationFiles();
            // This phase expects the live database to be absent (it was retained
            // as the previous generation); a zero-length file at the live path
            // is an unexpected layout, never "no database".
            files.Layout(SelfHostedActivationPhase.PreviousDatabaseRetained, false);
            File.WriteAllBytes(files.Paths.LiveDatabase, []);
            if (withCompanion)
            {
                File.WriteAllText(files.Paths.LiveDatabase + "-wal", "not-a-wal");
            }

            var before = files.Snapshot();
            Func<Task> reconcile = () => files.Reconcile();
            await reconcile.Should().ThrowAsync<MigrationActivationException>();
            File.Exists(files.Paths.LiveDatabase).Should().BeTrue(
                "a zero-length file is never deleted and never treated as an absent generation");
            files.Snapshot().Should().BeEquivalentTo(before,
                "a refused reconciliation must not touch any file");
        }
    }

    [Fact]
    public async Task AllRegisteredStepsValidateBeforeAnyExecution_AndLaterStepsParticipateUnderClosedGate()
    {
        using var files = new ActivationFiles(); files.Layout(SelfHostedActivationPhase.CandidateDatabaseActivated, false);
        var gate = new LibraryMaintenanceCoordinator(marker: new LibraryMaintenanceMarker(files.Paths.LiveDatabase));
        var store = new SelfHostedActivationJournalStore(files.Paths, gate);
        var later = new FutureStep(gate);
        var engine = new SelfHostedActivationRecoveryStartupService(files.Paths, store, gate,
            [new SelfHostedActivationComponentStep(files.Paths, false), new SelfHostedActivationComponentStep(files.Paths, true), later]);
        var before = files.Snapshot();
        Func<Task> reconcile = () => engine.ReconcileIncompleteAsync();
        await reconcile.Should().ThrowAsync<InvalidOperationException>();
        files.Snapshot().Should().BeEquivalentTo(before);
        gate.IsMaintenanceActive.Should().BeTrue(); gate.TryEnterOperation().Should().BeNull();
        later.Refuse = false;
        await engine.ReconcileIncompleteAsync();
        later.Executed.Should().BeTrue();
        gate.IsMaintenanceActive.Should().BeFalse();
        var after = files.Snapshot();
        await engine.ReconcileIncompleteAsync();
        files.Snapshot().Should().BeEquivalentTo(after);
    }

    private sealed class FutureStep(LibraryMaintenanceCoordinator gate) : ISelfHostedActivationRecoveryStep
    {
        public int Order => 100;
        public bool Refuse { get; set; } = true;
        public bool Executed { get; private set; }
        public void Validate(SelfHostedActivationJournal journal, SelfHostedRecoveryAction action)
        { if (Refuse) throw new InvalidOperationException("later verification refused"); }
        public void Execute(SelfHostedActivationJournal journal, SelfHostedRecoveryAction action)
        { gate.TryEnterOperation().Should().BeNull(); Executed = true; }
    }
}

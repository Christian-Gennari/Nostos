using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Activation;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

public sealed class ActivationFileTests
{
    public static IEnumerable<object[]> Phases() => Enum.GetValues<SelfHostedActivationPhase>().Select(p => new object[] { p });

    [Theory]
    [MemberData(nameof(Phases))]
    public async Task Journal_EveryPhaseRoundTrips_AndSamePhaseRetryIsByteIdentical(SelfHostedActivationPhase phase)
    {
        using var files = new ActivationFiles();
        var gate = new LibraryMaintenanceCoordinator();
        var store = new SelfHostedActivationJournalStore(files.Paths, gate);
        var journal = files.Journal;
        store.Write(journal);
        await using var lease = await gate.EnterExclusiveAsync(LibraryMaintenanceReason.Activation);
        foreach (var target in Route(phase)) journal = store.Advance(journal.JobId, target, lease);
        store.Read(journal.JobId).Should().Be(journal);
        var bytes = File.ReadAllBytes(files.Paths.Journal(journal.JobId));
        store.Write(journal, lease);
        File.ReadAllBytes(files.Paths.Journal(journal.JobId)).Should().Equal(bytes);
        store.Advance(journal.JobId, phase, lease).Should().Be(journal);
    }

    [Fact]
    public async Task Journal_RejectsIllegalTransition_IdentityChange_AbsentForeignAndDisposedLeases()
    {
        using var files = new ActivationFiles();
        var gate = new LibraryMaintenanceCoordinator();
        var store = new SelfHostedActivationJournalStore(files.Paths, gate);
        store.Write(files.Journal);
        Action without = () => store.Write(files.Journal with { Phase = SelfHostedActivationPhase.ExclusiveEntered });
        without.Should().Throw<InvalidOperationException>();
        var otherGate = new LibraryMaintenanceCoordinator();
        await using var foreign = await otherGate.EnterExclusiveAsync(LibraryMaintenanceReason.Activation);
        Action foreignWrite = () => store.Write(files.Journal with { Phase = SelfHostedActivationPhase.ExclusiveEntered }, foreign);
        foreignWrite.Should().Throw<InvalidOperationException>();
        var lease = await gate.EnterExclusiveAsync(LibraryMaintenanceReason.Activation);
        Action illegal = () => store.Advance(files.Journal.JobId, SelfHostedActivationPhase.Committed, lease);
        illegal.Should().Throw<InvalidOperationException>();
        Action identity = () => store.Write(files.Journal with { Phase = SelfHostedActivationPhase.ExclusiveEntered, OperationId = Guid.NewGuid() }, lease);
        identity.Should().Throw<InvalidOperationException>();
        await lease.DisposeAsync();
        Action disposed = () => store.Advance(files.Journal.JobId, SelfHostedActivationPhase.ExclusiveEntered, lease);
        disposed.Should().Throw<InvalidOperationException>();
        store.Read(files.Journal.JobId).Should().Be(files.Journal);
        using var heldOperation = gate.TryEnterOperation();
        var draining = gate.EnterExclusiveAsync(LibraryMaintenanceReason.Activation);
        gate.IsMaintenanceActive.Should().BeTrue();
        without.Should().Throw<InvalidOperationException>("closed admission is not a drained exclusive lease");
        heldOperation!.Dispose();
        await using var drained = await draining;
    }

    [Fact]
    public async Task Journal_TornFinalAtEveryByteBoundary_BadChecksumAndUnknownVersions_FailClosed()
    {
        using var files = new ActivationFiles();
        var gate = new LibraryMaintenanceCoordinator();
        var store = new SelfHostedActivationJournalStore(files.Paths, gate);
        store.Write(files.Journal);
        var path = files.Paths.Journal(files.Journal.JobId);
        var encoded = SelfHostedActivationDocument.Encode(files.Journal);
        var bytes = Encoding.UTF8.GetBytes(encoded);
        // Every byte boundary covers empty, partial header/body/checksum/closing
        // delimiter, rather than assuming a particular serializer field length.
        for (var length = 0; length < bytes.Length; length++)
        {
            File.WriteAllBytes(path, bytes[..length]);
            Action read = () => store.Read(files.Journal.JobId);
            read.Should().Throw<MigrationActivationException>().Which.Code.Should().Be(MigrationActivationErrorCodes.RecoveryCorrupt);
        }
        foreach (var corrupt in new[] { encoded.Replace("revision-1", "revision-2"), encoded.Replace("\"Version\":1", "\"Version\":2"),
            SelfHostedActivationDocument.Encode(files.Journal with { JournalVersion = 2 }),
            SelfHostedActivationDocument.Encode(files.Journal with { Phase = (SelfHostedActivationPhase)99 }),
            SelfHostedActivationDocument.Encode(files.Journal with { JobId = Guid.NewGuid() }) })
        {
            File.WriteAllText(path, corrupt);
            Action read = () => store.Read(files.Journal.JobId);
            read.Should().Throw<MigrationActivationException>();
        }
        File.WriteAllBytes(path, [0xff, 0xfe, 0xff]);
        Action invalidUtf8 = () => store.Read(files.Journal.JobId);
        invalidUtf8.Should().Throw<MigrationActivationException>();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Journal_CrashBeforePublish_EveryTornTempLeavesPreviousStateIntact()
    {
        using var files = new ActivationFiles();
        var gate = new LibraryMaintenanceCoordinator();
        var store = new SelfHostedActivationJournalStore(files.Paths, gate);
        store.Write(files.Journal);
        await using var lease = await gate.EnterExclusiveAsync(LibraryMaintenanceReason.Activation);
        store.BeforePublishForTesting = () => throw new SimulatedCrash();
        Action write = () => store.Advance(files.Journal.JobId, SelfHostedActivationPhase.ExclusiveEntered, lease);
        write.Should().Throw<SimulatedCrash>();
        var temporary = Directory.GetFiles(Path.GetDirectoryName(files.Paths.Journal(files.Journal.JobId))!, "*.tmp").Single();
        var bytes = File.ReadAllBytes(temporary);
        for (var length = 0; length <= bytes.Length; length++)
        {
            File.WriteAllBytes(temporary, bytes[..length]);
            store.Read(files.Journal.JobId).Should().Be(files.Journal);
        }
        store.BeforePublishForTesting = null;
        store.Advance(files.Journal.JobId, SelfHostedActivationPhase.ExclusiveEntered, lease).Phase.Should().Be(SelfHostedActivationPhase.ExclusiveEntered);
    }

    [Fact]
    public void Paths_RejectEmptyAndHostileIds_AndAllComponentsStayInConfiguredParents()
    {
        using var files = new ActivationFiles();
        Action empty = () => files.Paths.CandidateDatabase(Guid.Empty);
        empty.Should().Throw<MigrationActivationException>();
        foreach (var hostile in new[] { "../outside", "/etc/passwd", "..\\outside", "", "CON", new string('a', 32) + "/.." })
        {
            Guid.TryParseExact(hostile, "N", out _).Should().BeFalse();
            var directory = Path.Combine(files.Paths.JournalRoot, hostile == "../outside" ? "hostile" : "unexpected");
            Directory.CreateDirectory(directory);
            var store = new SelfHostedActivationJournalStore(files.Paths, new());
            Action scan = () => store.ReadAll();
            scan.Should().Throw<MigrationActivationException>();
            Directory.Delete(directory);
        }
        foreach (var path in new[] { files.Paths.CandidateDatabase(files.Id), files.Paths.PreviousDatabase(files.Id),
            files.Paths.Journal(files.Id), files.Paths.RecoveryManifest(files.Id) })
            Path.GetRelativePath(Path.GetDirectoryName(files.Paths.LiveDatabase)!, path).Should().NotStartWith("..");
        foreach (var path in new[] { files.Paths.CandidateMedia(files.Id), files.Paths.PreviousMedia(files.Id) })
            Path.GetRelativePath(Path.GetDirectoryName(files.Paths.LiveMedia)!, path).Should().NotStartWith("..");
    }

    [Fact]
    public void Journal_NoncanonicalDirectoryCannotHideAnActionableJournal()
    {
        using var files = new ActivationFiles();
        var directory = Path.Combine(files.Paths.JournalRoot, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
        Directory.CreateDirectory(directory);
        var journal = files.Journal with { JobId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), Phase = SelfHostedActivationPhase.CutoverPrepared };
        File.WriteAllText(Path.Combine(directory, "activation.json"), SelfHostedActivationDocument.Encode(journal));
        var store = new SelfHostedActivationJournalStore(files.Paths, new());
        Action read = () => store.ReadAll();
        read.Should().Throw<MigrationActivationException>().Which.Code.Should().Be(MigrationActivationErrorCodes.RecoveryCorrupt);
    }

    [Fact]
    public void Paths_CrossVolumeDetectionIsUpfront_Typed_AndDoesNotRequireDbAndMediaToShareAVolume()
    {
        using var files = new ActivationFiles();
        var volume = new TestVolume { Allow = false };
        var paths = new SelfHostedActivationPaths(files.Paths.LiveDatabase, files.Paths.LiveMedia, volume);
        var before = files.Snapshot();
        Action prepare = () => paths.Prepare(files.Id);
        prepare.Should().Throw<MigrationActivationException>().Which.Code.Should().Be("migration_activation_cross_volume");
        files.Snapshot().Should().BeEquivalentTo(before);
        volume.Allow = true;
        paths.Prepare(files.Id);
        volume.Comparisons.Should().OnlyContain(pair =>
            Path.GetRelativePath(Path.GetDirectoryName(pair.Item1)!, pair.Item2).StartsWith(".nostos-", StringComparison.Ordinal));
    }

    [Fact]
    public void Paths_RejectSymlinkTraversalAndLiveRootLinks_WithoutTouchingTargets()
    {
        if (OperatingSystem.IsWindows()) return; // Windows symlink creation requires host privileges
        using var files = new ActivationFiles();
        var outside = Path.Combine(files.Root, "outside"); Directory.CreateDirectory(outside);
        var candidateParent = Path.GetDirectoryName(files.Paths.CandidateDatabase(files.Id))!;
        File.Delete(files.Paths.CandidateDatabase(files.Id));
        Directory.Delete(candidateParent);
        Directory.CreateSymbolicLink(candidateParent, outside);
        Action verify = () => files.Paths.Verify(files.Id);
        verify.Should().Throw<Exception>();
        Directory.GetFileSystemEntries(outside).Should().BeEmpty();
        Directory.Delete(candidateParent);
        Directory.CreateDirectory(candidateParent);
        Directory.Delete(files.Paths.LiveMedia, true);
        Directory.CreateSymbolicLink(files.Paths.LiveMedia, outside);
        verify.Should().Throw<Exception>();
    }

    internal static IEnumerable<SelfHostedActivationPhase> Route(SelfHostedActivationPhase phase)
    {
        if (phase is SelfHostedActivationPhase.RollingBack or SelfHostedActivationPhase.RolledBack)
        {
            yield return SelfHostedActivationPhase.RollingBack;
            if (phase == SelfHostedActivationPhase.RolledBack) yield return phase;
        }
        else for (var p = SelfHostedActivationPhase.ExclusiveEntered; p <= phase; p++) yield return p;
    }

    private sealed class TestVolume : IActivationVolume
    {
        public bool Allow { get; set; }
        public List<(string, string)> Comparisons { get; } = [];
        public bool SameVolume(string first, string second) { Comparisons.Add((first, second)); return Allow; }
    }
    internal sealed class SimulatedCrash : Exception;
}

internal sealed class ActivationFiles : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"nostos-activation-{Guid.NewGuid():N}");
    internal Guid Id => Journal.JobId;
    internal SelfHostedActivationJournal Journal { get; } = ActivationStateTests.Journal();
    internal SelfHostedActivationPaths Paths { get; }
    internal ActivationFiles()
    {
        Directory.CreateDirectory(Path.Combine(Root, "db-volume"));
        Directory.CreateDirectory(Path.Combine(Root, "media-volume"));
        Paths = new(Path.Combine(Root, "db-volume", "custom.sqlite"), Path.Combine(Root, "media-volume", "library"));
        Paths.Prepare(Id);
        CreateDatabase(Paths.LiveDatabase, "original");
        CreateDatabase(Paths.CandidateDatabase(Id), "candidate");
        CreateMedia(Paths.LiveMedia, "original");
        CreateMedia(Paths.CandidateMedia(Id), "candidate");
    }
    internal static void CreateDatabase(string path, string generation)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE Generation(Value TEXT); INSERT INTO Generation VALUES ($value);";
        command.Parameters.AddWithValue("$value", generation); command.ExecuteNonQuery();
    }
    internal static string DatabaseGeneration(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT Value FROM Generation";
        return (string)command.ExecuteScalar()!;
    }
    internal static void CreateMedia(string path, string generation)
    { Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, "book.epub"), generation); }
    internal Dictionary<string, string> Snapshot() => Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
        .ToDictionary(p => Path.GetRelativePath(Root, p), p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
    internal void Layout(SelfHostedActivationPhase phase, bool nextRename)
    {
        var count = phase switch
        {
            SelfHostedActivationPhase.CutoverPrepared => 0,
            SelfHostedActivationPhase.PreviousMediaRetained => 1,
            SelfHostedActivationPhase.PreviousDatabaseRetained => 2,
            SelfHostedActivationPhase.CandidateMediaActivated => 3,
            SelfHostedActivationPhase.CandidateDatabaseActivated or SelfHostedActivationPhase.PostActivationVerified or SelfHostedActivationPhase.Committed => 4,
            SelfHostedActivationPhase.RollingBack => 4,
            _ => 0,
        };
        if (nextRename && phase >= SelfHostedActivationPhase.CutoverPrepared && phase <= SelfHostedActivationPhase.CandidateMediaActivated) count++;
        if (count >= 1) Directory.Move(Paths.LiveMedia, Paths.PreviousMedia(Id));
        if (count >= 2) File.Move(Paths.LiveDatabase, Paths.PreviousDatabase(Id));
        if (count >= 3) Directory.Move(Paths.CandidateMedia(Id), Paths.LiveMedia);
        if (count >= 4) File.Move(Paths.CandidateDatabase(Id), Paths.LiveDatabase);
        File.WriteAllText(Paths.Journal(Id), SelfHostedActivationDocument.Encode(Journal with { Phase = phase }));
        new LibraryMaintenanceMarker(Paths.LiveDatabase).Write(LibraryMaintenanceReason.Activation);
    }
    internal async Task Reconcile(Action? afterRename = null)
    {
        var marker = new LibraryMaintenanceMarker(Paths.LiveDatabase);
        var gate = new LibraryMaintenanceCoordinator(marker: marker);
        var store = new SelfHostedActivationJournalStore(Paths, gate);
        var engine = new SelfHostedActivationRecoveryStartupService(Paths, store, gate,
            [new SelfHostedActivationComponentStep(Paths, false, afterRename), new SelfHostedActivationComponentStep(Paths, true, afterRename)]);
        await engine.ReconcileIncompleteAsync();
        gate.IsMaintenanceActive.Should().BeFalse();
        File.Exists(marker.MarkerPath).Should().BeFalse();
    }
    public void Dispose() => Directory.Delete(Root, true);
}

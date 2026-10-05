using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Activation;
using Nostos.Backend.Services.Portability.Migration;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Slice 10 tests for the activation orphan sweep: only provably unreferenced
/// leftovers of terminal or absent jobs are removed after the safety age, active
/// jobs and too-young entries are kept, retained recovery material without a
/// manifest is never destroyed, and no path is followed outside the roots.
/// </summary>
public sealed class ActivationOrphanSweepTests
{
    [Fact]
    public async Task TerminalAndAbsentJobLeftovers_AreRemoved_ActiveAndTooYoungAreKept()
    {
        using var bed = new ActivationMaintenanceTestBed();
        var old = bed.Clock.UtcNow.UtcDateTime.AddHours(-48);
        var young = bed.Clock.UtcNow.UtcDateTime;

        var terminalOld = Guid.NewGuid();
        await bed.SeedJobAsync(terminalOld, MigrationJobState.Completed);
        bed.SeedCandidateDatabase(terminalOld, old);
        bed.SeedCandidateMedia(terminalOld, old);

        var activeOld = Guid.NewGuid();
        await bed.SeedJobAsync(activeOld, MigrationJobState.ReadyToActivate);
        bed.SeedCandidateDatabase(activeOld, old);
        bed.SeedCandidateMedia(activeOld, old);

        var terminalYoung = Guid.NewGuid();
        await bed.SeedJobAsync(terminalYoung, MigrationJobState.Completed);
        bed.SeedCandidateDatabase(terminalYoung, young);
        bed.SeedCandidateMedia(terminalYoung, young);

        var absentOld = Guid.NewGuid();
        bed.SeedCandidateDatabase(absentOld, old);

        var result = await bed.CreateSweep().SweepAsync(default);

        result.RemovedEntries.Should().BeGreaterThanOrEqualTo(3);
        File.Exists(bed.Paths.CandidateDatabase(terminalOld)).Should().BeFalse(
            "a terminal job's candidate database is an orphan");
        Directory.Exists(bed.Paths.CandidateMedia(terminalOld)).Should().BeFalse(
            "a terminal job's candidate media is an orphan");
        File.Exists(bed.Paths.CandidateDatabase(absentOld)).Should().BeFalse(
            "a candidate whose job no longer exists is an orphan");

        File.Exists(bed.Paths.CandidateDatabase(activeOld)).Should().BeTrue(
            "an active job may still retry activation");
        Directory.Exists(bed.Paths.CandidateMedia(activeOld)).Should().BeTrue();
        File.Exists(bed.Paths.CandidateDatabase(terminalYoung)).Should().BeTrue(
            "a leftover younger than the safety age is never removed");
        Directory.Exists(bed.Paths.CandidateMedia(terminalYoung)).Should().BeTrue();
    }

    [Fact]
    public async Task SymlinkOutOfTheRoot_IsNotFollowed()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // creating symlinks needs privileges Windows CI does not grant
        }

        using var bed = new ActivationMaintenanceTestBed();
        var old = bed.Clock.UtcNow.UtcDateTime.AddHours(-48);
        var jobId = Guid.NewGuid();
        bed.SeedCandidateMedia(jobId, old);
        var outside = Path.Combine(bed.Root, "outside");
        Directory.CreateDirectory(outside);
        var outsideFile = Path.Combine(outside, "keep.txt");
        File.WriteAllText(outsideFile, "keep");
        Directory.CreateSymbolicLink(Path.Combine(bed.Paths.CandidateMedia(jobId), "escape"), outside);

        var result = await bed.CreateSweep().SweepAsync(default);

        File.Exists(outsideFile).Should().BeTrue("the sweep never follows a link out of the root");
        Directory.Exists(outside).Should().BeTrue();
        Directory.Exists(bed.Paths.CandidateMedia(jobId)).Should().BeTrue(
            "a tree containing an unexpected link is left for an operator");
        result.RemovedEntries.Should().Be(0);
    }

    [Fact]
    public async Task RecoveryWithoutManifest_MaterialIsLeft_EmptyTempOnlyIsRemoved()
    {
        using var bed = new ActivationMaintenanceTestBed();
        var old = bed.Clock.UtcNow.UtcDateTime.AddHours(-48);

        var material = Guid.NewGuid();
        bed.Paths.PrepareRecovery(material);
        RecoveryTestBed.CreateRawDatabase(bed.Paths.PreviousDatabase(material), "previous");
        ActivationMaintenanceTestBed.SetTreeWriteTimeUtc(
            Path.GetDirectoryName(bed.Paths.RecoveryManifest(material))!, old);

        var emptyTemp = Guid.NewGuid();
        var emptyDirectory = Path.GetDirectoryName(bed.Paths.RecoveryManifest(emptyTemp))!;
        Directory.CreateDirectory(emptyDirectory);
        var temporary = Path.Combine(emptyDirectory, $"recovery.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temporary, "torn");
        ActivationMaintenanceTestBed.SetTreeWriteTimeUtc(emptyDirectory, old);

        var deleting = Guid.NewGuid();
        bed.Manifests.CreateDeletionMarker(deleting);
        ActivationMaintenanceTestBed.SetTreeWriteTimeUtc(
            Path.GetDirectoryName(bed.Paths.RecoveryManifest(deleting))!, old);

        var valid = bed.SeedExpiredCopy(bed.Clock.UtcNow);

        var result = await bed.CreateSweep().SweepAsync(default);

        File.Exists(bed.Paths.PreviousDatabase(material)).Should().BeTrue(
            "retained material without a manifest is ambiguous and never destroyed");
        result.LeftAmbiguous.Should().BeGreaterThanOrEqualTo(1);
        Directory.Exists(emptyDirectory).Should().BeFalse(
            "an empty interrupted manifest plan older than the safety age is removed");
        Directory.Exists(Path.GetDirectoryName(bed.Paths.RecoveryManifest(deleting))).Should().BeTrue(
            "the guarded cleanup owns a durable deletion marker");
        Directory.Exists(bed.Paths.PreviousMedia(valid.JobId)).Should().BeTrue(
            "the guarded cleanup owns a valid manifest");
    }

    [Fact]
    public async Task RetriedJobBetweenCheckAndDelete_CandidateIsKept()
    {
        using var bed = new ActivationMaintenanceTestBed();
        var old = bed.Clock.UtcNow.UtcDateTime.AddHours(-48);
        var jobId = Guid.NewGuid();
        await bed.SeedJobAsync(jobId, MigrationJobState.Failed);
        bed.SeedCandidateDatabase(jobId, old);
        bed.SeedCandidateMedia(jobId, old);

        var sweep = bed.CreateSweep();
        MigrationJobStoreException? retryResult = null;
        sweep.BeforeJobClaimForTesting = id =>
        {
            if (id == jobId)
            {
                // The user retries the old failed job after the pass started;
                // the sweep's fresh per-job read must observe it as active.
                retryResult = bed.TryRetryAsync(id).GetAwaiter().GetResult();
            }
        };

        var result = await sweep.SweepAsync(default);

        retryResult.Should().BeNull("the retry wins the race before the sweep claims the job");
        (await bed.ReadJobAsync(jobId)).State.Should().Be((int)MigrationJobState.Pending);
        File.Exists(bed.Paths.CandidateDatabase(jobId)).Should().BeTrue(
            "an active job must never lose its candidate to the sweep");
        Directory.Exists(bed.Paths.CandidateMedia(jobId)).Should().BeTrue();
        result.RemovedEntries.Should().Be(0);
    }

    [Fact]
    public async Task SweepClaimWins_RetryLosesAndChangesNothing()
    {
        using var bed = new ActivationMaintenanceTestBed();
        var old = bed.Clock.UtcNow.UtcDateTime.AddHours(-48);
        var jobId = Guid.NewGuid();
        await bed.SeedJobAsync(jobId, MigrationJobState.Failed);
        bed.SeedCandidateDatabase(jobId, old);
        bed.SeedCandidateMedia(jobId, old);

        var sweep = bed.CreateSweep();
        MigrationJobStoreException? retryResult = null;
        sweep.AfterJobClaimForTesting = id =>
        {
            if (id != jobId)
            {
                return;
            }

            var before = bed.ReadJobAsync(id).GetAwaiter().GetResult();
            retryResult = bed.TryRetryAsync(id).GetAwaiter().GetResult();
            var after = bed.ReadJobAsync(id).GetAwaiter().GetResult();
            after.Version.Should().Be(before.Version, "the losing retry must change nothing");
            after.State.Should().Be(before.State);
        };

        var result = await sweep.SweepAsync(default);

        retryResult.Should().NotBeNull("the sweep holds the fenced cleanup claim");
        retryResult!.Code.Should().Be(MigrationJobStoreErrorCodes.LeaseConflict);
        File.Exists(bed.Paths.CandidateDatabase(jobId)).Should().BeFalse("the sweep wins and removes the orphan");
        Directory.Exists(bed.Paths.CandidateMedia(jobId)).Should().BeFalse();
        (await bed.ReadJobAsync(jobId)).State.Should().Be((int)MigrationJobState.Failed);
        result.RemovedEntries.Should().BeGreaterThanOrEqualTo(2);

        // The claim is released when the job's deletion completes, so a later
        // retry proceeds normally.
        (await bed.TryRetryAsync(jobId)).Should().BeNull();
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("invalid-utf8")]
    [InlineData("checksum")]
    [InlineData("wrong-job")]
    [InlineData("bad-validation")]
    [InlineData("directory")]
    public async Task CorruptManifest_IsLeftByteForByte(string kind)
    {
        using var bed = new ActivationMaintenanceTestBed();
        var jobId = Guid.NewGuid();
        bed.Paths.PrepareRecovery(jobId);
        var path = bed.Paths.RecoveryManifest(jobId);
        byte[]? before = null;
        switch (kind)
        {
            case "truncated":
                before = Encoding.UTF8.GetBytes("{\"Version\":1,\"PayloadJson\":\"x\"");
                File.WriteAllBytes(path, before);
                break;
            case "invalid-utf8":
                before = [0xFF, 0xFE, 0x80, 0x00];
                File.WriteAllBytes(path, before);
                break;
            case "checksum":
                before = Encoding.UTF8.GetBytes(Regex.Replace(
                    SelfHostedActivationDocument.Encode(bed.BuildManifest(jobId, bed.Clock.UtcNow)),
                    "\"Sha256\":\"[0-9a-fA-F]{64}\"",
                    "\"Sha256\":\"" + new string('0', 64) + "\""));
                File.WriteAllBytes(path, before);
                break;
            case "wrong-job":
                before = Encoding.UTF8.GetBytes(
                    SelfHostedActivationDocument.Encode(bed.BuildManifest(Guid.NewGuid(), bed.Clock.UtcNow)));
                File.WriteAllBytes(path, before);
                break;
            case "bad-validation":
                before = Encoding.UTF8.GetBytes(SelfHostedActivationDocument.Encode(
                    bed.BuildManifest(jobId, bed.Clock.UtcNow) with { ExpiresAtUtc = bed.Clock.UtcNow.AddDays(1) }));
                File.WriteAllBytes(path, before);
                break;
            case "directory":
                Directory.CreateDirectory(path);
                break;
        }

        ActivationMaintenanceTestBed.SetTreeWriteTimeUtc(
            Path.GetDirectoryName(path)!, bed.Clock.UtcNow.UtcDateTime.AddHours(-48));

        var result = await bed.CreateSweep().SweepAsync(default);

        result.LeftAmbiguous.Should().BeGreaterThanOrEqualTo(1);
        if (kind == "directory")
        {
            Directory.Exists(path).Should().BeTrue("a corrupt manifest is never erased");
        }
        else
        {
            File.Exists(path).Should().BeTrue("a corrupt manifest is never erased");
            File.ReadAllBytes(path).Should().Equal(before!);
        }
    }
}

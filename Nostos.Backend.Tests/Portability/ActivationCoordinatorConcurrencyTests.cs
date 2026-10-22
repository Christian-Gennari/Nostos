using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Activation;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Slice 7 concurrency: Phase A runs beside live readers and writers, the
/// exclusive window refuses new work, and two concurrent activation requests
/// produce exactly one cutover.
/// </summary>
[Collection(ActivationCoordinatorCollection.Name)]
public sealed class ActivationCoordinatorConcurrencyTests
{
    [Fact]
    public async Task OpenReaderDuringPhaseA_IsUnaffected_AndTheWindowWaitsForIt()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var reader = await bed.Maintenance.EnterOperationAsync(default);

        var activation = bed.ActivateAsync(confirm: true, observer: step =>
        {
            if (string.Equals(step, SelfHostedActivationSteps.AfterVerifyCandidate, StringComparison.Ordinal))
            {
                reached.TrySetResult();
            }
        });

        // Phase A finished with a live reader still holding a shared lease.
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        activation.IsCompleted.Should().BeFalse(
            "the exclusive window must wait for the existing reader to drain");

        await reader.DisposeAsync();

        var result = await activation;
        result.Outcome.Should().Be(SelfHostedActivationOutcome.Completed);
        await bed.AssertImportedGenerationAsync();
        bed.Maintenance.IsMaintenanceActive.Should().BeFalse("the lease is released on the success path");
    }

    [Fact]
    public async Task TwoConcurrentActivationRequests_ProduceExactlyOneCutover()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);

        var results = await Task.WhenAll(
            bed.ActivateAsync(confirm: true),
            bed.ActivateAsync(confirm: true));

        results.Count(result => result.Outcome == SelfHostedActivationOutcome.Completed)
            .Should().Be(1, "only one request may perform the cutover");
        results.Count(result => result.Outcome is SelfHostedActivationOutcome.InProgress
            or SelfHostedActivationOutcome.AlreadyCompleted).Should().Be(1);

        (await bed.CurrentRevisionAsync()).Should().Be(ActivationCoordinatorTemplate.AdvancedRevision,
            "the library revision is advanced exactly once");
        (await bed.ReadJobAsync()).State.Should().Be((int)MigrationJobState.Completed);
        await bed.AssertImportedGenerationAsync();

        // A later request is an idempotent success with no second cutover.
        (await bed.ActivateAsync(confirm: true)).Outcome
            .Should().Be(SelfHostedActivationOutcome.AlreadyCompleted);
        (await bed.CurrentRevisionAsync()).Should().Be(ActivationCoordinatorTemplate.AdvancedRevision);
    }

    /// <summary>
    /// The swap window deliberately has no database at the live path. A second
    /// activation request reaching the coordinator inside that window must not
    /// open SQLite there: doing so materializes an empty database, the owner's
    /// candidate rename then finds the destination occupied, and a load-timed
    /// parallel request turns a successful cutover into a generic failure.
    /// This is the deterministic reproduction of the CI flake.
    /// </summary>
    [Fact]
    public async Task SecondRequestInsideTheSwapWindow_DoesNotCreateAnEmptyLiveDatabase()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);
        var atRetainedDatabase = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The observer blocks a pool thread at the boundary, so run the first
        // activation away from the test's synchronization context.
        var first = Task.Run(() => bed.ActivateAsync(confirm: true, observer: step =>
        {
            if (string.Equals(step, SelfHostedActivationSteps.PhasePreviousDatabaseRetained, StringComparison.Ordinal))
            {
                atRetainedDatabase.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            }
        }));

        await atRetainedDatabase.Task.WaitAsync(TimeSpan.FromSeconds(30));
        File.Exists(bed.Paths.LiveDatabase).Should().BeFalse(
            "the swap window deliberately vacates the live database path");

        var second = await bed.ActivateAsync(confirm: true);
        second.Outcome.Should().Be(SelfHostedActivationOutcome.InProgress,
            "the durable lease belongs to the in-flight cutover");
        File.Exists(bed.Paths.LiveDatabase).Should().BeFalse(
            "a concurrent request must not materialize an empty database at the vacated live path");

        release.TrySetResult();
        (await first).Outcome.Should().Be(SelfHostedActivationOutcome.Completed);
        await bed.AssertImportedGenerationAsync();
    }

    /// <summary>
    /// Inside its exclusive window the owner holds SQLite locks on the live
    /// database (job transition, lease renewal, the finalized revision bump).
    /// A concurrent request that opened the live database there waited in
    /// SQLite's busy retry with its connection still bound to the pre-cutover
    /// file, slept across the swap and retried against the retained recovery
    /// copy. When that retry met the owner's post-commit write on the activated
    /// database, SQLite took the owner's rollback journal (named after the shared
    /// live path) as hot: it played the activated pages into the recovery copy,
    /// deleted the journal, and the owner's commit failed with a disk I/O error,
    /// "committed but finalization could not complete". This is the
    /// deterministic reproduction of that CI flake: the test holds the lock the
    /// owner holds at that point, and the second request must answer from
    /// durable files without opening or waiting on the live database.
    /// </summary>
    [Fact]
    public async Task SecondRequestInsideTheExclusiveWindow_NeverWaitsOnTheLiveDatabase()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);
        var inWindow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The observer blocks a pool thread inside the window, so run the
        // first activation away from the test's synchronization context.
        var first = Task.Run(() => bed.ActivateAsync(confirm: true, observer: step =>
        {
            if (string.Equals(step, SelfHostedActivationSteps.PhaseDatabaseCheckpointed, StringComparison.Ordinal))
            {
                inWindow.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            }
        }));

        try
        {
            await inWindow.Task.WaitAsync(TimeSpan.FromSeconds(30));

            // Stand in for the write lock the owner itself holds inside its
            // window; nothing else may touch the live database there.
            await using var ownerLock = new SqliteConnection(
                $"Data Source={bed.Paths.LiveDatabase};Mode=ReadWrite;Pooling=False");
            await ownerLock.OpenAsync();
            await using (var begin = ownerLock.CreateCommand())
            {
                begin.CommandText = "BEGIN EXCLUSIVE;";
                await begin.ExecuteNonQueryAsync();
            }

            var second = Task.Run(() => bed.ActivateAsync(confirm: true));
            var answered = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(5)));
            answered.Should().BeSameAs(second,
                "a request inside another owner's exclusive window must answer from durable files, "
                + "not wait on the live database with a connection that outlives the swap");
            (await second).Outcome.Should().Be(SelfHostedActivationOutcome.InProgress,
                "the durable journal shows this job's cutover is in flight");

            await using (var rollback = ownerLock.CreateCommand())
            {
                rollback.CommandText = "ROLLBACK;";
                await rollback.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            release.TrySetResult();
        }

        (await first).Outcome.Should().Be(SelfHostedActivationOutcome.Completed);
        await bed.AssertImportedGenerationAsync();
        (await bed.ReadJobAsync()).State.Should().Be((int)MigrationJobState.Completed);

        var manifest = bed.Manifests.Read(bed.JobId);
        manifest.Should().NotBeNull();
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(bed.Paths.PreviousDatabase(bed.JobId))))
            .Should().BeEquivalentTo(manifest!.DatabaseSha256,
                "the retained recovery copy must be exactly the database the manifest captured");
    }

    /// <summary>
    /// A run whose lease expired must not delete the candidate generation a
    /// successor has already rebuilt. The predecessor's failure path re-checks
    /// ownership inside a fenced transaction before any deletion; without the
    /// unexpired lease it removes nothing and the successor completes.
    /// </summary>
    [Fact]
    public async Task RunThatLostItsLease_CannotDeleteTheSuccessorsCandidateGeneration()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);

        var failure = await FluentActions
            .Awaiting(() => bed.ActivateAsync(confirm: true, observer: step =>
            {
                if (!string.Equals(step, SelfHostedActivationSteps.AfterBuildMedia, StringComparison.Ordinal))
                {
                    return;
                }

                // The run's lease expires, so a successor may acquire the job;
                // here it has already rebuilt its own candidate generation into
                // the shared job-scoped candidate paths.
                bed.Clock.Advance(
                    SelfHostedActivationCoordinator.ActivationLeaseDuration + TimeSpan.FromMinutes(1));
                File.WriteAllText(bed.Paths.CandidateDatabase(bed.JobId), "SUCCESSOR-CANDIDATE");
                var media = bed.Paths.CandidateMedia(bed.JobId);
                Directory.CreateDirectory(media);
                File.WriteAllText(Path.Combine(media, "book.epub"), "SUCCESSOR-MEDIA");

                throw new InvalidOperationException("injected failure after lease loss");
            }))
            .Should().ThrowAsync<MigrationActivationException>();
        failure.Which.Code.Should().Be(MigrationActivationErrorCodes.Failed);

        File.ReadAllText(bed.Paths.CandidateDatabase(bed.JobId)).Should().Be("SUCCESSOR-CANDIDATE",
            "a run that lost its lease must not delete the successor's candidate database");
        File.ReadAllText(Path.Combine(bed.Paths.CandidateMedia(bed.JobId), "book.epub"))
            .Should().Be("SUCCESSOR-MEDIA",
                "a run that lost its lease must not delete the successor's candidate media");

        // The successor acquires the expired lease and completes the activation.
        var successor = await bed.ActivateAsync(confirm: true);
        successor.Outcome.Should().Be(SelfHostedActivationOutcome.Completed);
        await bed.AssertImportedGenerationAsync();
    }
}

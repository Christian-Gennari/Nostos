using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Activation;
using Nostos.Backend.Services.Portability.Migration;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Slice 11: the orphan sweep's fenced cleanup claim against the real retry
/// route. The sweep holds the claim while blocked at its documented seam; a
/// browser retry arriving over HTTP must lose cleanly with the stable lease
/// conflict, change nothing, and the sweep must then complete the deletion. A
/// later retry succeeds normally, proving the claim was released.
/// </summary>
[Collection(ActivationCoordinatorCollection.Name)]
public sealed class MigrationOrphanSweepHttpTests
{
    [Fact]
    public async Task Retry_over_http_loses_cleanly_to_a_held_sweep_claim()
    {
        await using var h = StartHost();
        var now = h.Clock.GetUtcNow().UtcDateTime;
        var jobId = Guid.NewGuid();
        await h.WithDb(async db =>
        {
            db.MigrationJobRecords.Add(new MigrationJobRecord
            {
                Id = jobId,
                Direction = (int)MigrationDirection.Import,
                State = (int)MigrationJobState.Failed,
                RecoveryStatus = (int)MigrationRecoveryStatus.NotRequired,
                CreatedAtUtc = now.AddDays(-2),
                UpdatedAtUtc = now.AddDays(-2),
                IdempotencyKey = "sweep-http-" + jobId.ToString("N"),
                CreationPayloadHash = new string('a', 64),
                ExpiresAtUtc = now.AddDays(1),
                AttemptNumber = 1,
                FailureCode = MigrationActivationErrorCodes.Failed,
                FailureMessage = "seeded failure",
                Version = 1,
            });
            await db.SaveChangesAsync();
        });

        var paths = h.GetService<SelfHostedActivationPaths>();
        var old = now.AddHours(-48);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.CandidateDatabase(jobId))!);
        File.WriteAllText(paths.CandidateDatabase(jobId), "candidate");
        File.SetLastWriteTimeUtc(paths.CandidateDatabase(jobId), old);
        var media = paths.CandidateMedia(jobId);
        Directory.CreateDirectory(media);
        var mediaFile = Path.Combine(media, "book.epub");
        File.WriteAllText(mediaFile, "media");
        File.SetLastWriteTimeUtc(mediaFile, old);
        Directory.SetLastWriteTimeUtc(media, old);
        Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(paths.CandidateDatabase(jobId))!, old);

        var factory = h.GetService<IServiceScopeFactory>();
        await using var scope = factory.CreateAsyncScope();
        var sweep = scope.ServiceProvider.GetRequiredService<SelfHostedActivationOrphanSweep>();
        var claimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sweep.AfterJobClaimForTesting = id =>
        {
            if (id != jobId)
            {
                return;
            }

            claimed.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        };

        var sweepRun = Task.Run(() => sweep.SweepAsync(default));
        try
        {
            await claimed.Task.WaitAsync(TimeSpan.FromSeconds(30));

            var retry = await h.PostJsonAsync(
                $"/api/portability/migration/jobs/{jobId}/retry", new { });
            retry.Status.Should().Be(HttpStatusCode.Conflict);
            retry.Body.RootElement.GetProperty("error").GetString().Should().Be("migration_lease_conflict");
            retry.Body.Dispose();

            (await h.JobStatusAsync(jobId)).RootElement.GetProperty("job").GetProperty("state")
                .GetString().Should().Be("Failed", "the losing retry must change nothing");
            File.Exists(paths.CandidateDatabase(jobId)).Should().BeTrue(
                "the sweep has not deleted anything while it is paused before deletion");
        }
        finally
        {
            release.TrySetResult();
        }

        var result = await sweepRun;
        result.RemovedEntries.Should().BeGreaterThanOrEqualTo(1);
        File.Exists(paths.CandidateDatabase(jobId)).Should().BeFalse("the sweep completes its claimed deletion");
        Directory.Exists(media).Should().BeFalse();

        // The claim is released after the sweep's deletion, so the next retry
        // is an ordinary success.
        var later = await h.PostJsonAsync(
            $"/api/portability/migration/jobs/{jobId}/retry", new { });
        later.Status.Should().Be(HttpStatusCode.OK);
        later.Body.RootElement.GetProperty("job").GetProperty("state").GetString().Should().Be("Pending");
        later.Body.Dispose();
    }

    private static MigrationHttpHarness StartHost()
    {
        var harness = new MigrationHttpHarness
        {
            UseRealPhaseHandlers = true,
            ConfigureServices = services =>
                services.AddHostedService(sp => sp.GetRequiredService<SelfHostedActivationDispatcher>()),
        };
        return harness.Start();
    }
}

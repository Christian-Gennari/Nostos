using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data.Models;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// The live-file byte assertion observes the process-global SQLite connection
/// pool: another test's <c>SqliteConnection.ClearAllPools()</c> can close the
/// last pooled connection to this database and checkpoint its WAL, changing the
/// main file for reasons unrelated to the builder. This test therefore runs in
/// a non-parallel collection so no other collection can touch the pool while it
/// holds its anchor connection.
/// </summary>
[Collection(ActivationLiveFileIsolationCollection.Name)]
public sealed class ActivationLiveFileIsolationTests
{
    [Fact]
    public async Task Build_LeavesLiveDatabaseFileByteForByteUnchanged_UnderConcurrentReadsAndWrites()
    {
        await using var fixture = await ActivationBuildFixture.CreateAsync();
        // WAL plus disabled autocheckpoint means committed live writes land in
        // the WAL; the main file must stay byte-identical unless someone
        // checkpoints it. The anchor connection also keeps the WAL alive.
        SqliteConnection.ClearAllPools();
        using var anchor = new SqliteConnection(
            $"Data Source={fixture.Paths.LiveDatabase};Pooling=False");
        anchor.Open();
        Execute(anchor, "PRAGMA journal_mode=WAL;");
        Execute(anchor, "PRAGMA wal_autocheckpoint=0;");
        var liveBefore = ActivationBuildFixture.Sha256(fixture.Paths.LiveDatabase);

        var reachedPortable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = fixture.CreateBuilder();
        builder.AfterBuildStepForTesting = step =>
        {
            if (step == "data")
            {
                reachedPortable.TrySetResult();
            }
        };

        var build = builder.BuildPortableCandidateAsync(fixture.JobId, fixture.Prepared);
        await reachedPortable.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var writes = 0;
        for (var index = 0; index < 10; index++)
        {
            await using (var live = fixture.OpenLive())
            {
                // Keep every committing connection from auto-checkpointing the
                // WAL: only the builder must be able to touch the main file.
                live.Database.OpenConnection();
                live.Database.ExecuteSqlRaw("PRAGMA wal_autocheckpoint=0;");
                _ = await live.BackupRecords.CountAsync();
                live.BackupRecords.Add(new BackupRecord
                {
                    Id = Guid.NewGuid(),
                    CreatedAt = ActivationBuildFixture.FixedNow.AddMinutes(index),
                    SizeBytes = 100 + index,
                    Status = BackupStatus.Completed,
                    Provider = BackupProvider.Local,
                    IncludeBookFiles = false,
                });
                await live.SaveChangesAsync();
            }

            writes++;
        }

        await build;
        writes.Should().Be(10);

        ActivationBuildFixture.Sha256(fixture.Paths.LiveDatabase)
            .Should().Be(liveBefore, "the builder must never write or checkpoint the live main file");

        await using (var live = fixture.OpenLive())
        {
            (await live.BackupRecords.CountAsync())
                .Should().Be(ActivationBuildFixture.LiveBackupCount + writes,
                    "concurrent live writes must have committed while the build ran");
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ActivationLiveFileIsolationCollection
{
    public const string Name = "ActivationLiveFileIsolation";
}

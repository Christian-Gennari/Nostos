using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Portability;
using Xunit;

namespace Nostos.Backend.Tests.Data;

// Slice 2 of issue #679: the five operational transfer tables must exist with
// the exact provider-portable shape the plan specifies. These tests exercise
// both the EF model metadata and a real SQLite schema, including the integer
// concurrency tokens and every unique/check constraint.
public sealed class MigrationTransferRecordsSchemaTests : IDisposable
{
    private readonly List<string> _databasePaths = new();

    [Fact]
    public void Model_declares_the_operational_tables_columns_indexes_and_checks()
    {
        using var db = CreateInMemoryContext();
        var model = db.GetService<IDesignTimeModel>().Model;

        var job = model.FindEntityType(typeof(MigrationJobRecord))!;
        var session = model.FindEntityType(typeof(MigrationSessionRecord))!;
        var receipt = model.FindEntityType(typeof(MigrationChunkReceiptRecord))!;
        var artifact = model.FindEntityType(typeof(MigrationExportArtifactRecord))!;
        var reservation = model.FindEntityType(typeof(MigrationStorageReservationRecord))!;

        job.GetTableName().Should().Be("MigrationJobRecords");
        session.GetTableName().Should().Be("MigrationSessionRecords");
        receipt.GetTableName().Should().Be("MigrationChunkReceiptRecords");
        artifact.GetTableName().Should().Be("MigrationExportArtifactRecords");
        reservation.GetTableName().Should().Be("MigrationStorageReservations");

        job.GetProperties().Select(p => p.Name).Should().BeEquivalentTo(
        [
            "Id", "Direction", "State", "RecoveryStatus", "ProgressPhase",
            "ProgressBytesProcessed", "ProgressTotalBytes", "ProgressCompletedChunks",
            "ProgressTotalChunks", "ProgressMessage", "CreatedAtUtc", "UpdatedAtUtc",
            "HeartbeatAtUtc", "MigrationLeaseToken", "LeaseExpiresAtUtc", "IdempotencyKey",
            "CreationPayloadHash", "FailureCode", "FailureMessage", "ExpiresAtUtc",
            "AttemptNumber", "DestinationRevision", "PreparedStagingId",
            "PreparedImportMetadataJson", "ReservedStorageBytes", "ReservationId",
            "CancellationReason", "CancelledAtUtc", "CompletedAtUtc", "Version",
        ]);

        session.GetProperties().Select(p => p.Name).Should().BeEquivalentTo(
        [
            "Id", "JobId", "Purpose", "State", "TotalBytes", "ChunkSize", "TotalChunks",
            "FileIdentitySizeBytes", "FileIdentitySha256", "ClientFingerprint",
            "IdempotencyKey", "CreationPayloadHash", "ReceivedBytes", "CreatedAtUtc",
            "UpdatedAtUtc", "ExpiresAtUtc", "CompletedAtUtc", "StorageKey", "Version",
        ]);

        receipt.GetProperties().Select(p => p.Name).Should().BeEquivalentTo(
            ["SessionId", "ChunkIndex", "OffsetBytes", "LengthBytes", "Sha256", "ReceivedAtUtc"]);

        artifact.GetProperties().Select(p => p.Name).Should().BeEquivalentTo(
        [
            "JobId", "State", "StorageKey", "FileName", "ContentType", "SizeBytes",
            "Sha256", "CreatedAtUtc", "AvailableAtUtc", "ExpiresAtUtc", "DeletedAtUtc",
            "Version",
        ]);

        reservation.GetProperties().Select(p => p.Name).Should().BeEquivalentTo(
        [
            "Id", "Purpose", "ReservedBytes", "MaterializedBytes", "CreatedAtUtc",
            "ExpiresAtUtc", "ClaimedJobId", "ReleasedAtUtc", "Version",
        ]);

        AssertColumn(job, "Id", typeof(Guid), nullable: false);
        AssertColumn(job, "Direction", typeof(int), nullable: false);
        AssertColumn(job, "ProgressTotalBytes", typeof(long?), nullable: true);
        AssertColumn(job, "ProgressMessage", typeof(string), nullable: true, maxLength: 512);
        AssertColumn(job, "MigrationLeaseToken", typeof(string), nullable: true, maxLength: 128);
        AssertColumn(job, "IdempotencyKey", typeof(string), nullable: false, maxLength: 128);
        AssertColumn(job, "CreationPayloadHash", typeof(string), nullable: false, maxLength: 64);
        AssertColumn(job, "FailureMessage", typeof(string), nullable: true, maxLength: 1024);
        AssertColumn(job, "CreatedAtUtc", typeof(DateTimeOffset), nullable: false);
        AssertColumn(job, "PreparedImportMetadataJson", typeof(string), nullable: true);
        AssertColumn(job, "Version", typeof(long), nullable: false);

        AssertColumn(session, "JobId", typeof(Guid), nullable: false);
        AssertColumn(session, "TotalBytes", typeof(long), nullable: false);
        AssertColumn(session, "ChunkSize", typeof(int), nullable: false);
        AssertColumn(session, "FileIdentitySha256", typeof(string), nullable: false, maxLength: 64);
        AssertColumn(session, "ClientFingerprint", typeof(string), nullable: true, maxLength: 256);
        AssertColumn(session, "StorageKey", typeof(string), nullable: false, maxLength: 512);
        AssertColumn(session, "Version", typeof(long), nullable: false);

        AssertColumn(receipt, "ChunkIndex", typeof(int), nullable: false);
        AssertColumn(receipt, "OffsetBytes", typeof(long), nullable: false);
        AssertColumn(receipt, "LengthBytes", typeof(int), nullable: false);
        AssertColumn(receipt, "Sha256", typeof(string), nullable: false, maxLength: 64);
        AssertColumn(receipt, "ReceivedAtUtc", typeof(DateTimeOffset), nullable: false);

        AssertColumn(artifact, "JobId", typeof(Guid), nullable: false);
        AssertColumn(artifact, "StorageKey", typeof(string), nullable: false, maxLength: 512);
        AssertColumn(artifact, "FileName", typeof(string), nullable: false, maxLength: 255);
        AssertColumn(artifact, "ContentType", typeof(string), nullable: false, maxLength: 128);
        AssertColumn(artifact, "Sha256", typeof(string), nullable: true, maxLength: 64);
        AssertColumn(artifact, "Version", typeof(long), nullable: false);

        AssertColumn(reservation, "ReservedBytes", typeof(long), nullable: false);
        AssertColumn(reservation, "MaterializedBytes", typeof(long), nullable: false);
        AssertColumn(reservation, "ClaimedJobId", typeof(Guid?), nullable: true);
        AssertColumn(reservation, "ReleasedAtUtc", typeof(DateTimeOffset?), nullable: true);
        AssertColumn(reservation, "Version", typeof(long), nullable: false);

        receipt.FindPrimaryKey()!.Properties.Select(p => p.Name)
            .Should().Equal("SessionId", "ChunkIndex");

        job.GetIndexes().Select(DescribeIndex).Should().BeEquivalentTo(
            "IdempotencyKey unique", "State,LeaseExpiresAtUtc", "ExpiresAtUtc", "UpdatedAtUtc");
        session.GetIndexes().Select(DescribeIndex).Should().BeEquivalentTo(
            "JobId,IdempotencyKey unique", "JobId,State", "ExpiresAtUtc");
        artifact.GetIndexes().Select(DescribeIndex).Should().BeEquivalentTo("State,ExpiresAtUtc");
        receipt.GetIndexes().Should().BeEmpty();

        job.GetCheckConstraints().Select(c => c.Name).Should().BeEquivalentTo(
            "CK_MigrationJobRecords_ReservedStorageBytes",
            "CK_MigrationJobRecords_AttemptNumber",
            "CK_MigrationJobRecords_IdempotencyKey",
            "CK_MigrationJobRecords_LeaseToken");
        session.GetCheckConstraints().Select(c => c.Name).Should().BeEquivalentTo(
            "CK_MigrationSessionRecords_TotalBytes",
            "CK_MigrationSessionRecords_ChunkSize",
            "CK_MigrationSessionRecords_TotalChunks",
            "CK_MigrationSessionRecords_FileIdentitySize",
            "CK_MigrationSessionRecords_ReceivedBytes");
        receipt.GetCheckConstraints().Select(c => c.Name).Should().Equal(
            "CK_MigrationChunkReceiptRecords_Bounds");
        artifact.GetCheckConstraints().Should().BeEmpty();
        reservation.GetCheckConstraints().Should().BeEmpty();

        AssertCascadeForeignKey(session, "JobId", typeof(MigrationJobRecord));
        AssertCascadeForeignKey(artifact, "JobId", typeof(MigrationJobRecord));
        AssertCascadeForeignKey(receipt, "SessionId", typeof(MigrationSessionRecord));

        job.FindProperty("Version")!.IsConcurrencyToken.Should().BeTrue();
        session.FindProperty("Version")!.IsConcurrencyToken.Should().BeTrue();
        artifact.FindProperty("Version")!.IsConcurrencyToken.Should().BeTrue();
        reservation.FindProperty("Version")!.IsConcurrencyToken.Should().BeTrue();
        receipt.FindProperty("ChunkIndex")!.IsConcurrencyToken.Should().BeFalse();
    }

    [Fact]
    public async Task Created_sqlite_schema_contains_the_new_tables_indexes_and_checks()
    {
        var path = NewDatabasePath();
        var options = FileOptions(path);
        await using (var db = new NostosDbContext(options))
        {
            (await db.Database.EnsureCreatedAsync()).Should().BeTrue();
        }

        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();

        foreach (var table in new[]
                 {
                     "MigrationJobRecords",
                     "MigrationSessionRecords",
                     "MigrationChunkReceiptRecords",
                     "MigrationExportArtifactRecords",
                     "MigrationStorageReservations",
                 })
        {
            var sql = await ScalarAsync(
                connection,
                "SELECT \"sql\" FROM \"sqlite_master\" WHERE \"type\" = 'table' AND \"name\" = @name",
                ("@name", table));
            sql.Should().NotBeNull($"table {table} must be created by the model");

            var indexes = await ColumnAsync(
                connection,
                "SELECT \"name\" FROM \"sqlite_master\" WHERE \"type\" = 'index' AND \"tbl_name\" = @name",
                ("@name", table));
            indexes.Should().NotBeEmpty($"SQLite must create the declared indexes for {table}");
        }

        var jobSql = await ScalarAsync(
            connection,
            "SELECT \"sql\" FROM \"sqlite_master\" WHERE \"type\" = 'table' AND \"name\" = 'MigrationJobRecords'",
            ("@name", "MigrationJobRecords"));
        jobSql.Should().Contain("CK_MigrationJobRecords_ReservedStorageBytes");
        jobSql.Should().Contain("CK_MigrationJobRecords_AttemptNumber");
        jobSql.Should().Contain("CK_MigrationJobRecords_IdempotencyKey");
        jobSql.Should().Contain("CK_MigrationJobRecords_LeaseToken");

        var sessionSql = await ScalarAsync(
            connection,
            "SELECT \"sql\" FROM \"sqlite_master\" WHERE \"type\" = 'table' AND \"name\" = 'MigrationSessionRecords'",
            ("@name", "MigrationSessionRecords"));
        sessionSql.Should().Contain("CK_MigrationSessionRecords_ChunkSize");
        sessionSql.Should().Contain("CK_MigrationSessionRecords_FileIdentitySize");
        sessionSql.Should().Contain("CK_MigrationSessionRecords_ReceivedBytes");

        (await ColumnAsync(
                connection,
                "SELECT \"name\" FROM \"sqlite_master\" WHERE \"type\" = 'index' AND \"name\" = 'IX_MigrationJobRecords_IdempotencyKey'",
                ("@name", "IX_MigrationJobRecords_IdempotencyKey")))
            .Should().Equal("IX_MigrationJobRecords_IdempotencyKey");

        (await ColumnAsync(
                connection,
                "SELECT \"name\" FROM \"sqlite_master\" WHERE \"type\" = 'index' AND \"name\" = 'IX_MigrationSessionRecords_JobId_IdempotencyKey'",
                ("@name", "IX_MigrationSessionRecords_JobId_IdempotencyKey")))
            .Should().Equal("IX_MigrationSessionRecords_JobId_IdempotencyKey");
    }

    [Fact]
    public async Task Concurrency_token_rejects_a_stale_update()
    {
        var options = await CreateFileDatabaseAsync();
        Guid jobId;
        await using (var seed = new NostosDbContext(options))
        {
            var job = NewJob("concurrency-key");
            seed.MigrationJobRecords.Add(job);
            await seed.SaveChangesAsync();
            jobId = job.Id;
        }

        await using var first = new NostosDbContext(options);
        await using var stale = new NostosDbContext(options);
        var firstJob = await first.MigrationJobRecords.SingleAsync(j => j.Id == jobId);
        var staleJob = await stale.MigrationJobRecords.SingleAsync(j => j.Id == jobId);

        firstJob.ProgressBytesProcessed = 100;
        firstJob.Version += 1;
        await first.SaveChangesAsync();

        staleJob.ProgressBytesProcessed = 200;
        staleJob.Version += 1;
        var save = () => stale.SaveChangesAsync();
        await save.Should().ThrowAsync<DbUpdateConcurrencyException>(
            "the integer Version token must reject a stale writer");
    }

    [Fact]
    public async Task Unique_job_idempotency_key_rejects_a_duplicate()
    {
        var options = await CreateFileDatabaseAsync();
        await using (var seed = new NostosDbContext(options))
        {
            seed.MigrationJobRecords.Add(NewJob("reused-key"));
            await seed.SaveChangesAsync();
        }

        await using var db = new NostosDbContext(options);
        db.MigrationJobRecords.Add(NewJob("reused-key"));
        var save = () => db.SaveChangesAsync();
        await save.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Session_idempotency_key_is_unique_only_within_its_job()
    {
        var options = await CreateFileDatabaseAsync();
        Guid jobA;
        Guid jobB;
        await using (var seed = new NostosDbContext(options))
        {
            var first = NewJob("job-a");
            var second = NewJob("job-b");
            seed.MigrationJobRecords.AddRange(first, second);
            await seed.SaveChangesAsync();
            jobA = first.Id;
            jobB = second.Id;
        }

        await using (var db = new NostosDbContext(options))
        {
            db.MigrationSessionRecords.AddRange(
                NewSession(jobA, "shared-session-key"),
                NewSession(jobB, "shared-session-key"));
            await db.SaveChangesAsync();
        }

        await using (var db = new NostosDbContext(options))
        {
            db.MigrationSessionRecords.Add(NewSession(jobA, "shared-session-key"));
            var save = () => db.SaveChangesAsync();
            await save.Should().ThrowAsync<DbUpdateException>(
                "a session key is unique within its parent job");
        }
    }

    [Fact]
    public async Task Deleting_a_job_cascades_to_sessions_receipts_and_artifacts()
    {
        var options = await CreateFileDatabaseAsync();
        Guid jobId;
        Guid sessionId;
        await using (var seed = new NostosDbContext(options))
        {
            var job = NewJob("cascade-job");
            var session = NewSession(job.Id, "cascade-session");
            seed.MigrationJobRecords.Add(job);
            seed.MigrationSessionRecords.Add(session);
            seed.MigrationChunkReceiptRecords.AddRange(
                NewReceipt(session.Id, 0),
                NewReceipt(session.Id, 1));
            seed.MigrationExportArtifactRecords.Add(NewArtifact(job.Id));
            await seed.SaveChangesAsync();
            jobId = job.Id;
            sessionId = session.Id;
        }

        await using (var db = new NostosDbContext(options))
        {
            var job = await db.MigrationJobRecords.SingleAsync(j => j.Id == jobId);
            db.MigrationJobRecords.Remove(job);
            await db.SaveChangesAsync();
        }

        await using (var db = new NostosDbContext(options))
        {
            (await db.MigrationSessionRecords.CountAsync()).Should().Be(0);
            (await db.MigrationChunkReceiptRecords.CountAsync()).Should().Be(0);
            (await db.MigrationExportArtifactRecords.CountAsync()).Should().Be(0);
        }

        // A standalone session also cascades to its receipts.
        Guid standaloneSessionId;
        await using (var seed = new NostosDbContext(options))
        {
            var job = NewJob("standalone-job");
            var session = NewSession(job.Id, "standalone-session");
            seed.MigrationJobRecords.Add(job);
            seed.MigrationSessionRecords.Add(session);
            seed.MigrationChunkReceiptRecords.Add(NewReceipt(session.Id, 0));
            await seed.SaveChangesAsync();
            standaloneSessionId = session.Id;
        }

        await using (var db = new NostosDbContext(options))
        {
            var session = await db.MigrationSessionRecords.SingleAsync(s => s.Id == standaloneSessionId);
            db.MigrationSessionRecords.Remove(session);
            await db.SaveChangesAsync();
        }

        await using (var db = new NostosDbContext(options))
        {
            (await db.MigrationChunkReceiptRecords.CountAsync()).Should().Be(0);
        }
    }

    [Fact]
    public async Task Composite_chunk_receipt_primary_key_rejects_a_duplicate_index()
    {
        var options = await CreateFileDatabaseAsync();
        Guid sessionId;
        await using (var seed = new NostosDbContext(options))
        {
            var job = NewJob("chunk-duplicate-job");
            var session = NewSession(job.Id, "chunk-duplicate-session");
            seed.MigrationJobRecords.Add(job);
            seed.MigrationSessionRecords.Add(session);
            await seed.SaveChangesAsync();
            sessionId = session.Id;
        }

        await using (var db = new NostosDbContext(options))
        {
            db.MigrationChunkReceiptRecords.Add(NewReceipt(sessionId, 0));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            db.MigrationChunkReceiptRecords.Add(NewReceipt(sessionId, 0));
            var save = () => db.SaveChangesAsync();
            await save.Should().ThrowAsync<DbUpdateException>();
        }
    }

    [Fact]
    public async Task Sqlite_check_constraints_reject_invalid_job_rows()
    {
        await AssertJobRejectedAsync(job => job.ReservedStorageBytes = -1);
        await AssertJobRejectedAsync(job => job.AttemptNumber = 0);
        await AssertJobRejectedAsync(job => job.IdempotencyKey = string.Empty);
    }

    [Fact]
    public async Task Sqlite_check_constraints_reject_invalid_session_rows()
    {
        await AssertSessionRejectedAsync(session => session.TotalBytes = 0);
        await AssertSessionRejectedAsync(session => session.ChunkSize = MigrationContractLimits.MinChunkBytes - 1);
        await AssertSessionRejectedAsync(session => session.TotalChunks = 0);
        await AssertSessionRejectedAsync(session => session.FileIdentitySizeBytes = session.TotalBytes - 1);
        await AssertSessionRejectedAsync(session => session.ReceivedBytes = session.TotalBytes + 1);
    }

    [Fact]
    public async Task Sqlite_check_constraints_reject_invalid_chunk_receipt_rows()
    {
        await AssertReceiptRejectedAsync(receipt => receipt.LengthBytes = 0);
        await AssertReceiptRejectedAsync(receipt => receipt.ChunkIndex = -1);
        await AssertReceiptRejectedAsync(receipt => receipt.OffsetBytes = -1);
    }

    private async Task AssertJobRejectedAsync(Action<MigrationJobRecord> mutate)
    {
        var options = await CreateFileDatabaseAsync();
        await using var db = new NostosDbContext(options);
        var job = NewJob("invalid-job-" + Guid.NewGuid().ToString("N"));
        mutate(job);
        db.MigrationJobRecords.Add(job);
        var save = () => db.SaveChangesAsync();
        await save.Should().ThrowAsync<DbUpdateException>();
    }

    private async Task AssertSessionRejectedAsync(Action<MigrationSessionRecord> mutate)
    {
        var options = await CreateFileDatabaseAsync();
        await using var db = new NostosDbContext(options);
        var job = NewJob("invalid-session-parent-" + Guid.NewGuid().ToString("N"));
        db.MigrationJobRecords.Add(job);
        await db.SaveChangesAsync();

        var session = NewSession(job.Id, "invalid-session-" + Guid.NewGuid().ToString("N"));
        mutate(session);
        db.MigrationSessionRecords.Add(session);
        var save = () => db.SaveChangesAsync();
        await save.Should().ThrowAsync<DbUpdateException>();
    }

    private async Task AssertReceiptRejectedAsync(Action<MigrationChunkReceiptRecord> mutate)
    {
        var options = await CreateFileDatabaseAsync();
        await using var db = new NostosDbContext(options);
        var job = NewJob("invalid-receipt-parent-" + Guid.NewGuid().ToString("N"));
        var session = NewSession(job.Id, "invalid-receipt-session-" + Guid.NewGuid().ToString("N"));
        db.MigrationJobRecords.Add(job);
        db.MigrationSessionRecords.Add(session);
        await db.SaveChangesAsync();

        var receipt = NewReceipt(session.Id, 0);
        mutate(receipt);
        db.MigrationChunkReceiptRecords.Add(receipt);
        var save = () => db.SaveChangesAsync();
        await save.Should().ThrowAsync<DbUpdateException>();
    }

    private static MigrationJobRecord NewJob(string idempotencyKey) => new()
    {
        Direction = (int)MigrationDirection.Import,
        State = (int)MigrationJobState.Pending,
        RecoveryStatus = (int)MigrationRecoveryStatus.NotRequired,
        ProgressPhase = (int)MigrationProgressPhase.Pending,
        IdempotencyKey = idempotencyKey,
        CreationPayloadHash = new string('a', 64),
        CreatedAtUtc = DateTimeOffset.UtcNow,
        UpdatedAtUtc = DateTimeOffset.UtcNow,
        ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(7),
        AttemptNumber = 1,
        ReservedStorageBytes = 32 * 1024 * 1024,
    };

    private static MigrationSessionRecord NewSession(Guid jobId, string idempotencyKey) => new()
    {
        JobId = jobId,
        Purpose = (int)MigrationSessionPurpose.Import,
        State = (int)MigrationSessionState.Created,
        TotalBytes = 32 * 1024 * 1024,
        ChunkSize = MigrationContractLimits.DefaultChunkBytes,
        TotalChunks = 2,
        FileIdentitySizeBytes = 32 * 1024 * 1024,
        FileIdentitySha256 = new string('b', 64),
        IdempotencyKey = idempotencyKey,
        CreationPayloadHash = new string('c', 64),
        ReceivedBytes = 0,
        ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(24),
        StorageKey = $"uploads/session-{Guid.NewGuid():N}/archive.part",
    };

    private static MigrationChunkReceiptRecord NewReceipt(Guid sessionId, int chunkIndex) => new()
    {
        SessionId = sessionId,
        ChunkIndex = chunkIndex,
        OffsetBytes = chunkIndex * 16L * 1024 * 1024,
        LengthBytes = 16 * 1024 * 1024,
        Sha256 = new string('d', 64),
        ReceivedAtUtc = DateTimeOffset.UtcNow,
    };

    private static MigrationExportArtifactRecord NewArtifact(Guid jobId) => new()
    {
        JobId = jobId,
        State = (int)MigrationExportArtifactState.Preparing,
        StorageKey = $"exports/job-{Guid.NewGuid():N}/library.nostos",
        FileName = "library.nostos",
        ContentType = "application/vnd.nostos.portable+zip",
        CreatedAtUtc = DateTimeOffset.UtcNow,
        ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(7),
    };

    private static void AssertColumn(
        IEntityType entityType,
        string name,
        Type clrType,
        bool nullable,
        int? maxLength = null)
    {
        var property = entityType.FindProperty(name);
        property.Should().NotBeNull($"{entityType.DisplayName()}.{name} must be mapped");
        if (clrType == typeof(string))
        {
            property!.ClrType.Should().Be(typeof(string));
            property.IsNullable.Should().Be(nullable);
            property.GetMaxLength().Should().Be(maxLength);
        }
        else
        {
            property!.ClrType.Should().Be(clrType);
            property.IsNullable.Should().Be(nullable);
        }
    }

    private static void AssertCascadeForeignKey(
        IEntityType dependent,
        string foreignKeyProperty,
        Type principal)
    {
        var foreignKey = dependent.GetForeignKeys().Single(fk =>
            fk.Properties.Select(p => p.Name).SequenceEqual([foreignKeyProperty]));
        foreignKey.PrincipalEntityType.ClrType.Should().Be(principal);
        foreignKey.DeleteBehavior.Should().Be(DeleteBehavior.Cascade);
    }

    private static string DescribeIndex(IIndex index) =>
        string.Join(",", index.Properties.Select(p => p.Name)) +
        (index.IsUnique ? " unique" : string.Empty);

    private static NostosDbContext CreateInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        return new NostosDbContext(options);
    }

    private DbContextOptions<NostosDbContext> FileOptions(string path) =>
        new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;

    private async Task<DbContextOptions<NostosDbContext>> CreateFileDatabaseAsync()
    {
        var options = FileOptions(NewDatabasePath());
        await using var db = new NostosDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return options;
    }

    private string NewDatabasePath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nostos-679-schema-{Guid.NewGuid():N}.db");
        _databasePaths.Add(path);
        return path;
    }

    private static async Task<string?> ScalarAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task<List<string>> ColumnAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        var values = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            values.Add(reader.GetString(0));
        return values;
    }

    public void Dispose()
    {
        foreach (var path in _databasePaths)
        {
            foreach (var suffix in new[] { "", "-shm", "-wal" })
            {
                try
                {
                    File.Delete(path + suffix);
                }
                catch (IOException)
                {
                    // Best-effort cleanup only.
                }
            }
        }
    }
}

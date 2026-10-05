using FluentAssertions;
using Microsoft.Data.Sqlite;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Activation;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

public sealed class SelfHostedSqliteLifecycleTests
{
    [Fact]
    public async Task QuiesceLive_WithPooledConnectionsAndAnActiveWal_LeavesTheCommittedWritesInTheSingleMainFile()
    {
        using var env = new LifecycleEnvironment();
        env.Execute("PRAGMA journal_mode=WAL;");
        env.Execute("CREATE TABLE Marker(Id INTEGER PRIMARY KEY, Value TEXT NOT NULL);");

        // A pooled physical connection keeps a handle (and WAL autocheckpoint
        // disabled on that connection) while its committed write stays in the WAL.
        using (var pooled = new SqliteConnection($"Data Source={env.Live}"))
        {
            pooled.Open();
            env.ExecuteOn(pooled, "PRAGMA wal_autocheckpoint=0;");
            env.ExecuteOn(pooled, "INSERT INTO Marker(Value) VALUES ('committed');");
        }

        var wal = env.Live + "-wal";
        File.Exists(wal).Should().BeTrue();
        new FileInfo(wal).Length.Should().BeGreaterThan(0, "the write must be WAL-resident before quiesce");

        var gate = new LibraryMaintenanceCoordinator();
        var lifecycle = new SelfHostedSqliteLifecycle(env.Paths, gate);
        await using (var lease = await gate.EnterExclusiveAsync(LibraryMaintenanceReason.Activation))
        {
            lifecycle.QuiesceLive(lease);
            lifecycle.QuiesceLive(lease); // repeatable
        }

        File.Exists(wal).Should().BeFalse();
        File.Exists(env.Live + "-shm").Should().BeFalse();

        // Open the main file alone: the committed write survives without a WAL.
        using var direct = new SqliteConnection($"Data Source={env.Live};Mode=ReadOnly;Pooling=False");
        direct.Open();
        env.ExecuteScalar(direct, "SELECT Value FROM Marker;").Should().Be("committed");
    }

    [Fact]
    public void ReadOnlyAndReadWriteWithoutCreate_FailOnAMissingDatabase_AndNeverMaterializeIt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nostos-no-create-{Guid.NewGuid():N}.db");
        var readOnly = SelfHostedSqliteFile.ConnectionString(path, readOnly: true, pooling: false);
        var readWrite = SelfHostedSqliteFile.ConnectionString(
            path, readOnly: false, pooling: false, create: false);

        Action openReadOnly = () => { using var connection = new SqliteConnection(readOnly); connection.Open(); };
        Action openReadWrite = () => { using var connection = new SqliteConnection(readWrite); connection.Open(); };
        openReadOnly.Should().Throw<SqliteException>();
        openReadWrite.Should().Throw<SqliteException>();
        File.Exists(path).Should().BeFalse(
            "no activation-side opener may create a database the cutover has vacated");
    }

    [Fact]
    public async Task QuiesceLive_OnADeleteJournalDatabase_IsANoOpThatSucceedsAndRepeats()
    {
        using var env = new LifecycleEnvironment();
        env.Execute("CREATE TABLE Marker(Value TEXT NOT NULL); INSERT INTO Marker(Value) VALUES ('kept');");

        var gate = new LibraryMaintenanceCoordinator();
        var lifecycle = new SelfHostedSqliteLifecycle(env.Paths, gate);
        await using (var lease = await gate.EnterExclusiveAsync(LibraryMaintenanceReason.Activation))
        {
            lifecycle.QuiesceLive(lease);
            lifecycle.QuiesceLive(lease);
        }

        File.Exists(env.Live + "-wal").Should().BeFalse();
        File.Exists(env.Live + "-shm").Should().BeFalse();
        env.ExecuteScalar("SELECT Value FROM Marker;").Should().Be("kept");
    }

    [Fact]
    public async Task QuiesceLive_ClearsPoolsOpenedWithADifferentButEquivalentConnectionString()
    {
        using var env = new LifecycleEnvironment();
        env.Execute("PRAGMA journal_mode=WAL;");
        env.Execute("CREATE TABLE Marker(Id INTEGER PRIMARY KEY, Value TEXT NOT NULL);");

        // Exact-string pool lookup would miss this spelling; the global pool
        // primitive must still release the idle handle.
        using (var pooled = new SqliteConnection($"Data Source={env.Live};Default Timeout=30"))
        {
            pooled.Open();
            env.ExecuteOn(pooled, "INSERT INTO Marker(Value) VALUES ('committed');");
        }

        var gate = new LibraryMaintenanceCoordinator();
        var lifecycle = new SelfHostedSqliteLifecycle(env.Paths, gate);
        await using (var lease = await gate.EnterExclusiveAsync(LibraryMaintenanceReason.Activation))
        {
            lifecycle.QuiesceLive(lease);
        }

        File.Exists(env.Live + "-wal").Should().BeFalse();
        File.Exists(env.Live + "-shm").Should().BeFalse();

        // The live file itself can now be renamed without a leaked handle.
        var moved = env.Live + ".moved";
        File.Move(env.Live, moved);
        File.Move(moved, env.Live);
        env.ExecuteScalar("SELECT Value FROM Marker;").Should().Be("committed");
    }

    [Fact]
    public async Task QuiesceLive_WithoutACurrentExclusiveLease_IsRejectedWithoutSideEffects()
    {
        using var env = new LifecycleEnvironment();
        env.Execute("CREATE TABLE Marker(Value TEXT NOT NULL); INSERT INTO Marker(Value) VALUES ('kept');");
        var gate = new LibraryMaintenanceCoordinator();
        var lifecycle = new SelfHostedSqliteLifecycle(env.Paths, gate);

        Action noLease = () => lifecycle.QuiesceLive(null!);
        noLease.Should().Throw<InvalidOperationException>();

        var foreignGate = new LibraryMaintenanceCoordinator();
        await using (var foreign = await foreignGate.EnterExclusiveAsync(LibraryMaintenanceReason.Activation))
        {
            Action foreignLease = () => lifecycle.QuiesceLive(foreign);
            foreignLease.Should().Throw<InvalidOperationException>();
        }

        var disposedLease = await gate.EnterExclusiveAsync(LibraryMaintenanceReason.Activation);
        await disposedLease.DisposeAsync();
        Action disposed = () => lifecycle.QuiesceLive(disposedLease);
        disposed.Should().Throw<InvalidOperationException>();

        env.ExecuteScalar("SELECT Value FROM Marker;").Should().Be("kept");
    }

    [Fact]
    public async Task QuiesceLive_WithAHeldReaderPinningTheWal_FailsClosed_AndTheLiveDatabaseStaysUsable()
    {
        using var env = new LifecycleEnvironment();
        env.Execute("CREATE TABLE Marker(Id INTEGER PRIMARY KEY, Value TEXT NOT NULL);");
        env.Execute("PRAGMA journal_mode=WAL;");
        env.Execute("INSERT INTO Marker(Value) VALUES ('first');");

        using var reader = new SqliteConnection($"Data Source={env.Live};Pooling=False");
        reader.Open();
        env.ExecuteOn(reader, "BEGIN;");
        env.ExecuteScalar(reader, "SELECT COUNT(*) FROM Marker;").Should().Be(1L);

        // A writer adds committed frames while the reader snapshot pins the WAL.
        env.Execute("INSERT INTO Marker(Value) VALUES ('second');");
        var wal = env.Live + "-wal";
        new FileInfo(wal).Length.Should().BeGreaterThan(0);

        var gate = new LibraryMaintenanceCoordinator();
        var lifecycle = new SelfHostedSqliteLifecycle(env.Paths, gate);
        await using (var lease = await gate.EnterExclusiveAsync(LibraryMaintenanceReason.Activation))
        {
            Action quiesce = () => lifecycle.QuiesceLive(lease);
            quiesce.Should().Throw<MigrationActivationException>()
                .Which.Code.Should().Be(MigrationActivationErrorCodes.Busy);
        }

        new FileInfo(wal).Length.Should().BeGreaterThan(0, "committed frames must never be discarded");

        env.ExecuteOn(reader, "COMMIT;");
        env.Execute("INSERT INTO Marker(Value) VALUES ('third');");
        env.ExecuteScalar("SELECT COUNT(*) FROM Marker;").Should().Be(3L);
    }

    [Fact]
    public async Task ReopenActivated_AfterAFileSwitch_NewConnectionsSeeTheNewDatabase_AndRepeatable()
    {
        using var env = new LifecycleEnvironment();
        env.Execute("CREATE TABLE Marker(Value TEXT NOT NULL); INSERT INTO Marker(Value) VALUES ('old');");

        // Leave an idle pooled handle pointing at the pre-switch file identity.
        using (var pooled = new SqliteConnection($"Data Source={env.Live}"))
        {
            pooled.Open();
            env.ExecuteScalar(pooled, "SELECT Value FROM Marker;").Should().Be("old");
        }

        var candidate = Path.Combine(env.DatabaseRoot, "candidate.db");
        LifecycleEnvironment.CreateDatabase(candidate, "new");
        File.Move(env.Live, env.Live + ".previous");
        File.Move(candidate, env.Live);

        var gate = new LibraryMaintenanceCoordinator();
        var lifecycle = new SelfHostedSqliteLifecycle(env.Paths, gate);
        await using (var lease = await gate.EnterExclusiveAsync(LibraryMaintenanceReason.Activation))
        {
            lifecycle.ReopenActivated(lease);
            lifecycle.ReopenActivated(lease); // repeatable
        }

        using var reopened = new SqliteConnection($"Data Source={env.Live}");
        reopened.Open();
        env.ExecuteScalar(reopened, "SELECT Value FROM Marker;").Should().Be("new");
        File.Exists(env.Live + ".previous").Should().BeTrue();
    }

    [Fact]
    public async Task ReopenActivated_OnACorruptActivatedDatabase_FailsClosedWithATypedError()
    {
        using var env = new LifecycleEnvironment();
        env.Execute("CREATE TABLE Marker(Value TEXT NOT NULL); INSERT INTO Marker(Value) VALUES ('kept');");
        File.WriteAllBytes(env.Live, [0x00, 0x11, 0x22, 0x33, 0x44]);

        var gate = new LibraryMaintenanceCoordinator();
        var lifecycle = new SelfHostedSqliteLifecycle(env.Paths, gate);
        await using var lease = await gate.EnterExclusiveAsync(LibraryMaintenanceReason.Activation);
        Action reopen = () => lifecycle.ReopenActivated(lease);
        reopen.Should().Throw<MigrationActivationException>()
            .Which.Code.Should().Be(MigrationActivationErrorCodes.Failed);
    }
}

internal sealed class LifecycleEnvironment : IDisposable
{
    internal LifecycleEnvironment()
    {
        Root = Path.Combine(Path.GetTempPath(), $"nostos-sqlite-lifecycle-{Guid.NewGuid():N}");
        DatabaseRoot = Path.Combine(Root, "db-volume");
        var mediaRoot = Path.Combine(Root, "media-volume");
        Directory.CreateDirectory(DatabaseRoot);
        Directory.CreateDirectory(mediaRoot);
        Live = Path.Combine(DatabaseRoot, "nostos.db");
        Paths = new SelfHostedActivationPaths(Live, Path.Combine(mediaRoot, "library"));
    }

    internal string Root { get; }
    internal string DatabaseRoot { get; }
    internal string Live { get; }
    internal SelfHostedActivationPaths Paths { get; }

    internal static void CreateDatabase(string path, string value)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE Marker(Value TEXT NOT NULL);";
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO Marker(Value) VALUES ($value);";
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    internal void Execute(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={Live};Pooling=False");
        connection.Open();
        ExecuteOn(connection, sql);
    }

    internal void ExecuteOn(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal object? ExecuteScalar(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={Live};Pooling=False");
        connection.Open();
        return ExecuteScalar(connection, sql);
    }

    internal object? ExecuteScalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch
        {
            // Test cleanup only.
        }
    }
}

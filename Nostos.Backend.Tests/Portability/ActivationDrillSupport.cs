using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Activation;
using Nostos.Backend.Services.Portability.Migration;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Disposable data root and content assertions for the real-process drills. The
/// database, media root and staged transfer area all live under one temp root;
/// no child process ever receives a path outside it.
/// </summary>
internal sealed class ActivationDrillFixture : IAsyncDisposable
{
    private ActivationDrillFixture(
        ActivationCoordinatorTemplate template,
        bool populated,
        string root,
        string databaseRoot,
        string booksRoot,
        string transferRoot,
        string backupRoot)
    {
        Template = template;
        Populated = populated;
        Root = root;
        DatabaseRoot = databaseRoot;
        BooksRoot = booksRoot;
        TransferRoot = transferRoot;
        BackupRoot = backupRoot;
        DatabasePath = Path.Combine(databaseRoot, "nostos.db");
    }

    internal ActivationCoordinatorTemplate Template { get; }
    internal bool Populated { get; }
    internal string Root { get; }
    internal string DatabaseRoot { get; }
    internal string BooksRoot { get; }
    internal string TransferRoot { get; }
    internal string BackupRoot { get; }
    internal string DatabasePath { get; }
    internal string MaintenanceMarkerPath =>
        Path.Combine(DatabaseRoot, ".nostos-activation", "maintenance.json");

    internal static async Task<ActivationDrillFixture> CreateAsync(bool populated)
    {
        var template = ActivationCoordinatorTemplate.For(populated);
        var root = Path.Combine(Path.GetTempPath(), $"nostos-681-drill-{Guid.NewGuid():N}");
        var databaseRoot = Path.Combine(root, "db");
        var booksRoot = Path.Combine(root, "books");
        var transferRoot = Path.Combine(root, "transfers");
        var backupRoot = Path.Combine(root, "backups");
        Directory.CreateDirectory(databaseRoot);
        Directory.CreateDirectory(booksRoot);
        Directory.CreateDirectory(transferRoot);
        Directory.CreateDirectory(backupRoot);

        var fixture = new ActivationDrillFixture(
            template, populated, root, databaseRoot, booksRoot, transferRoot, backupRoot);
        File.Copy(template.TemplateDatabase, fixture.DatabasePath, overwrite: true);
        CopyDirectory(template.TemplateMedia, booksRoot);

        // The drill creates its own import job through the real routes. Remove
        // the fixture's seeded migration bookkeeping so the live host's hosted
        // workers have no unrelated rows to advance and the full-database
        // comparison isolates the library generation, not worker churn.
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                   $"Data Source={fixture.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = string.Join(";",
            [
                "DELETE FROM \"MigrationChunkReceiptRecords\"",
                "DELETE FROM \"MigrationSessionRecords\"",
                "DELETE FROM \"MigrationExportArtifactRecords\"",
                "DELETE FROM \"MigrationStorageReservations\"",
                "DELETE FROM \"MigrationJobRecords\"",
            ]);
            command.ExecuteNonQuery();
        }

        await Task.CompletedTask;
        return fixture;
    }

    internal static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    internal static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        if (!Directory.Exists(source)) return;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    // ---- content assertions ----

    internal Dictionary<string, List<string>> SnapshotFull() =>
        ActivationBuildFixture.DumpAllTables(DatabasePath);

    internal Dictionary<string, string> SnapshotMedia() =>
        ActivationBuildFixture.MediaSnapshot(BooksRoot);

    internal Dictionary<string, List<string>> SnapshotPortable() =>
        ActivationCoordinatorTemplate.DumpPortable(DatabasePath);

    /// <summary>
    /// Every table, every row and every media byte equal to the snapshot, except
    /// the tested job's own row and the capacity reservations. Activation
    /// admission legitimately adjusts the durable transfer reservation (it
    /// enlarges or records the recovery hold) even when the cutover then rolls
    /// back; those rows are live operational accounting, not library content.
    /// Portable tables are still compared row-for-row by
    /// <see cref="AssertPortableAndMedia"/> and the library generation by the
    /// verifier.
    /// </summary>
    internal void AssertSameGenerationExceptJob(
        Dictionary<string, List<string>> expected,
        Guid jobId,
        string because)
    {
        var actual = SnapshotFull();
        actual.Keys.Should().BeEquivalentTo(expected.Keys, $"{because}: no table may appear or disappear");
        foreach (var table in expected.Keys)
        {
            if (table == "MigrationStorageReservations")
            {
                // The activation run may add or top up its own capacity hold
                // even when it rolls back. Every pre-existing reservation row
                // must survive exactly.
                foreach (var row in expected[table])
                {
                    actual[table].Should().Contain(row, $"{because} (pre-existing reservation survives)");
                }

                continue;
            }

            var expectedRows = table == "MigrationJobRecords"
                ? expected[table].Where(row => !IsJobRow(row, jobId)).ToList()
                : expected[table];
            var actualRows = table == "MigrationJobRecords"
                ? actual[table].Where(row => !IsJobRow(row, jobId)).ToList()
                : actual[table];
            actualRows.Should().BeEquivalentTo(expectedRows, $"{because} (table {table})");
        }
    }

    private static bool IsJobRow(string row, Guid jobId) =>
        string.Equals(row.Split('|', 2)[0], jobId.ToString(), StringComparison.OrdinalIgnoreCase);

    /// <summary>The complete portable library and media equal the expected generation.</summary>
    internal void AssertPortableAndMedia(
        Dictionary<string, List<string>> expectedPortable,
        Dictionary<string, string> expectedMedia,
        string because)
    {
        var actual = SnapshotPortable();
        actual.Keys.Should().BeEquivalentTo(expectedPortable.Keys);
        foreach (var table in expectedPortable.Keys)
        {
            actual[table].Should().BeEquivalentTo(expectedPortable[table], $"{because} (table {table})");
        }

        SnapshotMedia().Should().BeEquivalentTo(expectedMedia, $"{because} (media)");
    }

    /// <summary>The live library is exactly the verified imported generation.</summary>
    internal async Task AssertImportedGenerationAsync()
    {
        await using var db = new NostosDbContext(new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={DatabasePath};Pooling=False")
            .Options);
        var report = await new PortableLibraryVerifier()
            .VerifyCandidateAsync(db, BooksRoot, Template.Prepared, Template.ExpectedHandle, default);
        report.Passed.Should().BeTrue(
            "the activated generation must be exactly the verified import: {0}",
            string.Join(", ", report.Failures.Select(failure => failure.Code)));
        SnapshotMedia().Should().BeEquivalentTo(
            Template.ExpectedImportedMedia, "only the imported primary media may be active");
    }

    internal void AssertOriginalGeneration(string because)
    {
        AssertPortableAndMedia(Template.OriginalPortable, Template.OriginalMedia, because);
    }

    internal void AssertMaintenanceReleased()
    {
        File.Exists(MaintenanceMarkerPath).Should().BeFalse(
            "the host must leave maintenance before ordinary requests are admitted");
    }

    internal string RecoveryManifestPath(Guid recoveryId) =>
        Path.Combine(DatabaseRoot, ".nostos-recovery", recoveryId.ToString("N"), "recovery.json");

    internal string RecoveryDirectory(Guid recoveryId) =>
        Path.GetDirectoryName(RecoveryManifestPath(recoveryId))!;

    internal void ExpireRecoveryCopy(Guid recoveryId)
    {
        var path = RecoveryManifestPath(recoveryId);
        var manifest = SelfHostedActivationDocument.Decode<SelfHostedRecoveryManifest>(File.ReadAllText(path));
        var delta = TimeSpan.FromDays(MigrationContractLimits.RecoveryRetentionDays + 1);
        File.WriteAllText(path, SelfHostedActivationDocument.Encode(manifest with
        {
            CreatedAtUtc = manifest.CreatedAtUtc - delta,
            ExpiresAtUtc = manifest.ExpiresAtUtc - delta,
        }));
    }

    internal void SeedCorruptJournal(Guid jobId)
    {
        var directory = Path.Combine(DatabaseRoot, ".nostos-activation", jobId.ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "activation.json"), "{torn");
    }

    internal void RemoveJournalTree(Guid jobId)
    {
        var directory = Path.Combine(DatabaseRoot, ".nostos-activation", jobId.ToString("N"));
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    /// <summary>
    /// Expires any crashed owner's job lease while no host is running. A real
    /// restart waits for the lease TTL; the in-process crash matrix advances its
    /// fake clock and the HTTP suite strips the lease, so the drills do the same
    /// instead of sleeping for minutes.
    /// </summary>
    internal void ExpireJobLeases()
    {
        // At a pre-commit crash the live database may be retained; never open
        // (and thereby create) a database at the vacated live path.
        if (!File.Exists(DatabasePath)) return;
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={DatabasePath};Mode=ReadWrite;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE \"MigrationJobRecords\" SET \"MigrationLeaseToken\" = NULL, \"LeaseExpiresAtUtc\" = NULL;";
        command.ExecuteNonQuery();
    }

    internal async Task<DateTime?> ReservationReleasedAtAsync(Guid reservationId)
    {
        await using var db = new NostosDbContext(new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={DatabasePath};Pooling=False")
            .Options);
        var row = await db.MigrationStorageReservations.AsNoTracking()
            .Where(reservation => reservation.Id == reservationId)
            .Select(reservation => reservation.ReleasedAtUtc)
            .SingleOrDefaultAsync();
        return row;
    }

    public ValueTask DisposeAsync()
    {
        MakeWritable(Root);
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return ValueTask.CompletedTask;
    }

    internal static void MakeWritable(string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
        {
            try
            {
                if (Directory.Exists(path))
                {
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
                else
                {
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
            catch (Exception)
            {
                // Best effort cleanup only.
            }
        }
    }
}

/// <summary>
/// One real backend process on the fixture's data root. Hard kill is
/// <see cref="Process.Kill(bool)"/> with the whole process tree, never a
/// graceful shutdown.
/// </summary>
internal sealed class ActivationDrillHost : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ActivationDrillFixture _fixture;
    private readonly StringBuilder _logs = new();
    private readonly object _logGate = new();
    private Process? _process;

    private ActivationDrillHost(ActivationDrillFixture fixture)
    {
        _fixture = fixture;
        Port = ActivationDrillFixture.FreePort();
        Client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{Port}"),
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    internal int Port { get; }
    internal HttpClient Client { get; }
    internal string MarkerPath { get; private set; } = string.Empty;
    internal string? ReleasePath { get; private set; }
    internal bool IsRunning => _process is { HasExited: false };
    internal string Logs
    {
        get
        {
            lock (_logGate) return _logs.ToString();
        }
    }

    internal static async Task<ActivationDrillHost> StartAsync(
        ActivationDrillFixture fixture,
        string? stopAt = null,
        string? releasePath = null,
        IDictionary<string, string>? extraEnvironment = null)
    {
        var host = new ActivationDrillHost(fixture);
        host.MarkerPath = Path.Combine(fixture.Root, $"drill-marker-{Guid.NewGuid():N}.txt");
        host.ReleasePath = releasePath;
        var backendDll = Path.Combine(AppContext.BaseDirectory, "Nostos.Backend.dll");
        File.Exists(backendDll).Should().BeTrue($"the built backend assembly must exist at {backendDll}");

        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = fixture.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(backendDll);
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Testing";
        startInfo.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{host.Port}";
        startInfo.Environment["Persistence__DatabasePath"] = fixture.DatabasePath;
        startInfo.Environment["Storage__BooksRoot"] = fixture.BooksRoot;
        startInfo.Environment["Storage__TransferPath"] = fixture.TransferRoot;
        startInfo.Environment["Storage__BackupsRoot"] = fixture.BackupRoot;
        startInfo.Environment["Mcp__Enabled"] = "false";
        startInfo.Environment["Logging__LogLevel__Default"] = "Warning";
        startInfo.Environment["Logging__LogLevel__Microsoft.AspNetCore"] = "Warning";
        if (stopAt is not null)
        {
            startInfo.Environment["NOSTOS_ACTIVATION_DRILL_STOP_AT"] = stopAt;
            startInfo.Environment["NOSTOS_ACTIVATION_DRILL_MARKER_FILE"] = host.MarkerPath;
            if (releasePath is not null)
            {
                startInfo.Environment["NOSTOS_ACTIVATION_DRILL_RELEASE_FILE"] = releasePath;
            }
        }

        if (extraEnvironment is not null)
        {
            foreach (var (key, value) in extraEnvironment) startInfo.Environment[key] = value;
        }

        host._process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The drill host process could not be started.");
        host._process.OutputDataReceived += (_, args) => host.Append(args.Data);
        host._process.ErrorDataReceived += (_, args) => host.Append(args.Data);
        host._process.BeginOutputReadLine();
        host._process.BeginErrorReadLine();
        await host.WaitReadyAsync();
        return host;
    }

    /// <summary>Starts a host that is expected to refuse startup; waits for the exit.</summary>
    internal static async Task<ActivationDrillHost> StartExpectingRefusalAsync(
        ActivationDrillFixture fixture,
        IDictionary<string, string>? extraEnvironment = null)
    {
        var host = new ActivationDrillHost(fixture);
        host.MarkerPath = Path.Combine(fixture.Root, $"drill-marker-{Guid.NewGuid():N}.txt");
        var backendDll = Path.Combine(AppContext.BaseDirectory, "Nostos.Backend.dll");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = fixture.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(backendDll);
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Testing";
        startInfo.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{host.Port}";
        startInfo.Environment["Persistence__DatabasePath"] = fixture.DatabasePath;
        startInfo.Environment["Storage__BooksRoot"] = fixture.BooksRoot;
        startInfo.Environment["Storage__TransferPath"] = fixture.TransferRoot;
        startInfo.Environment["Storage__BackupsRoot"] = fixture.BackupRoot;
        startInfo.Environment["Mcp__Enabled"] = "false";
        startInfo.Environment["Logging__LogLevel__Default"] = "Warning";
        startInfo.Environment["Logging__LogLevel__Microsoft.AspNetCore"] = "Warning";
        if (extraEnvironment is not null)
        {
            foreach (var (key, value) in extraEnvironment) startInfo.Environment[key] = value;
        }

        host._process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The drill host process could not be started.");
        host._process.OutputDataReceived += (_, args) => host.Append(args.Data);
        host._process.ErrorDataReceived += (_, args) => host.Append(args.Data);
        host._process.BeginOutputReadLine();
        host._process.BeginErrorReadLine();
        await host._process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(120));
        return host;
    }

    private void Append(string? line)
    {
        if (line is null) return;
        lock (_logGate)
        {
            _logs.AppendLine(line);
            if (_logs.Length > 200_000) _logs.Remove(0, _logs.Length - 150_000);
        }
    }

    private async Task WaitReadyAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            if (_process is { HasExited: true })
            {
                throw new InvalidOperationException(
                    $"The drill host exited with code {_process.ExitCode} before becoming ready.\n{Logs}");
            }

            try
            {
                using var response = await Client.GetAsync("/health/ready");
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException)
            {
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"The drill host did not become ready.\n{Logs}");
    }

    internal async Task WaitForMarkerAsync(TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(120));
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(MarkerPath)) return;
            if (_process is { HasExited: true })
            {
                throw new InvalidOperationException(
                    $"The drill host exited with code {_process.ExitCode} before reaching the boundary.\n{Logs}");
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"The drill boundary marker was not written.\n{Logs}");
    }

    internal void ReleaseBoundary()
    {
        ReleasePath.Should().NotBeNull("the drill was started with a release file");
        File.WriteAllText(ReleasePath!, "release");
    }

    /// <summary>
    /// Terminates the host process tree hard (SIGKILL on Linux) and asserts the
    /// abnormal exit: a graceful stop would not exercise crash recovery.
    /// </summary>
    internal async Task KillHardAsync()
    {
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            _process.HasExited.Should().BeTrue("the hard kill must terminate the host process");
            _process.ExitCode.Should().NotBe(0, "a hard kill is an abnormal, non-graceful exit");
        }
    }

    // ---- HTTP helpers ----

    internal async Task<(HttpStatusCode Status, JsonDocument Body)> SendAsync(
        HttpMethod method,
        string path,
        HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        return (response.StatusCode, await ReadJsonAsync(response));
    }

    internal async Task<(HttpStatusCode Status, JsonDocument Body)> PostJsonAsync(string path, object payload)
    {
        // Serialize through the runtime type: the payload parameter erases the
        // static type, and an object-typed JsonContent would lose every field.
        var json = JsonSerializer.Serialize(payload, payload.GetType(), Json);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await SendAsync(HttpMethod.Post, path, content);
    }

    internal async Task<JsonDocument> GetJsonAsync(string path)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, path);
        if (status != HttpStatusCode.OK)
        {
            var observed = body.RootElement.GetRawText();
            body.Dispose();
            throw new InvalidOperationException($"GET {path} returned {(int)status}: {observed}");
        }

        return body;
    }

    internal static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
    }

    internal async Task<JsonDocument> GetActivationAsync(Guid jobId) =>
        await GetJsonAsync($"/api/portability/migration/jobs/{jobId}/activation");

    internal async Task<(HttpStatusCode Status, JsonDocument Body)> ActivateAsync(
        Guid jobId,
        string revision,
        bool confirm) =>
        await PostJsonAsync(
            $"/api/portability/migration/jobs/{jobId}/activate",
            new { destinationRevision = revision, confirmReplacement = confirm });

    internal async Task<string?> JobStateAsync(Guid jobId)
    {
        var body = await GetJsonAsync($"/api/portability/migration/jobs/{jobId}");
        using (body)
        {
            return body.RootElement.GetProperty("job").GetProperty("state").GetString();
        }
    }

    /// <summary>The current opaque destination revision, read through real preflight.</summary>
    internal async Task<string> CurrentRevisionAsync()
    {
        var preflight = await PreflightAsync();
        var revision = preflight.Body.RootElement
            .GetProperty("evaluation").GetProperty("destinationRevision").GetString();
        preflight.Body.Dispose();
        revision.Should().NotBeNullOrEmpty("preflight always returns the current revision");
        return revision!;
    }

    private async Task<(HttpStatusCode Status, JsonDocument Body)> PreflightAsync()
    {
        var metadata = _fixture.Template.Prepared.Metadata;
        var payload = new Dictionary<string, object?>
        {
            ["incomingCounts"] = JsonSerializer.SerializeToElement(metadata.Counts, Json),
            ["declaredArchiveBytes"] = metadata.ArchiveBytes,
            ["declaredMediaBytes"] = metadata.MediaBytes,
            ["maxSingleEntryBytes"] = Math.Max(1, Math.Max(metadata.ArchiveBytes, metadata.MediaBytes)),
            ["declaredFormatVersion"] = 1,
            ["declaredDataVersion"] = 1,
            ["declaredFormatName"] = "nostos-portable",
            ["clientDestinationRevision"] = null,
            ["isOperationalBackup"] = false,
        };
        var json = JsonSerializer.Serialize(payload, Json);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var result = await SendAsync(
            HttpMethod.Post, "/api/portability/migration/preflight", content);
        result.Status.Should().Be(
            HttpStatusCode.OK,
            $"preflight sent {json} and answered {result.Body.RootElement.GetRawText()}\n{Logs}");
        return result;
    }

    internal async Task<(HttpStatusCode Status, JsonDocument Body)> RestoreAsync(
        Guid recoveryId,
        string revision) =>
        await PostJsonAsync(
            $"/api/portability/migration/recovery/{recoveryId}/restore",
            new { destinationRevision = revision, confirmReplacement = true });

    internal async Task<JsonDocument> WaitForRecoveryStatusAsync(
        Guid recoveryId,
        string expected,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(120));
        string? last = null;
        while (DateTime.UtcNow < deadline)
        {
            var (status, body) = await SendAsync(
                HttpMethod.Get, $"/api/portability/migration/recovery/{recoveryId}");
            using (body)
            {
                if (status == HttpStatusCode.OK)
                {
                    last = body.RootElement.GetProperty("status").GetString();
                    if (string.Equals(last, expected, StringComparison.Ordinal))
                    {
                        return JsonDocument.Parse(body.RootElement.GetRawText());
                    }
                }
                else
                {
                    last = ((int)status).ToString();
                }
            }

            await Task.Delay(150);
        }

        throw new TimeoutException(
            $"Recovery copy {recoveryId} did not reach {expected}; last status was {last}.\n{Logs}");
    }

    /// <summary>Uploads the fixture archive through the real routes and reaches ReadyToActivate.</summary>
    internal async Task<Guid> UploadImportAsync(string keyPrefix)
    {
        var bytes = await File.ReadAllBytesAsync(_fixture.Template.ArchivePath);
        var preflight = await PreflightAsync();
        var reservationId = preflight.Body.RootElement.GetProperty("reservationId").GetGuid();
        preflight.Body.Dispose();

        var createJson = JsonSerializer.Serialize(new
        {
            direction = "Import",
            idempotencyKey = keyPrefix + Guid.NewGuid().ToString("N"),
            reservationId,
        }, Json);
        using var createContent = new StringContent(createJson, Encoding.UTF8, "application/json");
        var create = await SendAsync(
            HttpMethod.Post, "/api/portability/migration/jobs", createContent);
        create.Status.Should().Be(
            HttpStatusCode.Created,
            $"create sent {createJson} and answered {create.Body.RootElement.GetRawText()}\n{Logs}");
        var jobId = create.Body.RootElement.GetProperty("job").GetProperty("id").GetGuid();
        create.Body.Dispose();

        const int chunkSize = 4 * 1024 * 1024;
        var totalChunks = checked((int)((bytes.LongLength - 1) / chunkSize + 1));
        var sessionJson = JsonSerializer.Serialize(new
        {
            purpose = "Import",
            totalBytes = bytes.LongLength,
            chunkSize,
            totalChunks,
            fileIdentity = new
            {
                totalSizeBytes = bytes.LongLength,
                sha256Checksum = Sha256(bytes),
                clientFingerprint = "drill",
            },
            idempotencyKey = "drill-session-" + Guid.NewGuid().ToString("N"),
        }, Json);
        using var sessionContent = new StringContent(sessionJson, Encoding.UTF8, "application/json");
        var session = await SendAsync(
            HttpMethod.Post,
            $"/api/portability/migration/jobs/{jobId}/upload-session",
            sessionContent);
        session.Status.Should().BeOneOf(
            new[] { HttpStatusCode.Created, HttpStatusCode.OK },
            $"session sent {sessionJson} and answered {session.Body.RootElement.GetRawText()}\n{Logs}");
        session.Body.Dispose();

        for (var index = 0; index < totalChunks; index++)
        {
            var offset = (long)index * chunkSize;
            var length = (int)Math.Min(chunkSize, bytes.LongLength - offset);
            var chunk = bytes.AsSpan((int)offset, length).ToArray();
            using var content = new ByteArrayContent(chunk);
            content.Headers.TryAddWithoutValidation(
                "Content-Range", $"bytes {offset}-{offset + length - 1}/{bytes.LongLength}");
            content.Headers.TryAddWithoutValidation("X-Nostos-Chunk-SHA256", Sha256(chunk));
            var upload = await SendAsync(
                HttpMethod.Put,
                $"/api/portability/migration/jobs/{jobId}/upload-session/chunks/{index}",
                content);
            upload.Body.Dispose();
            upload.Status.Should().Be(HttpStatusCode.OK);
        }

        var complete = await SendAsync(
            HttpMethod.Post,
            $"/api/portability/migration/jobs/{jobId}/upload-session/complete",
            new ByteArrayContent([]));
        complete.Body.Dispose();
        complete.Status.Should().Be(HttpStatusCode.OK);

        await WaitForJobStateAsync(jobId, "ReadyToActivate");
        return jobId;
    }

    internal async Task WaitForJobStateAsync(Guid jobId, string expected, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(120));
        string? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await JobStateAsync(jobId);
            if (string.Equals(last, expected, StringComparison.Ordinal)) return;
            await Task.Delay(100);
        }

        throw new TimeoutException($"Job {jobId} did not reach {expected}; last state was {last}.\n{Logs}");
    }

    /// <summary>
    /// Posts activation until the drill boundary marker appears. The host's own
    /// migration worker may hold the job lease for its final preparation unit at
    /// the moment of the first POST; that run ends InProgress without starting a
    /// cutover, so a real browser would repeat the POST. Re-posting is harmless
    /// while a run is genuinely active: the dispatcher replays it.
    /// </summary>
    internal async Task ActivateUntilMarkerAsync(Guid jobId, string revision, bool confirm)
    {
        var deadline = DateTime.UtcNow.AddSeconds(180);
        while (DateTime.UtcNow < deadline)
        {
            var accepted = await ActivateAsync(jobId, revision, confirm);
            accepted.Body.Dispose();
            accepted.Status.Should().Be(HttpStatusCode.Accepted);

            try
            {
                await WaitForMarkerAsync(TimeSpan.FromSeconds(15));
                return;
            }
            catch (TimeoutException)
            {
            }

            if (await TryJobStateAsync(jobId) is "ReadyToActivate"
                or "Completed" or "Failed" or "Cancelled" or "Expired")
            {
                continue; // the run lost a transient lease race; post again
            }

            // A run is genuinely in Phase A/B; wait for its boundary marker.
            var slice = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < slice)
            {
                if (File.Exists(MarkerPath)) return;
                await Task.Delay(100);
            }
        }

        throw new TimeoutException($"The drill boundary marker was never reached.\n{Logs}");
    }

    /// <summary>Posts activation until a terminal outcome is observed.</summary>
    internal async Task<JsonDocument> ActivateToCompletionAsync(
        Guid jobId,
        string revision,
        bool confirm,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(180));
        while (DateTime.UtcNow < deadline)
        {
            var accepted = await ActivateAsync(jobId, revision, confirm);
            accepted.Body.Dispose();
            accepted.Status.Should().Be(HttpStatusCode.Accepted);

            var slice = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < slice)
            {
                var status = await GetActivationAsync(jobId);
                var outcome = status.RootElement.GetProperty("outcome").GetString();
                if (outcome is "Completed" or "Failed" or "RecoveryFailed") return status;
                status.Dispose();
                await Task.Delay(100);
            }
        }

        throw new TimeoutException($"Job {jobId} did not reach a terminal activation outcome.\n{Logs}");
    }

    internal async Task<string?> TryJobStateAsync(Guid jobId)
    {
        var (status, body) = await SendAsync(
            HttpMethod.Get, $"/api/portability/migration/jobs/{jobId}");
        using (body)
        {
            return status == HttpStatusCode.OK
                ? body.RootElement.GetProperty("job").GetProperty("state").GetString()
                : null;
        }
    }

    internal async Task<JsonDocument> WaitForActivationOutcomeAsync(
        Guid jobId,
        string outcome,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(120));
        JsonDocument? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last?.Dispose();
            last = await GetActivationAsync(jobId);
            if (last.RootElement.GetProperty("outcome").GetString() == outcome) return last;
            await Task.Delay(100);
        }

        var observed = last?.RootElement.GetRawText() ?? "<none>";
        last?.Dispose();
        throw new TimeoutException($"Job {jobId} did not reach activation outcome {outcome}; last {observed}.\n{Logs}");
    }

    internal static string Sha256(byte[] bytes) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            }
        }
        catch (Exception)
        {
            // Drill cleanup only.
        }

        _process?.Dispose();
        Client.Dispose();
    }
}

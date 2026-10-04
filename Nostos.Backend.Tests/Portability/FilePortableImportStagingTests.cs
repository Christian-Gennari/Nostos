using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Transfers;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Runs the provider-independent <see cref="IPortableImportStaging"/> contract
/// against the durable file-backed provider over a disposable transfer root.
/// </summary>
public sealed class FilePortableImportStagingContractTests
    : PortableImportStagingContractTests, IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"nostos-file-staging-{Guid.NewGuid():N}");

    protected override IPortableImportStaging CreateStaging() =>
        new FilePortableImportStaging(new TransferPathResolver(_root));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Test cleanup only.
        }
    }
}

/// <summary>
/// File-specific behaviour of the durable staging provider: restart and crash
/// recovery, path defense, atomic publication, delete, and deterministic
/// single- and cross-instance concurrency. Every crash seam is exercised on real
/// files; no test sleeps.
/// </summary>
public sealed class FilePortableImportStagingTests : IDisposable
{
    private const string StateFileName = "state.json";
    private const string StateTempFileName = "state.json.tmp";

    private readonly string _parent = Path.Combine(
        Path.GetTempPath(),
        $"nostos-file-staging-parent-{Guid.NewGuid():N}");

    private readonly string _root;
    private readonly TransferPathResolver _resolver;

    public FilePortableImportStagingTests()
    {
        _root = Path.Combine(_parent, "root");
        Directory.CreateDirectory(_parent);
        _resolver = new TransferPathResolver(_root);
    }

    [Fact]
    public async Task Committed_import_survives_a_fresh_process_scope()
    {
        var metadata = default(PreparedPortableImportMetadata);
        var mediaReference = default(PortableStagedMediaReference);
        var dataBytes = Array.Empty<byte>();
        var manifestBytes = Array.Empty<byte>();
        var mediaBytes = Array.Empty<byte>();

        var first = CreateStaging(NewScope());
        var id = await first.CreateAsync();
        var staged = await StageAndCommitAsync(first, id);
        metadata = staged.Metadata;
        mediaReference = staged.MediaReference;
        dataBytes = staged.DataBytes;
        manifestBytes = staged.ManifestBytes;
        mediaBytes = staged.MediaBytes;
        await first.DisposeAsync();

        // A different coordinator scope forces the second instance to rebuild
        // everything from disk, exactly as a new process would.
        await using var second = CreateStaging(NewScope());

        var rebuilt = await second.RebuildPreparedImportAsync(id);
        rebuilt.Metadata.Should().Be(metadata);
        rebuilt.Media.Should().ContainSingle();
        rebuilt.Media[0].Reference.Should().Be(mediaReference);

        await using (var data = await second.OpenDataReadAsync(id))
        {
            using var copy = new MemoryStream();
            await data.CopyToAsync(copy);
            copy.ToArray().Should().Equal(dataBytes);
        }

        await using (var manifest = await second.OpenManifestReadAsync(id))
        {
            using var copy = new MemoryStream();
            await manifest.CopyToAsync(copy);
            copy.ToArray().Should().Equal(manifestBytes);
        }

        await using (var media = await second.OpenMediaReadAsync(id, mediaReference))
        {
            using var copy = new MemoryStream();
            await media.CopyToAsync(copy);
            copy.ToArray().Should().Equal(mediaBytes);
        }

        (await second.ListMediaAsync(id)).Should().ContainSingle();

        await second.DeleteAsync(id);
        Directory.Exists(AreaDirectory(id)).Should().BeFalse();
    }

    [Fact]
    public async Task Crash_after_media_seal_before_rename_leaves_no_visible_item()
    {
        await using var crashing = CreateStaging(
            NewScope(),
            CrashAt(FileStagingCrashPoint.AfterMediaSealedBeforeRename));
        var id = await crashing.CreateAsync();
        var content = new byte[] { 1, 2, 3, 4 };
        var descriptor = MediaDescriptor(content);
        var write = await crashing.OpenMediaWriteAsync(id, descriptor);
        await write.Stream.WriteAsync(content);

        var crash = await Assert.ThrowsAsync<FileStagingSimulatedCrashException>(
            () => crashing.CompleteMediaAsync(id, write));
        crash.Point.Should().Be(FileStagingCrashPoint.AfterMediaSealedBeforeRename);

        Directory.EnumerateFiles(MediaDirectory(id))
            .Should().Contain(path => path.Contains(".tmp.", StringComparison.Ordinal),
                "the crash seam stops after the scratch file was written and flushed");

        await using var fresh = CreateStaging(NewScope());
        (await fresh.ListMediaAsync(id)).Should().BeEmpty();
        await Assert.ThrowsAsync<PortableStagingException>(
            () => fresh.OpenMediaReadAsync(id, write.Reference));
        await Assert.ThrowsAsync<PortableStagingException>(
            () => fresh.RebuildPreparedImportAsync(id));

        await fresh.DeleteAsync(id);
        Directory.Exists(AreaDirectory(id)).Should().BeFalse();
    }

    [Fact]
    public async Task Crash_after_media_rename_before_inventory_leaves_uncommitted_leftover()
    {
        await using var crashing = CreateStaging(
            NewScope(),
            CrashAt(FileStagingCrashPoint.AfterMediaRenamedBeforeInventory));
        var id = await crashing.CreateAsync();
        var content = new byte[] { 9, 8, 7 };
        var write = await crashing.OpenMediaWriteAsync(id, MediaDescriptor(content));
        await write.Stream.WriteAsync(content);

        var crash = await Assert.ThrowsAsync<FileStagingSimulatedCrashException>(
            () => crashing.CompleteMediaAsync(id, write));
        crash.Point.Should().Be(FileStagingCrashPoint.AfterMediaRenamedBeforeInventory);

        Directory.EnumerateFiles(MediaDirectory(id))
            .Should().ContainSingle(path => !path.Contains(".tmp.", StringComparison.Ordinal),
                "the final file was renamed into place before the crash");

        await using var fresh = CreateStaging(NewScope());
        (await fresh.ListMediaAsync(id)).Should().BeEmpty(
            "the durable inventory never recorded the renamed leftover");
        await Assert.ThrowsAsync<PortableStagingException>(
            () => fresh.OpenMediaReadAsync(id, write.Reference));
        await Assert.ThrowsAsync<PortableStagingException>(
            () => fresh.RebuildPreparedImportAsync(id));

        await fresh.DeleteAsync(id);
        Directory.Exists(AreaDirectory(id)).Should().BeFalse();
    }

    [Fact]
    public async Task Crash_during_commit_before_marker_replace_keeps_area_uncommitted()
    {
        await using var crashing = CreateStaging(
            NewScope(),
            CrashAt(FileStagingCrashPoint.BeforeCommitMarkerReplace));
        var id = await crashing.CreateAsync();
        var staged = await StageAllWithoutCommitAsync(crashing, id);

        var crash = await Assert.ThrowsAsync<FileStagingSimulatedCrashException>(
            () => crashing.CommitPreparedImportAsync(id, staged.Metadata));
        crash.Point.Should().Be(FileStagingCrashPoint.BeforeCommitMarkerReplace);

        File.Exists(Path.Combine(AreaDirectory(id), StateTempFileName))
            .Should().BeTrue("the committed inventory was flushed to state.json.tmp before the crash");

        await using var fresh = CreateStaging(NewScope());
        await Assert.ThrowsAsync<PortableStagingException>(
            () => fresh.RebuildPreparedImportAsync(id));
        (await fresh.ListMediaAsync(id)).Should().ContainSingle(
            "completed media remains durable; only the commit marker is missing");

        await using (var data = await fresh.OpenDataReadAsync(id))
        {
            using var copy = new MemoryStream();
            await data.CopyToAsync(copy);
            copy.ToArray().Should().Equal(staged.DataBytes);
        }

        await fresh.DeleteAsync(id);
        Directory.Exists(AreaDirectory(id)).Should().BeFalse();
    }

    [Fact]
    public async Task Uncommitted_leftovers_are_deletable_and_not_rebuildable()
    {
        await using var staging = CreateStaging(NewScope());
        var id = await staging.CreateAsync();
        var staged = await StageAllWithoutCommitAsync(staging, id);

        await using var fresh = CreateStaging(NewScope());
        (await fresh.ListMediaAsync(id)).Should().ContainSingle();
        await Assert.ThrowsAsync<PortableStagingException>(
            () => fresh.RebuildPreparedImportAsync(id));
        await fresh.DeleteAsync(id);
        await fresh.DeleteAsync(id);
        await fresh.DeleteAsync(new PortableStagingId(Guid.NewGuid()));
        await fresh.DeleteAsync(default);

        Directory.Exists(AreaDirectory(id)).Should().BeFalse();
        await Assert.ThrowsAsync<PortableStagingException>(
            () => fresh.ListMediaAsync(id));
    }

    [Fact]
    public async Task Hostile_references_are_rejected_and_never_used_as_paths()
    {
        await using var staging = CreateStaging(NewScope());
        var id = await staging.CreateAsync();

        foreach (var value in new[]
                 {
                     string.Empty,
                     ".",
                     "..",
                     "../escape",
                     "a/b",
                     @"a\b",
                     "/etc/passwd",
                     @"C:\Windows\evil",
                     "../outside",
                     new string('a', 129),
                 })
        {
            var failure = await Assert.ThrowsAsync<PortableStagingException>(
                () => staging.OpenMediaReadAsync(id, new PortableStagedMediaReference(value)));
            failure.Code.Should().Be(PortableStagingException.InvalidReferenceCode);
        }

        var unknownButValid = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.OpenMediaReadAsync(
                id,
                new PortableStagedMediaReference(new string('a', 128))));
        unknownButValid.Code.Should().Be(PortableStagingException.NotFoundCode);

        Directory.EnumerateFileSystemEntries(MediaDirectory(id)).Should().BeEmpty();
        EnumerateFilesOutsideRoot().Should().BeEmpty();
    }

    [Fact]
    public async Task Media_descriptor_paths_never_become_filesystem_paths()
    {
        await using var staging = CreateStaging(NewScope());
        var id = await staging.CreateAsync();
        var content = new byte[] { 4, 2 };
        var descriptor = MediaDescriptor(content) with
        {
            Path = "../../outside-evil.bin",
            FileName = "outside-evil.bin",
        };

        var write = await staging.OpenMediaWriteAsync(id, descriptor);
        await write.Stream.WriteAsync(content);
        await staging.CompleteMediaAsync(id, write);
        await write.DisposeAsync();

        var mediaFiles = Directory.EnumerateFiles(MediaDirectory(id)).ToArray();
        mediaFiles.Should().ContainSingle();
        Path.GetFileName(mediaFiles[0]).Should().Be(write.Reference.Value);

        EnumerateFilesOutsideRoot().Should().BeEmpty();
        File.Exists(Path.Combine(_parent, "outside-evil.bin")).Should().BeFalse();
    }

    [Fact]
    public async Task Over_limit_writes_discard_the_item_and_leave_no_file()
    {
        await using var staging = CreateStaging(NewScope());
        var id = await staging.CreateAsync();
        var content = new byte[] { 1, 2, 3, 4 };
        var write = await staging.OpenMediaWriteAsync(id, MediaDescriptor(content));
        await write.Stream.WriteAsync(content);

        var failure = await Assert.ThrowsAsync<PortableStagingException>(
            () => write.Stream.WriteAsync(new byte[] { 5 }).AsTask());
        failure.Code.Should().Be(PortableStagingException.LimitExceededCode);

        Directory.EnumerateFileSystemEntries(MediaDirectory(id)).Should().BeEmpty();
        (await staging.ListMediaAsync(id)).Should().BeEmpty();
        var complete = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.CompleteMediaAsync(id, write));
        complete.Code.Should().Be(PortableStagingException.NotFoundCode);

        var oversized = new PortableArchivePayload(
            "manifest.json",
            PortableArchiveLimits.MaxManifestBytes + 1,
            new string('a', 64));
        var openFailure = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.OpenManifestWriteAsync(id, oversized));
        openFailure.Code.Should().Be(PortableStagingException.LimitExceededCode);
        Directory.EnumerateFileSystemEntries(AreaDirectory(id), "*", SearchOption.AllDirectories)
            .Should().NotContain(path => path.Contains("manifest", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Delete_removes_the_whole_area_tree_and_is_idempotent()
    {
        await using var staging = CreateStaging(NewScope());
        var id = await staging.CreateAsync();
        var staged = await StageAndCommitAsync(staging, id);
        Directory.Exists(AreaDirectory(id)).Should().BeTrue();

        await staging.DeleteAsync(id);
        Directory.Exists(AreaDirectory(id)).Should().BeFalse();
        await staging.DeleteAsync(id);
        await staging.DeleteAsync(default);

        await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.OpenDataReadAsync(id));
        await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.OpenMediaReadAsync(id, staged.MediaReference));
        await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.RebuildPreparedImportAsync(id));
    }

    [Fact]
    public async Task A_symlinked_media_directory_inside_the_area_is_refused()
    {
        await using var staging = CreateStaging(NewScope());
        var id = await staging.CreateAsync();
        var mediaDirectory = MediaDirectory(id);
        Directory.Delete(mediaDirectory, recursive: true);

        var outside = Path.Combine(_parent, "outside-media");
        Directory.CreateDirectory(outside);
        try
        {
            Directory.CreateSymbolicLink(mediaDirectory, outside);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException)
        {
            return;
        }

        var act = () => staging.OpenMediaWriteAsync(id, MediaDescriptor(new byte[] { 1 }));
        await act.Should().ThrowAsync<TransferPathException>();
        Directory.EnumerateFileSystemEntries(outside).Should().BeEmpty();
    }

    [Fact]
    public async Task A_symlink_planted_at_the_final_media_path_is_refused()
    {
        await using var staging = CreateStaging(NewScope());
        var id = await staging.CreateAsync();
        var content = new byte[] { 5, 5, 5 };
        var write = await staging.OpenMediaWriteAsync(id, MediaDescriptor(content));
        await write.Stream.WriteAsync(content);

        var outside = Path.Combine(_parent, "outside-target.bin");
        await File.WriteAllTextAsync(outside, "outside");
        var finalPath = Path.Combine(MediaDirectory(id), write.Reference.Value);
        try
        {
            File.CreateSymbolicLink(finalPath, outside);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException)
        {
            return;
        }

        var act = () => staging.CompleteMediaAsync(id, write);
        await act.Should().ThrowAsync<TransferPathException>();
        (await File.ReadAllTextAsync(outside)).Should().Be("outside");

        await write.DisposeAsync();
    }

    [Fact]
    public async Task Delete_wins_when_completion_pauses_before_the_area_gate()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hooks = new FilePortableImportStagingHooks
        {
            BeforeAreaGateAsync = async operation =>
            {
                if (operation == FileStagingOperation.CompleteMedia)
                {
                    entered.TrySetResult();
                    await release.Task;
                }
            },
        };

        await using var staging = CreateStaging(NewScope(), hooks);
        var id = await staging.CreateAsync();
        var content = new byte[] { 1, 2, 3 };
        var write = await staging.OpenMediaWriteAsync(id, MediaDescriptor(content));
        await write.Stream.WriteAsync(content);

        var completion = Task.Run(async () =>
        {
            try
            {
                await staging.CompleteMediaAsync(id, write);
                return null;
            }
            catch (PortableStagingException exception)
            {
                return exception;
            }
        });

        await entered.Task;
        await staging.DeleteAsync(id);
        release.TrySetResult();

        var failure = await completion;
        failure.Should().NotBeNull();
        failure!.Code.Should().Be(PortableStagingException.NotFoundCode);

        await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.OpenMediaReadAsync(id, write.Reference));
        await write.DisposeAsync();
    }

    [Fact]
    public async Task Completion_wins_when_delete_pauses_before_the_area_gate()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hooks = new FilePortableImportStagingHooks
        {
            BeforeAreaGateAsync = async operation =>
            {
                if (operation == FileStagingOperation.Delete)
                {
                    entered.TrySetResult();
                    await release.Task;
                }
            },
        };

        await using var staging = CreateStaging(NewScope(), hooks);
        var id = await staging.CreateAsync();
        var content = new byte[] { 4, 5, 6 };
        var write = await staging.OpenMediaWriteAsync(id, MediaDescriptor(content));
        await write.Stream.WriteAsync(content);

        var deletion = Task.Run(() => staging.DeleteAsync(id));

        await entered.Task;
        await staging.CompleteMediaAsync(id, write);
        release.TrySetResult();
        await deletion;

        await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.OpenMediaReadAsync(id, write.Reference));
        await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.RebuildPreparedImportAsync(id));
        await write.DisposeAsync();
    }

    [Fact]
    public async Task A_second_instance_delete_stops_an_in_flight_write()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hooks = new FilePortableImportStagingHooks
        {
            BeforeStreamWrite = () =>
            {
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            },
        };

        await using var first = CreateStaging(string.Empty, hooks);
        await using var second = CreateStaging(string.Empty);
        var id = await first.CreateAsync();
        var write = await first.OpenMediaWriteAsync(id, MediaDescriptor(new byte[4]));

        var writing = Task.Run(async () =>
        {
            try
            {
                await write.Stream.WriteAsync(new byte[4]);
                return null;
            }
            catch (PortableStagingException exception)
            {
                return exception;
            }
        });

        await entered.Task;
        await second.DeleteAsync(id);
        release.TrySetResult();

        var failure = await writing;
        failure.Should().NotBeNull();
        failure!.Code.Should().Be(PortableStagingException.NotFoundCode);

        var complete = await Assert.ThrowsAsync<PortableStagingException>(
            () => first.CompleteMediaAsync(id, write));
        complete.Code.Should().Be(PortableStagingException.NotFoundCode);
        await write.DisposeAsync();
    }

    [Fact]
    public async Task Nothing_is_ever_written_outside_the_transfer_root()
    {
        var stagingResolver = new TransferPathResolver(_root);
        await using var staging = new FilePortableImportStaging(stagingResolver);
        var committedId = await staging.CreateAsync();
        await StageAndCommitAsync(staging, committedId);

        var abandonedId = await staging.CreateAsync();
        var abandoned = await staging.OpenMediaWriteAsync(
            abandonedId,
            MediaDescriptor(new byte[] { 7, 7 }) with
            {
                Path = "../escape.bin",
                FileName = "escape.bin",
            });
        await abandoned.Stream.WriteAsync(new byte[] { 7 });
        await abandoned.DisposeAsync();

        var crashingResolver = new TransferPathResolver(_root);
        await using var crashing = new FilePortableImportStaging(
            crashingResolver,
            CrashAt(FileStagingCrashPoint.AfterMediaRenamedBeforeInventory),
            NewScope());
        var crashingId = await crashing.CreateAsync();
        var crashWrite = await crashing.OpenMediaWriteAsync(
            crashingId,
            MediaDescriptor(new byte[] { 3 }));
        await crashWrite.Stream.WriteAsync(new byte[] { 3 });
        await Assert.ThrowsAsync<FileStagingSimulatedCrashException>(
            () => crashing.CompleteMediaAsync(crashingId, crashWrite));

        var files = Directory.EnumerateFiles(_parent, "*", SearchOption.AllDirectories).ToArray();
        files.Should().NotBeEmpty();
        var rootPrefix = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;
        files.Should().OnlyContain(path => path.StartsWith(rootPrefix, StringComparison.Ordinal));

        await staging.DeleteAsync(committedId);
        await staging.DeleteAsync(abandonedId);
        await crashing.DeleteAsync(crashingId);

        Directory.EnumerateFileSystemEntries(Path.Combine(_root, "staging"))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Corrupted_staged_bytes_fail_rebuild_and_read()
    {
        var scope = NewScope();
        var staging = CreateStaging(scope);
        var id = await staging.CreateAsync();
        var staged = await StageAndCommitAsync(staging, id);
        await staging.DisposeAsync();

        await File.WriteAllBytesAsync(
            Path.Combine(AreaDirectory(id), "data", "library.json"),
            Encoding.UTF8.GetBytes("{\"version\":9}"));
        var mediaPath = Path.Combine(MediaDirectory(id), staged.MediaReference.Value);
        await File.WriteAllBytesAsync(mediaPath, staged.MediaBytes[..^1]);

        await using var fresh = CreateStaging(NewScope());
        var rebuild = await Assert.ThrowsAsync<PortableStagingException>(
            () => fresh.RebuildPreparedImportAsync(id));
        rebuild.Code.Should().Be(PortableStagingException.IntegrityMismatchCode);

        var read = await Assert.ThrowsAsync<PortableStagingException>(
            () => fresh.OpenMediaReadAsync(id, staged.MediaReference));
        read.Code.Should().Be(PortableStagingException.IntegrityMismatchCode);

        await fresh.DeleteAsync(id);
    }

    [Fact]
    public async Task State_file_is_created_empty_and_holds_the_commit_marker_last()
    {
        var scope = NewScope();
        await using var staging = CreateStaging(scope);
        var id = await staging.CreateAsync();

        var created = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(AreaDirectory(id), StateFileName)));
        created.RootElement.GetProperty("Prepared").ValueKind.Should().Be(JsonValueKind.Null);

        var staged = await StageAllWithoutCommitAsync(staging, id);
        var uncommitted = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(AreaDirectory(id), StateFileName)));
        uncommitted.RootElement.GetProperty("Prepared").ValueKind.Should().Be(JsonValueKind.Null);

        await staging.CommitPreparedImportAsync(id, staged.Metadata);

        var committed = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(AreaDirectory(id), StateFileName)));
        committed.RootElement.GetProperty("Prepared").ValueKind.Should().Be(JsonValueKind.Object);

        await using var fresh = CreateStaging(NewScope());
        var rebuilt = await fresh.RebuildPreparedImportAsync(id);
        rebuilt.Metadata.Should().Be(staged.Metadata);
    }

    [Fact]
    public async Task Crash_leftovers_are_ignored_by_list_and_rebuild()
    {
        var staging = CreateStaging(NewScope());
        var id = await staging.CreateAsync();
        var content = new byte[] { 6 };
        var descriptor = MediaDescriptor(content);
        var write = await staging.OpenMediaWriteAsync(id, descriptor);
        await write.Stream.WriteAsync(content);
        await staging.CompleteMediaAsync(id, write);
        await write.DisposeAsync();
        await staging.DisposeAsync();

        // Simulate a previous process that crashed while replacing the inventory.
        await File.WriteAllTextAsync(
            Path.Combine(AreaDirectory(id), StateTempFileName),
            "{ not valid json");

        await using var fresh = CreateStaging(NewScope());
        (await fresh.ListMediaAsync(id)).Should().ContainSingle();
        await Assert.ThrowsAsync<PortableStagingException>(
            () => fresh.RebuildPreparedImportAsync(id));

        await fresh.DeleteAsync(id);
        Directory.Exists(AreaDirectory(id)).Should().BeFalse();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_parent))
                Directory.Delete(_parent, recursive: true);
        }
        catch
        {
            // Test cleanup only.
        }
    }

    private FilePortableImportStaging CreateStaging(string scope) =>
        new(_resolver, hooks: null, scope);

    private FilePortableImportStaging CreateStaging(
        string scope,
        FilePortableImportStagingHooks hooks) =>
        new(_resolver, hooks, scope);

    private static FilePortableImportStagingHooks CrashAt(FileStagingCrashPoint point) =>
        new()
        {
            CrashAt = actual =>
            {
                if (actual == point)
                    throw new FileStagingSimulatedCrashException(actual);
            },
        };

    private static string NewScope() => Guid.NewGuid().ToString("N");

    private string AreaDirectory(PortableStagingId id) =>
        Path.Combine(_root, "staging", id.Value.ToString("N"));

    private string MediaDirectory(PortableStagingId id) =>
        Path.Combine(AreaDirectory(id), "media");

    private IEnumerable<string> EnumerateFilesOutsideRoot()
    {
        var rootPrefix = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;
        return Directory
            .EnumerateFiles(_parent, "*", SearchOption.AllDirectories)
            .Where(path => !path.StartsWith(rootPrefix, StringComparison.Ordinal));
    }

    private static async Task<StagedFileImport> StageAndCommitAsync(
        FilePortableImportStaging staging,
        PortableStagingId id)
    {
        var staged = await StageAllWithoutCommitAsync(staging, id);
        await staging.CommitPreparedImportAsync(id, staged.Metadata);
        return staged;
    }

    private static async Task<StagedFileImport> StageAllWithoutCommitAsync(
        FilePortableImportStaging staging,
        PortableStagingId id)
    {
        var dataBytes = Encoding.UTF8.GetBytes("{\"version\":3}");
        var dataWrite = await staging.OpenDataWriteAsync(
            id,
            new PortableArchivePayload("data/library.json", dataBytes.LongLength, Sha256Hex(dataBytes)));
        await dataWrite.Stream.WriteAsync(dataBytes);
        await staging.CompleteDataAsync(id, dataWrite);
        await dataWrite.DisposeAsync();

        var manifestBytes = Encoding.UTF8.GetBytes("{\"format\":\"nostos-portable\"}");
        var manifestWrite = await staging.OpenManifestWriteAsync(
            id,
            new PortableArchivePayload("manifest.json", manifestBytes.LongLength, Sha256Hex(manifestBytes)));
        await manifestWrite.Stream.WriteAsync(manifestBytes);
        await staging.CompleteManifestAsync(id, manifestWrite);
        await manifestWrite.DisposeAsync();

        var mediaBytes = new byte[] { 9, 8, 7, 6 };
        var mediaDescriptor = MediaDescriptor(mediaBytes);
        var mediaWrite = await staging.OpenMediaWriteAsync(id, mediaDescriptor);
        await mediaWrite.Stream.WriteAsync(mediaBytes);
        await staging.CompleteMediaAsync(id, mediaWrite);
        var mediaReference = mediaWrite.Reference;
        await mediaWrite.DisposeAsync();

        var metadata = new PreparedPortableImportMetadata(
            StagingId: id,
            FormatVersion: 1,
            DataVersion: 3,
            DataBytes: dataBytes.LongLength,
            DataSha256: Sha256Hex(dataBytes),
            Counts: new MigrationArchiveCounts(Books: 1, MediaEntries: 1),
            MediaFiles: 1,
            MediaBytes: mediaBytes.LongLength,
            ArchiveBytes: 4096,
            PreparedAtUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            IntegrityVerified: true);

        return new StagedFileImport(
            metadata,
            mediaReference,
            dataBytes,
            manifestBytes,
            mediaBytes);
    }

    private static PortableArchiveMediaEntry MediaDescriptor(byte[] content) =>
        new(
            BookId: Guid.Parse("33333333-3333-3333-3333-333333333333"),
            Kind: "book",
            Path: "media/book.bin",
            FileName: "book.bin",
            ContentType: "application/octet-stream",
            Length: content.LongLength,
            Sha256: Sha256Hex(content));

    private static string Sha256Hex(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private sealed record StagedFileImport(
        PreparedPortableImportMetadata Metadata,
        PortableStagedMediaReference MediaReference,
        byte[] DataBytes,
        byte[] ManifestBytes,
        byte[] MediaBytes);
}

using FluentAssertions;
using Nostos.Backend.Services.Portability.Transfers;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

// Slice 3 of issue #679: every local transfer path comes from this resolver.
// Hostile persisted keys are rejected, symlinked components below the root are
// refused, and persisted keys are always relative.
public sealed class TransferPathResolverTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _root;
    private readonly TransferPathResolver _resolver;

    public TransferPathResolverTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"nostos-679-paths-{Guid.NewGuid():N}");
        _root = Path.Combine(_tempRoot, "transfers");
        Directory.CreateDirectory(_root);
        _resolver = new TransferPathResolver(_root);
    }

    [Fact]
    public void Generated_paths_stay_strictly_under_the_root()
    {
        var sessionId = Guid.NewGuid();
        var stagingId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var prefix = _resolver.RootPath + Path.DirectorySeparatorChar;

        _resolver.GetUploadSessionDirectory(sessionId).Should().StartWith(prefix);
        _resolver.GetUploadArchivePartPath(sessionId).Should().StartWith(prefix);
        _resolver.GetUploadArchivePath(sessionId).Should().StartWith(prefix);
        _resolver.GetUploadChunkTempPath(sessionId, 7).Should().StartWith(prefix);
        _resolver.GetStagingDirectory(stagingId).Should().StartWith(prefix);
        _resolver.GetStagingMediaDirectory(stagingId).Should().StartWith(prefix);
        _resolver.GetExportDirectory(jobId).Should().StartWith(prefix);
        _resolver.GetExportTempPath(jobId).Should().StartWith(prefix);
        _resolver.GetExportArtifactPath(jobId).Should().StartWith(prefix);
    }

    [Fact]
    public void Persisted_keys_are_relative_and_never_contain_the_absolute_root()
    {
        var sessionId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        var archiveKey = _resolver.GetUploadArchiveStorageKey(sessionId);
        archiveKey.Should().Be($"uploads/{sessionId:N}/archive.nostos");

        var exportKey = _resolver.GetExportArtifactStorageKey(jobId);
        exportKey.Should().Be($"exports/{jobId:N}/library.nostos");

        var fromPath = _resolver.ToStorageKey(_resolver.GetUploadArchivePartPath(sessionId));
        fromPath.Should().Be($"uploads/{sessionId:N}/archive.part");

        foreach (var key in new[] { archiveKey, exportKey, fromPath })
        {
            key.Should().NotContain(_resolver.RootPath);
            Path.IsPathRooted(key).Should().BeFalse();
        }
    }

    [Fact]
    public void Valid_storage_keys_round_trip_to_generated_paths()
    {
        var sessionId = Guid.NewGuid();
        var stagingId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        _resolver.ResolveStorageKey($"uploads/{sessionId:N}/archive.part")
            .Should().Be(_resolver.GetUploadArchivePartPath(sessionId));
        _resolver.ResolveStorageKey($"staging/{stagingId:N}/state.json")
            .Should().Be(Path.Combine(_resolver.GetStagingDirectory(stagingId), "state.json"));
        _resolver.ResolveStorageKey($"exports/{jobId:N}/library.nostos")
            .Should().Be(_resolver.GetExportArtifactPath(jobId));
        _resolver.ResolveStorageKey($"uploads/{sessionId:N}/.chunk-00000007.abc.tmp")
            .Should().Be(Path.Combine(_resolver.GetUploadSessionDirectory(sessionId), ".chunk-00000007.abc.tmp"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("whitespace")]
    [InlineData("dot")]
    [InlineData("dotdot")]
    [InlineData("traversal")]
    [InlineData("nested-traversal")]
    [InlineData("rooted-unix")]
    [InlineData("rooted-windows")]
    [InlineData("backslash")]
    [InlineData("dotted-segment")]
    [InlineData("empty-segment")]
    [InlineData("trailing-separator")]
    [InlineData("reserved-nul")]
    [InlineData("reserved-com1-with-extension")]
    [InlineData("trailing-dot")]
    [InlineData("trailing-space")]
    [InlineData("colon")]
    [InlineData("star")]
    [InlineData("control-character")]
    [InlineData("nul-character")]
    [InlineData("oversized-segment")]
    [InlineData("oversized-key")]
    public void Hostile_storage_keys_are_rejected(string kind)
    {
        var key = kind switch
        {
            "null" => null,
            "empty" => string.Empty,
            "whitespace" => "   ",
            "dot" => ".",
            "dotdot" => "..",
            "traversal" => "../escape",
            "nested-traversal" => "uploads/../../escape",
            "rooted-unix" => "/etc/passwd",
            "rooted-windows" => "C:/Windows/System32/config",
            "backslash" => @"uploads\archive.part",
            "dotted-segment" => "uploads/./archive.part",
            "empty-segment" => "uploads//archive.part",
            "trailing-separator" => "uploads/",
            "reserved-nul" => "uploads/NUL/archive.part",
            "reserved-com1-with-extension" => "uploads/com1.txt",
            "trailing-dot" => "uploads/archive.",
            "trailing-space" => "uploads/archive ",
            "colon" => "uploads/a:b",
            "star" => "uploads/a*b",
            "control-character" => "uploads/a\u0001b",
            "nul-character" => "uploads/a\u0000b",
            "oversized-segment" => "uploads/" + new string('a', 256),
            "oversized-key" => new string('a', 513),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        var resolve = () => _resolver.ResolveStorageKey(key);
        resolve.Should().Throw<TransferPathException>()
            .Which.Code.Should().Be(TransferPathException.InvalidPath);

        _resolver.TryResolveStorageKey(key, out var resolved).Should().BeFalse();
        resolved.Should().BeEmpty();
    }

    [Fact]
    public void ToStorageKey_rejects_paths_outside_the_root_and_the_root_itself()
    {
        var outside = Path.Combine(_tempRoot, "outside.txt");

        var actOutside = () => _resolver.ToStorageKey(outside);
        actOutside.Should().Throw<TransferPathException>()
            .Which.Code.Should().Be(TransferPathException.OutsideRoot);

        var actRoot = () => _resolver.ToStorageKey(_resolver.RootPath);
        actRoot.Should().Throw<TransferPathException>()
            .Which.Code.Should().Be(TransferPathException.OutsideRoot);
    }

    [Fact]
    public void EnsureDirectoryExists_creates_nested_directories_below_the_root()
    {
        var stagingId = Guid.NewGuid();
        var directory = _resolver.GetStagingMediaDirectory(stagingId);

        _resolver.EnsureDirectoryExists(directory).Should().Be(directory);
        Directory.Exists(directory).Should().BeTrue();

        var parentOfFile = _resolver.EnsureParentDirectoryExists(
            _resolver.GetUploadArchivePartPath(Guid.NewGuid()));
        Directory.Exists(parentOfFile).Should().BeTrue();
    }

    [Fact]
    public void A_symlinked_transfer_subdirectory_pointing_outside_is_refused()
    {
        var outside = Path.Combine(_tempRoot, "outside");
        Directory.CreateDirectory(outside);
        var link = Path.Combine(_root, TransferPathResolver.UploadsDirectoryName);
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException)
        {
            // The OS forbids creating symlinks in this environment; the guard
            // itself is exercised wherever links are supported.
            return;
        }

        var sessionId = Guid.NewGuid();

        var resolve = () => _resolver.ResolveStorageKey($"uploads/{sessionId:N}/archive.part");
        resolve.Should().Throw<TransferPathException>()
            .Which.Code.Should().Be(TransferPathException.ReparsePoint);

        var ensure = () => _resolver.EnsureDirectoryExists(_resolver.GetUploadSessionDirectory(sessionId));
        ensure.Should().Throw<TransferPathException>()
            .Which.Code.Should().Be(TransferPathException.ReparsePoint);
    }

    [Fact]
    public void A_symlinked_leaf_directory_pointing_outside_is_refused()
    {
        var stagingId = Guid.NewGuid();
        var stagingDirectory = _resolver.GetStagingDirectory(stagingId);
        Directory.CreateDirectory(stagingDirectory);

        var outside = Path.Combine(_tempRoot, "outside-media");
        Directory.CreateDirectory(outside);
        var mediaDirectory = _resolver.GetStagingMediaDirectory(stagingId);
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

        var ensure = () => _resolver.EnsureDirectoryExists(mediaDirectory);
        ensure.Should().Throw<TransferPathException>()
            .Which.Code.Should().Be(TransferPathException.ReparsePoint);
    }

    [Fact]
    public void A_symlinked_file_is_refused_before_an_overwrite()
    {
        var outside = Path.Combine(_tempRoot, "outside.txt");
        File.WriteAllText(outside, "outside");
        var link = Path.Combine(_root, TransferPathResolver.ExportsDirectoryName);
        Directory.CreateDirectory(link);
        var fileLink = Path.Combine(link, "link.txt");
        try
        {
            File.CreateSymbolicLink(fileLink, outside);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException)
        {
            return;
        }

        var ensure = () => _resolver.EnsureFileIsNotReparsePoint(fileLink);
        ensure.Should().Throw<TransferPathException>()
            .Which.Code.Should().Be(TransferPathException.ReparsePoint);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData(@"a\b")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("UPPERCASE")]
    [InlineData("not-hex-z")]
    public void Hostile_media_references_are_rejected(string? mediaReference)
    {
        var act = () => _resolver.GetStagingMediaPath(Guid.NewGuid(), mediaReference!);

        act.Should().Throw<TransferPathException>()
            .Which.Code.Should().Be(TransferPathException.InvalidPath);
    }

    [Fact]
    public void A_valid_media_reference_resolves_under_the_staging_media_directory()
    {
        var stagingId = Guid.NewGuid();
        var reference = Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant() + "00";

        var path = _resolver.GetStagingMediaPath(stagingId, reference);

        path.Should().Be(Path.Combine(_resolver.GetStagingMediaDirectory(stagingId), reference));
        TransferPathResolver.IsValidOpaqueToken(reference).Should().BeTrue();
    }

    [Fact]
    public void Root_construction_rejects_empty_and_filesystem_root_paths()
    {
        var actEmpty = () => new TransferPathResolver("   ");
        actEmpty.Should().Throw<ArgumentException>();

        var actRoot = () => new TransferPathResolver(Path.GetPathRoot(_root)!);
        actRoot.Should().Throw<TransferPathException>()
            .Which.Code.Should().Be(TransferPathException.InvalidPath);
    }

    [Fact]
    public void Empty_scope_identifiers_are_rejected()
    {
        var actSession = () => _resolver.GetUploadSessionDirectory(Guid.Empty);
        actSession.Should().Throw<ArgumentException>();

        var actStaging = () => _resolver.GetStagingDirectory(Guid.Empty);
        actStaging.Should().Throw<ArgumentException>();

        var actExport = () => _resolver.GetExportDirectory(Guid.Empty);
        actExport.Should().Throw<ArgumentException>();

        var actChunk = () => _resolver.GetUploadChunkTempPath(Guid.NewGuid(), -1);
        actChunk.Should().Throw<ArgumentOutOfRangeException>();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup only.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup only.
        }
    }
}

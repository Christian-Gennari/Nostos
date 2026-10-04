using FluentAssertions;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Transfers;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

// Slice 3 of issue #679: the transfer root defaults beside the resolved books
// root, unsafe roots fail closed, and every configured bound is validated
// before admission can run.
public sealed class TransferStorageOptionsTests
{
    [Fact]
    public void Defaults_match_the_plan_values()
    {
        var options = new TransferStorageOptions();

        options.TransferPath.Should().BeNull();
        options.ChunkBytes.Should().Be(MigrationContractLimits.DefaultChunkBytes);
        options.MinChunkBytes.Should().Be(MigrationContractLimits.MinChunkBytes);
        options.MaxChunkBytes.Should().Be(MigrationContractLimits.MaxChunkBytes);
        options.MaxConcurrentJobs.Should().Be(1);
        options.DiskSafetyMarginBytes.Should().Be(1024L * 1024 * 1024);
        options.DiskSafetyMarginPercent.Should().Be(5);
        options.PreflightReservationMinutes.Should().Be(15);
        options.ExportRetentionHours.Should().Be(24);
        options.PreparedImportRetentionHours.Should().Be(24);
        options.CleanupIntervalMinutes.Should().Be(15);
    }

    [Fact]
    public void ResolveRoot_defaults_to_a_transfers_directory_beside_the_books_root()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), $"nostos-content-{Guid.NewGuid():N}");
        var booksRoot = Path.Combine(contentRoot, "Storage", "books");

        var resolved = TransferStorageOptions.ResolveRoot(contentRoot, booksRoot, new TransferStorageOptions());

        resolved.Should().Be(Path.GetFullPath(Path.Combine(contentRoot, "Storage", "transfers")));
        TransferStorageOptions.IsUnsafeRoot(resolved, contentRoot).Should().BeFalse();
    }

    [Fact]
    public void ResolveRoot_follows_a_custom_books_volume_by_default()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), $"nostos-content-{Guid.NewGuid():N}");
        var booksRoot = Path.Combine(Path.GetTempPath(), "library-volume", "books");

        var resolved = TransferStorageOptions.ResolveRoot(contentRoot, booksRoot, new TransferStorageOptions());

        resolved.Should().Be(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "library-volume", "transfers")));
    }

    [Fact]
    public void ResolveRoot_honors_configured_relative_and_absolute_paths()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), $"nostos-content-{Guid.NewGuid():N}");
        var booksRoot = Path.Combine(contentRoot, "Storage", "books");

        var relative = TransferStorageOptions.ResolveRoot(
            contentRoot,
            booksRoot,
            new TransferStorageOptions { TransferPath = Path.Combine("scratch", "transfers") });
        relative.Should().Be(Path.GetFullPath(Path.Combine(contentRoot, "scratch", "transfers")));

        var absolutePath = Path.Combine(Path.GetTempPath(), $"nostos-abs-transfers-{Guid.NewGuid():N}");
        var absolute = TransferStorageOptions.ResolveRoot(
            contentRoot,
            booksRoot,
            new TransferStorageOptions { TransferPath = absolutePath });
        absolute.Should().Be(Path.GetFullPath(absolutePath));
    }

    [Theory]
    [InlineData("root")]
    [InlineData("content")]
    [InlineData("ancestor")]
    public void ResolveRoot_rejects_unsafe_broad_roots(string unsafeKind)
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), $"nostos-content-{Guid.NewGuid():N}");
        var booksRoot = Path.Combine(contentRoot, "Storage", "books");
        var configured = unsafeKind switch
        {
            "root" => Path.GetPathRoot(contentRoot)!,
            "content" => contentRoot,
            "ancestor" => Path.GetDirectoryName(contentRoot)!,
            _ => throw new ArgumentOutOfRangeException(nameof(unsafeKind)),
        };

        var resolve = () => TransferStorageOptions.ResolveRoot(
            contentRoot,
            booksRoot,
            new TransferStorageOptions { TransferPath = configured });

        resolve.Should().Throw<InvalidOperationException>()
            .WithMessage("*unsafe*");
    }

    [Fact]
    public void Validate_accepts_the_default_configuration()
    {
        var validate = () => TransferStorageOptions.Validate(new TransferStorageOptions());

        validate.Should().NotThrow();
    }

    [Fact]
    public void Validate_rejects_chunk_sizes_outside_the_shared_contract_limits()
    {
        var tooSmall = new TransferStorageOptions
        {
            MinChunkBytes = MigrationContractLimits.MinChunkBytes,
            MaxChunkBytes = MigrationContractLimits.MaxChunkBytes,
            ChunkBytes = MigrationContractLimits.MinChunkBytes - 1,
        };
        var actTooSmall = () => TransferStorageOptions.Validate(tooSmall);
        actTooSmall.Should().Throw<InvalidOperationException>().WithMessage("*ChunkBytes*");

        var tooLarge = new TransferStorageOptions
        {
            MinChunkBytes = MigrationContractLimits.MinChunkBytes,
            MaxChunkBytes = MigrationContractLimits.MaxChunkBytes,
            ChunkBytes = MigrationContractLimits.MaxChunkBytes + 1,
        };
        var actTooLarge = () => TransferStorageOptions.Validate(tooLarge);
        actTooLarge.Should().Throw<InvalidOperationException>().WithMessage("*ChunkBytes*");

        var inverted = new TransferStorageOptions
        {
            MinChunkBytes = MigrationContractLimits.DefaultChunkBytes,
            MaxChunkBytes = MigrationContractLimits.MinChunkBytes,
        };
        var actInverted = () => TransferStorageOptions.Validate(inverted);
        actInverted.Should().Throw<InvalidOperationException>().WithMessage("*MinChunkBytes*");
    }

    [Theory]
    [InlineData("maxConcurrentJobs")]
    [InlineData("marginBytes")]
    [InlineData("marginPercent")]
    [InlineData("preflightMinutes")]
    [InlineData("exportHours")]
    [InlineData("preparedImportHours")]
    [InlineData("cleanupMinutes")]
    public void Validate_rejects_impossible_operational_bounds(string field)
    {
        var options = new TransferStorageOptions();
        switch (field)
        {
            case "maxConcurrentJobs":
                options.MaxConcurrentJobs = 0;
                break;
            case "marginBytes":
                options.DiskSafetyMarginBytes = -1;
                break;
            case "marginPercent":
                options.DiskSafetyMarginPercent = 101;
                break;
            case "preflightMinutes":
                options.PreflightReservationMinutes = 0;
                break;
            case "exportHours":
                options.ExportRetentionHours = 0;
                break;
            case "preparedImportHours":
                options.PreparedImportRetentionHours = 0;
                break;
            case "cleanupMinutes":
                options.CleanupIntervalMinutes = 0;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(field));
        }

        var validate = () => TransferStorageOptions.Validate(options);

        validate.Should().Throw<InvalidOperationException>();
    }
}

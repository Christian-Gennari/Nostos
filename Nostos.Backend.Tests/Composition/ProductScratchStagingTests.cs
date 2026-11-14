using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Tests.Portability;
using Nostos.Backend.Tests.Support;
using Nostos.Product.Composition;
using Nostos.Product.Storage;
using Xunit;

namespace Nostos.Backend.Tests.Composition;

/// <summary>
/// One product-level scratch seam for everything large the product stages
/// (issue #1596): BookText sources, passage EPUBs, alignment publications,
/// provider snapshots and portable import spools. These tests pin the seam
/// itself, the product default, and a host override; each caller's own tests
/// pin that it stages through it.
/// </summary>
public sealed class ProductScratchStagingTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"nostos-product-scratch-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    [Fact]
    public void Scratch_storage_creates_unique_entries_under_one_root()
    {
        var scratch = new ProductScratchStorage(Path.Combine(_root, "scratch"));

        Directory.Exists(scratch.Root).Should().BeFalse("nothing is created until something is staged");

        var directory = scratch.CreateDirectory("nostos-test");
        var file = scratch.CreateFilePath("nostos-test", "epub");
        var other = scratch.CreateFilePath("nostos-test", ".epub");

        Directory.Exists(scratch.Root).Should().BeTrue();
        Directory.Exists(directory).Should().BeTrue();
        Path.GetDirectoryName(directory).Should().Be(scratch.Root);
        Path.GetFileName(directory).Should().StartWith("nostos-test-");
        Path.GetFileName(file).Should().StartWith("nostos-test-");
        file.Should().EndWith(".epub");
        Path.GetDirectoryName(file).Should().Be(scratch.Root);
        file.Should().NotBe(other);
        File.Exists(file).Should().BeFalse("the caller creates the file");
    }

    [Fact]
    public void The_product_default_is_still_the_process_temp_path()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNostosProduct(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();

        // Nostos-Cloud has not opted in yet: until it registers its own root it
        // keeps exactly the placement it had before the seam existed.
        provider.GetRequiredService<IProductScratchStorage>().Root
            .Should().Be(Path.GetFullPath(Path.GetTempPath()));
    }

    [Fact]
    public void A_host_registration_before_the_product_wins()
    {
        var host = new ProductScratchStorage(Path.Combine(_root, "host-scratch"));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IProductScratchStorage>(host);
        services.AddNostosProduct(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IProductScratchStorage>().Should().BeSameAs(host);
    }

    [Fact]
    public async Task Portable_import_spools_under_the_host_root_and_deletes_its_scratch_on_failure()
    {
        await using var destination = await LocalPortableTestLibrary.CreateAsync();
        var scratch = new RecordingProductScratchStorage(Path.Combine(_root, "portable"));

        var importer = new PortableArchiveService(
            destination.Db,
            destination.Storage,
            NullLogger<PortableArchiveService>.Instance,
            scratchStorage: scratch);

        // Not a .nostos archive: the import fails before it can mutate the
        // destination, after the scratch directory and its lease exist.
        using var corrupt = new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8]);

        var action = () => importer.ImportAsync(corrupt);

        await action.Should().ThrowAsync<PortableArchiveException>();

        // The spool root exists only because this import created its scratch
        // directory under the host root, and a failed import leaves nothing.
        Directory.Exists(scratch.Root).Should().BeTrue();
        scratch.Leftovers().Should().BeEmpty("a failed import deletes its scratch directory");
    }
}

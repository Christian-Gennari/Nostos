using FluentAssertions;
using Microsoft.Extensions.Options;
using Nostos.Backend.Services.Calibre;
using Nostos.Backend.Services.Library.Imports;
using Xunit;

namespace Nostos.Backend.Tests.Calibre;

public sealed class CalibreFolderSourceTests
{
    [Fact]
    public void A_relative_library_root_is_a_configuration_error()
    {
        var create = () => new CalibreFolderSource(Options.Create(new CalibreOptions
        {
            LibraryRoot = "books/calibre",
        }));

        create.Should().Throw<InvalidOperationException>()
            .WithMessage("*Calibre:LibraryRoot*absolute*");
    }

    [Fact]
    public void No_library_root_means_the_feature_is_off_not_broken()
    {
        var source = new CalibreFolderSource(Options.Create(new CalibreOptions()));

        source.IsConfigured.Should().BeFalse();
        source.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task A_folder_whose_metadata_db_is_not_calibre_says_so()
    {
        var directory = Directory.CreateTempSubdirectory("nostos-calibre-bad-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "metadata.db"), "not a database");
            var source = new CalibreFolderSource(Options.Create(new CalibreOptions
            {
                LibraryRoot = directory,
            }));

            source.IsConfigured.Should().BeTrue();
            source.IsAvailable.Should().BeTrue();

            var read = () => source.ReadAsync(CancellationToken.None);
            await read.Should().ThrowAsync<CalibreLibraryException>()
                .WithMessage("*readable Calibre library*");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

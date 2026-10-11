using FluentAssertions;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Migration;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// A 432 MB import showed "100% · 8.1 KiB of 8.1 KiB" and then dropped to
/// "0% · 0 B of 411.9 MiB": the data-validation phase was forwarded with its
/// own tiny total. The bar must only ever be measured against real work.
/// </summary>
public sealed class MigrationArchiveProgressMappingTests
{
    [Fact]
    public void Data_validation_is_reported_without_a_total()
    {
        var progress = MigrationArchiveProgressMapping.ToJobProgress(
            MigrationProgressPhase.Preparing,
            new PortableArchiveProgress(PortableArchiveProgressPhase.ValidatingData, 8_300, 8_300));

        progress.Phase.Should().Be(MigrationProgressPhase.Preparing);
        progress.TotalBytes.Should().BeNull();
        progress.BytesProcessed.Should().Be(0);
    }

    [Theory]
    [InlineData(PortableArchiveProgressPhase.StagingMedia)]
    [InlineData(PortableArchiveProgressPhase.InspectingArchive)]
    [InlineData(PortableArchiveProgressPhase.WritingArchive)]
    public void Measured_phases_keep_their_bytes_and_items(PortableArchiveProgressPhase phase)
    {
        var progress = MigrationArchiveProgressMapping.ToJobProgress(
            MigrationProgressPhase.Preparing,
            new PortableArchiveProgress(phase, 200, 1_000, ItemsProcessed: 3, TotalItems: 8, Message: "note"));

        progress.BytesProcessed.Should().Be(200);
        progress.TotalBytes.Should().Be(1_000);
        progress.CompletedChunks.Should().Be(3);
        progress.TotalChunks.Should().Be(8);
        progress.Message.Should().BeNull("a host opts in to forwarding engine messages");
    }

    [Fact]
    public void Item_counts_are_omitted_when_the_engine_has_no_total()
    {
        var progress = MigrationArchiveProgressMapping.ToJobProgress(
            MigrationProgressPhase.Validating,
            new PortableArchiveProgress(PortableArchiveProgressPhase.StagingMedia, 5, 10, ItemsProcessed: 2),
            includeMessage: true);

        progress.CompletedChunks.Should().BeNull();
        progress.TotalChunks.Should().BeNull();
    }
}

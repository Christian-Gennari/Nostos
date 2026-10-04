using FluentAssertions;
using Nostos.Backend.Services.Portability;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

public sealed class MigrationContractLimitsTests
{
    [Fact]
    public void Chunk_size_constants_are_locked_to_the_migration_contract()
    {
        MigrationContractLimits.MinChunkBytes.Should().Be(4 * 1024 * 1024);
        MigrationContractLimits.DefaultChunkBytes.Should().Be(16 * 1024 * 1024);
        MigrationContractLimits.MaxChunkBytes.Should().Be(64 * 1024 * 1024);

        MigrationContractLimits.DefaultChunkBytes.Should().BeInRange(
            MigrationContractLimits.MinChunkBytes,
            MigrationContractLimits.MaxChunkBytes);
    }

    [Fact]
    public void Chunk_size_validation_accepts_bounds_and_rejects_just_outside_each_bound()
    {
        MigrationContractLimits.IsValidChunkBytes(MigrationContractLimits.MinChunkBytes).Should().BeTrue();
        MigrationContractLimits.IsValidChunkBytes(MigrationContractLimits.DefaultChunkBytes).Should().BeTrue();
        MigrationContractLimits.IsValidChunkBytes(MigrationContractLimits.MaxChunkBytes).Should().BeTrue();

        MigrationContractLimits.IsValidChunkBytes(MigrationContractLimits.MinChunkBytes - 1).Should().BeFalse();
        MigrationContractLimits.IsValidChunkBytes(MigrationContractLimits.MaxChunkBytes + 1).Should().BeFalse();
        MigrationContractLimits.IsValidChunkBytes(0).Should().BeFalse();
        MigrationContractLimits.IsValidChunkBytes(-1).Should().BeFalse();
    }
}

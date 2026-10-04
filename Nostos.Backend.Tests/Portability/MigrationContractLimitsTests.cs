using FluentAssertions;
using Nostos.Backend.Services.Portability;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

public sealed class MigrationContractLimitsTests
{
    [Fact]
    public void Nominal_session_chunk_size_is_locked_to_the_migration_contract()
    {
        MigrationContractLimits.MinChunkBytes.Should().Be(4 * 1024 * 1024);
        MigrationContractLimits.DefaultChunkBytes.Should().Be(16 * 1024 * 1024);
        MigrationContractLimits.MaxChunkBytes.Should().Be(64 * 1024 * 1024);

        MigrationContractLimits.DefaultChunkBytes.Should().BeInRange(
            MigrationContractLimits.MinChunkBytes,
            MigrationContractLimits.MaxChunkBytes);

        MigrationContractLimits.IsValidChunkSize(MigrationContractLimits.MinChunkBytes).Should().BeTrue();
        MigrationContractLimits.IsValidChunkSize(MigrationContractLimits.DefaultChunkBytes).Should().BeTrue();
        MigrationContractLimits.IsValidChunkSize(MigrationContractLimits.MaxChunkBytes).Should().BeTrue();

        MigrationContractLimits.IsValidChunkSize(MigrationContractLimits.MinChunkBytes - 1).Should().BeFalse();
        MigrationContractLimits.IsValidChunkSize(MigrationContractLimits.MaxChunkBytes + 1).Should().BeFalse();
        MigrationContractLimits.IsValidChunkSize(0).Should().BeFalse();
        MigrationContractLimits.IsValidChunkSize(-1).Should().BeFalse();
    }

    [Fact]
    public void Short_final_chunk_is_legal_and_zero_length_chunk_is_not()
    {
        var chunkSize = MigrationContractLimits.DefaultChunkBytes;

        // A whole file smaller than MinChunkBytes is one legal short final chunk.
        MigrationContractLimits.IsValidChunkBytes(1, chunkSize, isFinalChunk: true).Should().BeTrue();
        MigrationContractLimits.IsValidChunkBytes(512 * 1024, chunkSize, isFinalChunk: true).Should().BeTrue();
        MigrationContractLimits.IsValidChunkBytes(MigrationContractLimits.MinChunkBytes - 1, chunkSize, isFinalChunk: true)
            .Should()
            .BeTrue("the final chunk may be smaller than the nominal minimum");
        MigrationContractLimits.IsValidChunkBytes(chunkSize, chunkSize, isFinalChunk: true).Should().BeTrue();

        MigrationContractLimits.IsValidChunkBytes(0, chunkSize, isFinalChunk: true).Should().BeFalse();
        MigrationContractLimits.IsValidChunkBytes(0, chunkSize, isFinalChunk: false).Should().BeFalse();
    }

    [Fact]
    public void Non_final_chunk_must_equal_the_session_chunk_size_and_bounds_apply_to_both()
    {
        var chunkSize = MigrationContractLimits.DefaultChunkBytes;

        MigrationContractLimits.IsValidChunkBytes(chunkSize, chunkSize, isFinalChunk: false).Should().BeTrue();
        MigrationContractLimits.IsValidChunkBytes(chunkSize - 1, chunkSize, isFinalChunk: false).Should().BeFalse();
        MigrationContractLimits.IsValidChunkBytes(chunkSize + 1, chunkSize, isFinalChunk: false).Should().BeFalse();

        // Below-minimum payloads are illegal for non-final chunks and for an
        // invalid nominal session size.
        MigrationContractLimits.IsValidChunkBytes(MigrationContractLimits.MinChunkBytes - 1, chunkSize, isFinalChunk: false)
            .Should()
            .BeFalse();
        MigrationContractLimits.IsValidChunkBytes(
                MigrationContractLimits.MinChunkBytes - 1,
                MigrationContractLimits.MinChunkBytes - 1,
                isFinalChunk: false)
            .Should()
            .BeFalse();

        // Above-maximum payloads are illegal for non-final and final chunks alike.
        MigrationContractLimits.IsValidChunkBytes(
                MigrationContractLimits.MaxChunkBytes + 1,
                MigrationContractLimits.MaxChunkBytes,
                isFinalChunk: false)
            .Should()
            .BeFalse();
        MigrationContractLimits.IsValidChunkBytes(
                MigrationContractLimits.MaxChunkBytes + 1,
                MigrationContractLimits.MaxChunkBytes,
                isFinalChunk: true)
            .Should()
            .BeFalse();

        // A final chunk cannot exceed its own session chunk size.
        MigrationContractLimits.IsValidChunkBytes(chunkSize + 1, chunkSize, isFinalChunk: true).Should().BeFalse();
    }
}

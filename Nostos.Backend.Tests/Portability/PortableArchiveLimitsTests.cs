using FluentAssertions;
using Nostos.Backend.Services.Portability;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

public sealed class PortableArchiveLimitsTests
{
    [Fact]
    public void Portable_archive_limits_match_migration_contract_limits()
    {
        PortableArchiveLimits.MaxArchiveEntries.Should().Be(MigrationContractLimits.MaxArchiveEntries);
        PortableArchiveLimits.MaxManifestBytes.Should().Be(MigrationContractLimits.MaxManifestBytes);
        PortableArchiveLimits.MaxDataBytes.Should().Be(MigrationContractLimits.MaxDataBytes);
        PortableArchiveLimits.MaxSingleEntryBytes.Should().Be(MigrationContractLimits.MaxSingleEntryBytes);
        PortableArchiveLimits.MaxArchiveBytes.Should().Be(MigrationContractLimits.MaxArchiveBytes);
    }
}

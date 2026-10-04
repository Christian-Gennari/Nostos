namespace Nostos.Backend.Services.Portability;

internal static class PortableArchiveLimits
{
    public const int MaxArchiveEntries = MigrationContractLimits.MaxArchiveEntries;
    public const long MaxManifestBytes = MigrationContractLimits.MaxManifestBytes;
    public const long MaxDataBytes = MigrationContractLimits.MaxDataBytes;
    public const long MaxSingleEntryBytes = MigrationContractLimits.MaxSingleEntryBytes;
    public const long MaxArchiveBytes = MigrationContractLimits.MaxArchiveBytes;

    public const long MaxUncompressedBytes = 1024L * 1024 * 1024 * 1024;
    public const double MaxCompressionRatio = 1000d;
}

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

    public const int MaxExplicitBufferBytes = 64 * 1024 * 1024;
    public const int CopyBufferBytes = 1024 * 1024;
    public const int MaxSynchronousZipWriteBufferBytes = 16 * 1024 * 1024;
    public const int MaxCentralDirectoryBytes = 16 * 1024 * 1024;
    public const int MaxEocdSearchBytes = ushort.MaxValue + 22;
    public const int MaxZip64EndRecordBytes = 64 * 1024;
    public const int MaxPrefetchedZipTailBytes =
        MaxCentralDirectoryBytes + MaxEocdSearchBytes + MaxZip64EndRecordBytes;
    public const int RangeCacheBytes = 16 * 1024 * 1024;
}

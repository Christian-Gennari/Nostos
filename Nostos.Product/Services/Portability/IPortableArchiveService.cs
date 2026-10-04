namespace Nostos.Backend.Services.Portability;

public interface IPortableArchiveService
{
    Task<PortableExportResult> ExportAsync(
        Stream destination,
        CancellationToken cancellationToken = default);

    Task<PortableExportResult> ExportAsync(
        IPortableArchiveSink destination,
        IProgress<PortableArchiveProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<PortableImportResult> ImportAsync(
        Stream source,
        CancellationToken cancellationToken = default);
}

public sealed record PortableArchiveCounts(
    int Works,
    int Books,
    int Collections,
    int BookCollections,
    int Notes,
    int Topics,
    int NoteTopics,
    int Writings,
    int BookAcquisitions);

public sealed record PortableArchivePayload(
    string Path,
    long Length,
    string Sha256);

public sealed record PortableArchiveMediaEntry(
    Guid BookId,
    string Kind,
    string Path,
    string FileName,
    string ContentType,
    long Length,
    string Sha256);

public sealed record PortableArchiveManifest(
    string Format,
    int FormatVersion,
    int DataVersion,
    DateTime ExportedAtUtc,
    string ApplicationVersion,
    PortableArchiveCounts Counts,
    PortableArchivePayload Data,
    IReadOnlyList<PortableArchiveMediaEntry> Media);

public sealed record PortableExportResult(
    int FormatVersion,
    PortableArchiveCounts Counts,
    int MediaFiles,
    long MediaBytes);

public sealed record PortableImportResult(
    int FormatVersion,
    PortableArchiveCounts Counts,
    int MediaFiles,
    long MediaBytes,
    bool IntegrityVerified);

public sealed class PortableArchiveException : Exception
{
    public PortableArchiveException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public PortableArchiveException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

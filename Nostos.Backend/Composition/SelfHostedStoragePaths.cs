using Nostos.Backend.Configuration;
using Nostos.Backend.Services.Library.Imports;
using Nostos.Backend.Services.Portability.Transfers;

namespace Nostos.Backend.Composition;

/// <summary>
/// Canonical SelfHosted filesystem paths resolved once during host composition so
/// persistence, backup settings, media, transfer staging, Calibre uploads, and
/// activation paths never disagree or re-evaluate <see cref="PersistenceRegistration.ResolveDatabasePath"/>.
/// </summary>
public sealed record SelfHostedStoragePaths(
    string ContentRootPath,
    string DatabasePath,
    string BooksRoot,
    string TransferRoot,
    string CalibreUploadRoot)
{
    public static SelfHostedStoragePaths Resolve(IConfiguration configuration, string contentRootPath)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

        var fileStorageOptions = configuration
            .GetSection(FileStorageOptions.SectionName)
            .Get<FileStorageOptions>() ?? new FileStorageOptions();

        var transferStorageOptions = configuration
            .GetSection(TransferStorageOptions.SectionName)
            .Get<TransferStorageOptions>() ?? new TransferStorageOptions();
        TransferStorageOptions.Validate(transferStorageOptions);

        var calibreUploadOptions = configuration
            .GetSection(CalibreUploadOptions.SectionName)
            .Get<CalibreUploadOptions>() ?? new CalibreUploadOptions();
        CalibreUploadOptions.Validate(calibreUploadOptions);

        var databasePath = PersistenceRegistration.ResolveDatabasePath(
            configuration[PersistenceRegistration.DatabasePathConfigurationKey],
            contentRootPath);
        var booksRoot = FileStorageOptions.ResolveBooksRoot(contentRootPath, fileStorageOptions);
        var transferRoot = TransferStorageOptions.ResolveRoot(
            contentRootPath, booksRoot, transferStorageOptions);

        var calibreUploadRoot = string.IsNullOrWhiteSpace(calibreUploadOptions.RootPath)
            ? Path.Combine(transferRoot, "calibre-uploads")
            : Path.GetFullPath(
                Path.IsPathRooted(calibreUploadOptions.RootPath)
                    ? calibreUploadOptions.RootPath
                    : Path.Combine(contentRootPath, calibreUploadOptions.RootPath));

        if (TransferStorageOptions.IsUnsafeRoot(calibreUploadRoot, contentRootPath))
        {
            throw new InvalidOperationException(
                $"'{CalibreUploadOptions.SectionName}:RootPath' must not be a filesystem root, " +
                "the application content root, or an ancestor of the application content root.");
        }

        return new SelfHostedStoragePaths(
            Path.GetFullPath(contentRootPath),
            databasePath,
            booksRoot,
            transferRoot,
            calibreUploadRoot);
    }
}

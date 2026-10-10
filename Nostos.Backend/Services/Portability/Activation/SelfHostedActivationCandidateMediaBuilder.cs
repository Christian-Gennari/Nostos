using System.Security.Cryptography;
using Nostos.Backend.Services.Portability;

namespace Nostos.Backend.Services.Portability.Activation;

/// <summary>
/// Verified, materialized candidate media generation. Byte totals are exact facts
/// for later capacity accounting; no path is persisted in any product DTO.
/// </summary>
internal sealed record SelfHostedActivationCandidateMedia(
    Guid JobId,
    string Root,
    int FileCount,
    long Bytes,
    int CopiedFileCount,
    long CopiedBytes,
    int ReusedFileCount,
    long ReusedBytes);

/// <summary>Deterministic progress over the prepared media inventory.</summary>
internal sealed record SelfHostedActivationCandidateMediaProgress(
    int ItemsCompleted,
    int TotalItems,
    long BytesCompleted,
    long TotalBytes);

/// <summary>
/// Materializes the prepared import's media into the candidate root beside the live
/// media root, in the exact layout the live <c>IBookAssetStorage</c> implementation
/// reads. Isolation is deliberate: no live path is written.
/// </summary>
internal interface ISelfHostedActivationCandidateMediaBuilder
{
    Task<SelfHostedActivationCandidateMedia> BuildMediaAsync(
        Guid jobId,
        IPreparedPortableImport prepared,
        IProgress<SelfHostedActivationCandidateMediaProgress>? progress = null,
        CancellationToken ct = default);
}

/// <summary>
/// Streams every staged media item from <see cref="IPortableImportStaging"/> into
/// <c>&lt;candidate-media-root&gt;/&lt;book-id&gt;/{book,cover,track-NNNN}&lt;ext&gt;</c>, one
/// item at a time through one bounded buffer, hashing while copying and verifying
/// exact length and SHA-256 against the committed descriptor. Each file is written
/// to a sibling <c>.partial</c>, flushed to disk, then renamed into place, so a
/// crash leaves either no candidate file or a complete one. Before copying, every
/// file in the candidate root that is not a planned primary media file is deleted,
/// so reconstructible leftovers (for example <c>cover-thumb-*.webp</c>) and partial
/// scratch from an earlier attempt can never survive into the activated
/// generation. Rerunning after a crash reuses an already-complete, hash-verified
/// file and discards any partial file. Cancellation is cleanup-safe. The builder
/// never touches the live media root and confines every write to the candidate
/// root.
/// </summary>
/// <remarks>
/// Durability: file contents are flushed with <c>Flush(flushToDisk: true)</c>
/// before each rename. <see cref="ActivationFileSystem.Rename"/> fsyncs the source
/// and target parent directories on Linux and uses <c>MoveFileEx</c>
/// <c>WRITE_THROUGH</c> on Windows. The builder does not separately fsync the
/// directory entry of a newly created per-book folder; a process crash that
/// loses that entry simply leaves the candidate media to be rebuilt.
/// </remarks>
internal sealed class SelfHostedActivationCandidateMediaBuilder(
    SelfHostedActivationPaths paths,
    IPortableImportStaging staging) : ISelfHostedActivationCandidateMediaBuilder
{
    private const int CopyBufferBytes = 128 * 1024;
    private const string PartialSuffix = ".partial";
    private const string BookKind = "book";
    private const string CoverKind = "cover";
    private const string TrackKind = "track";

    internal Action<string>? BeforeRenameForTesting { get; set; }

    public async Task<SelfHostedActivationCandidateMedia> BuildMediaAsync(
        Guid jobId,
        IPreparedPortableImport prepared,
        IProgress<SelfHostedActivationCandidateMediaProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (jobId == Guid.Empty)
        {
            throw Failure("Activation identifiers must be generated nonempty GUIDs.");
        }

        var metadata = prepared.Metadata;
        var stagingId = metadata.StagingId;
        if (stagingId.Value == Guid.Empty)
        {
            throw Failure("The prepared import does not identify a staging area.");
        }

        if (metadata.MediaFiles != prepared.Media.Count
            || metadata.MediaBytes != prepared.Media.Sum(item => item.Descriptor.Length))
        {
            throw Failure("The prepared import media inventory does not match its committed descriptor.");
        }

        paths.Verify(jobId);
        var root = paths.CandidateMedia(jobId);
        paths.VerifyMediaPath(root);

        var planned = Plan(jobId, root, prepared.Media);
        var totalBytes = planned.Sum(item => item.Item.Descriptor.Length);

        Directory.CreateDirectory(root);
        paths.VerifyMediaPath(root);
        DeleteUnplannedFiles(root, planned);

        var buffer = new byte[CopyBufferBytes];
        long bytes = 0;
        long copiedBytes = 0;
        long reusedBytes = 0;
        var copiedFiles = 0;
        var reusedFiles = 0;

        foreach (var item in planned)
        {
            ct.ThrowIfCancellationRequested();
            var descriptor = item.Item.Descriptor;

            if (!Directory.Exists(item.FolderPath))
            {
                Directory.CreateDirectory(item.FolderPath);
            }

            paths.VerifyMediaPath(item.FolderPath);
            paths.VerifyMediaPath(item.TargetPath);

            if (File.Exists(item.TargetPath))
            {
                var existing = await HashFileAsync(item.TargetPath, ct).ConfigureAwait(false);
                if (existing.Length == descriptor.Length
                    && HashEquals(existing.Sha256, descriptor.Sha256))
                {
                    reusedFiles++;
                    reusedBytes += existing.Length;
                    bytes += existing.Length;
                    progress?.Report(new SelfHostedActivationCandidateMediaProgress(
                        copiedFiles + reusedFiles,
                        planned.Length,
                        bytes,
                        totalBytes));
                    continue;
                }

                File.Delete(item.TargetPath);
            }

            var temporary = item.TargetPath + PartialSuffix;
            TryDelete(temporary);
            try
            {
                await using (var source = await OpenStagedAsync(stagingId, item, ct).ConfigureAwait(false))
                await using (var destination = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    CopyBufferBytes,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var copied = await CopyAndHashAsync(
                        source,
                        destination,
                        buffer,
                        descriptor.Length,
                        ct).ConfigureAwait(false);
                    await destination.FlushAsync(ct).ConfigureAwait(false);
                    destination.Flush(flushToDisk: true);

                    if (copied.Length != descriptor.Length)
                    {
                        throw Failure("A staged media item length does not match the prepared descriptor.");
                    }

                    if (!HashEquals(copied.Sha256, descriptor.Sha256))
                    {
                        throw Failure("A staged media item failed SHA-256 verification against the prepared descriptor.");
                    }
                }

                BeforeRenameForTesting?.Invoke(temporary);
                paths.VerifyMediaPath(temporary);
                paths.VerifyMediaPath(item.TargetPath);
                ActivationFileSystem.Rename(temporary, item.TargetPath);

                copiedFiles++;
                copiedBytes += descriptor.Length;
                bytes += descriptor.Length;
                progress?.Report(new SelfHostedActivationCandidateMediaProgress(
                    copiedFiles + reusedFiles,
                    planned.Length,
                    bytes,
                    totalBytes));
            }
            catch
            {
                TryDelete(temporary);
                throw;
            }
        }

        return new SelfHostedActivationCandidateMedia(
            jobId,
            root,
            planned.Length,
            bytes,
            copiedFiles,
            copiedBytes,
            reusedFiles,
            reusedBytes);
    }

    private void DeleteUnplannedFiles(string root, IReadOnlyList<PlannedMedia> planned)
    {
        var fullRoot = Path.GetFullPath(root);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var targets = new HashSet<string>(
            planned.Select(item => item.TargetPath),
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false,
        };

        foreach (var file in Directory.EnumerateFiles(fullRoot, "*", options))
        {
            var full = Path.GetFullPath(file);
            if (!full.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison))
            {
                throw Failure("A candidate media file resolves outside the candidate media root.");
            }

            if (targets.Contains(full))
            {
                continue;
            }

            paths.VerifyMediaPath(full);
            File.Delete(full);
        }
    }

    private PlannedMedia[] Plan(
        Guid jobId,
        string root,
        IReadOnlyList<PortablePreparedMedia> media)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var targets = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var keys = new HashSet<(Guid BookId, string Kind, string Discriminator)>();
        var planned = new PlannedMedia[media.Count];

        for (var index = 0; index < media.Count; index++)
        {
            var item = media[index];
            if (item is null)
            {
                throw Failure("The prepared import media inventory is malformed.");
            }

            var descriptor = item.Descriptor;
            var entityId = descriptor.BookId.ToString("D");

            if (descriptor.BookId == Guid.Empty)
            {
                throw Failure("A prepared media item has an empty book identifier.");
            }

            if (descriptor.Kind is not (BookKind or CoverKind or TrackKind))
            {
                throw Failure($"Book {entityId} has a prepared media item with an unsupported kind.");
            }

            if (string.IsNullOrWhiteSpace(descriptor.FileName)
                || Path.GetFileName(descriptor.FileName) != descriptor.FileName
                || descriptor.FileName.Contains('\\')
                || descriptor.FileName.Contains('/'))
            {
                throw Failure($"Book {entityId} has a prepared media item with an unsafe filename.");
            }

            string canonicalFileName;
            try
            {
                canonicalFileName = descriptor.Kind switch
                {
                    BookKind => BookKind + BookAssetFormats.RequireBookExtension(descriptor.FileName),
                    CoverKind => CoverKind + BookAssetFormats.RequireCoverExtension(descriptor.FileName),
                    // A track keeps its own numbered name: a book has many.
                    _ => BookTrackFormats.TryParseCanonicalFileName(
                            descriptor.FileName.ToLowerInvariant(), out var trackNumber)
                        ? BookTrackFormats.CanonicalFileName(trackNumber, Path.GetExtension(descriptor.FileName))
                        : throw new InvalidOperationException("Not a canonical track file name."),
                };
            }
            catch (InvalidOperationException)
            {
                throw Failure($"Book {entityId} has a prepared media item with an unsupported extension.");
            }

            if (!string.Equals(descriptor.FileName, canonicalFileName, StringComparison.OrdinalIgnoreCase))
            {
                throw Failure($"Book {entityId} has a prepared media item filename that is not canonical.");
            }

            if (!string.Equals(
                    descriptor.Path,
                    $"media/books/{descriptor.BookId:N}/{canonicalFileName}",
                    StringComparison.Ordinal))
            {
                throw Failure($"Book {entityId} has a prepared media item path that is not canonical.");
            }

            if (!keys.Add((
                    descriptor.BookId,
                    descriptor.Kind,
                    descriptor.Kind == TrackKind ? canonicalFileName : string.Empty)))
            {
                throw Failure($"Book {entityId} has a duplicate prepared {descriptor.Kind} media item.");
            }

            var folder = Path.Combine(root, descriptor.BookId.ToString());
            var target = Path.Combine(folder, canonicalFileName);
            var fullTarget = Path.GetFullPath(target);
            var fullRoot = Path.GetFullPath(root);
            if (!fullTarget.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison))
            {
                throw Failure("A prepared media item resolves outside the candidate media root.");
            }

            if (!targets.Add(fullTarget))
            {
                throw Failure("Two prepared media items resolve to the same candidate path.");
            }

            planned[index] = new PlannedMedia(folder, fullTarget, item);
        }

        return planned;
    }

    private async Task<Stream> OpenStagedAsync(
        PortableStagingId stagingId,
        PlannedMedia item,
        CancellationToken ct)
    {
        try
        {
            return await staging
                .OpenMediaReadAsync(stagingId, item.Item.Reference, ct)
                .ConfigureAwait(false);
        }
        catch (PortableStagingException exception)
        {
            throw Failure($"A staged media item could not be read ({exception.Code}).");
        }
    }

    private static async Task<(long Length, string Sha256)> CopyAndHashAsync(
        Stream source,
        Stream destination,
        Memory<byte> buffer,
        long maxBytes,
        CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total = checked(total + read);
            if (total > maxBytes)
            {
                throw Failure("A staged media item exceeds its declared length.");
            }

            hash.AppendData(buffer.Span[..read]);
            await destination.WriteAsync(buffer[..read], ct).ConfigureAwait(false);
        }

        return (total, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static async Task<(long Length, string Sha256)> HashFileAsync(
        string path,
        CancellationToken ct)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[CopyBufferBytes];
        long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total = checked(total + read);
            hash.AppendData(buffer, 0, read);
        }

        return (total, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static bool HashEquals(string left, string right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(left),
                Convert.FromHexString(right));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best effort: the caller rethrows the authoritative failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort: the caller rethrows the authoritative failure.
        }
    }

    private static MigrationActivationException Failure(string message) =>
        new(MigrationActivationErrorCodes.Failed, message);

    private sealed record PlannedMedia(
        string FolderPath,
        string TargetPath,
        PortablePreparedMedia Item);
}

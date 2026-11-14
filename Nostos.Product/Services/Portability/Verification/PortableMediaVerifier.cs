using static Nostos.Backend.Services.Portability.Verification.PortableVerificationPrimitives;

namespace Nostos.Backend.Services.Portability.Verification;

/// <summary>
/// Media verification for both storage and a candidate media root: every
/// expected entry is streamed and re-hashed with bounded memory.
/// </summary>
internal static class PortableMediaVerifier
{
    internal static Task<PortableLibraryVerificationReport> VerifyStorageAsync(
        IBookAssetStorage assets,
        IReadOnlyList<PortableArchiveMediaEntry> expected,
        CancellationToken ct = default) =>
        VerifyStorageAsync(assets, tracks: null, expected, ct);

    /// <summary>
    /// Verifies stored media including the tracks of multi-track audiobooks.
    /// Without <paramref name="tracks"/> an expected track is reported as a
    /// failure, never skipped.
    /// </summary>
    internal static async Task<PortableLibraryVerificationReport> VerifyStorageAsync(
        IBookAssetStorage assets,
        IBookTrackStorage? tracks,
        IReadOnlyList<PortableArchiveMediaEntry> expected,
        CancellationToken ct = default)
    {
        var failures = new FailureList();
        var (files, bytes) = await VerifyStorageIntoAsync(
            assets,
            tracks,
            expected,
            failures,
            ct).ConfigureAwait(false);

        return new PortableLibraryVerificationReport(
            failures.TotalCount == 0,
            failures,
            [nameof(PortableArchiveMediaEntry)],
            PortableRowsVerified: 0,
            files,
            bytes,
            failures.TotalCount);
    }

    /// <summary>
    /// The one stored-media comparison. Callers that already own a failure list
    /// (the candidate pass and the legacy import) append into it, so every stage
    /// reports media failures through the same code and in the same order.
    /// </summary>
    internal static async Task<(long Files, long Bytes)> VerifyStorageIntoAsync(
        IBookAssetStorage assets,
        IBookTrackStorage? tracks,
        IReadOnlyList<PortableArchiveMediaEntry> expected,
        FailureList failures,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(expected);

        long files = 0;
        long bytes = 0;

        foreach (var descriptor in expected)
        {
            ct.ThrowIfCancellationRequested();
            var entityId = descriptor.BookId.ToString("D");

            StoredAssetInfo? info;
            StoredAssetRead? opened;
            try
            {
                if (PortableArchiveFormat.IsKnownMediaKind(descriptor.Kind)
                    && PortableMediaStorage.IsAvailable(tracks, descriptor.Kind)
                    && PortableArchiveFormat.CanonicalMediaFileName(descriptor.Kind, descriptor.FileName) is not null)
                {
                    info = await PortableMediaStorage
                        .GetInfoAsync(assets, tracks, descriptor.BookId, descriptor.Kind, descriptor.FileName, ct)
                        .ConfigureAwait(false);
                    opened = await PortableMediaStorage
                        .OpenAsync(assets, tracks, descriptor.BookId, descriptor.Kind, descriptor.FileName, ct)
                        .ConfigureAwait(false);
                }
                else
                {
                    failures.Add(Failure(
                        PortableLibraryVerificationErrorCodes.ExpectedStateInvalid,
                        "media",
                        entityId,
                        nameof(descriptor.Kind),
                        "A media descriptor has an unsupported kind."));
                    continue;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                failures.Add(Failure(
                    PortableLibraryVerificationErrorCodes.MediaMissing,
                    "media",
                    entityId,
                    descriptor.Kind,
                    $"Stored media could not be read ({exception.GetType().Name})."));
                continue;
            }

            if (info is null || opened is null)
            {
                if (opened is not null)
                {
                    await opened.DisposeAsync().ConfigureAwait(false);
                }

                failures.Add(Failure(
                    PortableLibraryVerificationErrorCodes.MediaMissing,
                    "media",
                    entityId,
                    descriptor.Kind,
                    "A media item from the prepared import is missing from storage."));
                continue;
            }

            await using (opened)
            {
                if (info.Length != descriptor.Length)
                {
                    failures.Add(Failure(
                        PortableLibraryVerificationErrorCodes.MediaLengthMismatch,
                        "media",
                        entityId,
                        descriptor.Kind,
                        $"Stored media length {info.Length} does not match the prepared descriptor."));
                    continue;
                }

                var hash = await PortableArchiveHashing
                    .HashStreamAsync(opened.Content, ct, descriptor.Length)
                    .ConfigureAwait(false);
                if (hash.Length != descriptor.Length
                    || !PortableArchiveValidation.FixedHashEquals(hash.Sha256, descriptor.Sha256))
                {
                    failures.Add(Failure(
                        PortableLibraryVerificationErrorCodes.MediaHashMismatch,
                        "media",
                        entityId,
                        descriptor.Kind,
                        "Stored media failed SHA-256 verification against the prepared descriptor."));
                    continue;
                }

                files++;
                bytes += info.Length;
            }
        }

        return (files, bytes);
    }

    internal static async Task<(long Files, long Bytes)> VerifyCandidateMediaRootAsync(
        string candidateMediaRoot,
        IReadOnlyList<PortableArchiveMediaEntry> expected,
        FailureList failures,
        CancellationToken ct)
    {
        var root = Path.GetFullPath(candidateMediaRoot);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var equality = StringComparer.FromComparison(comparison);
        var expectedPaths = new Dictionary<string, PortableArchiveMediaEntry>(equality);

        foreach (var descriptor in expected)
        {
            if (!TryResolveCandidateMediaPath(root, descriptor, out var path, out var failure))
            {
                failures.Add(failure!);
                continue;
            }

            if (!expectedPaths.TryAdd(path, descriptor))
            {
                failures.Add(Failure(
                    PortableLibraryVerificationErrorCodes.ExpectedStateInvalid,
                    "media",
                    descriptor.BookId.ToString("D"),
                    descriptor.Kind,
                    "Two expected media entries resolve to the same candidate path."));
            }
        }

        var found = new HashSet<string>(equality);
        long verifiedFiles = 0;
        long verifiedBytes = 0;

        if (Directory.Exists(root))
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false,
            };

            foreach (var file in Directory.EnumerateFiles(root, "*", options))
            {
                ct.ThrowIfCancellationRequested();
                var fullPath = Path.GetFullPath(file);
                if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, comparison))
                {
                    failures.Add(Failure(
                        PortableLibraryVerificationErrorCodes.MediaUnexpectedFile,
                        "media",
                        null,
                        null,
                        "A candidate media file resolves outside the candidate root."));
                    continue;
                }

                if (!expectedPaths.TryGetValue(fullPath, out var descriptor))
                {
                    failures.Add(Failure(
                        PortableLibraryVerificationErrorCodes.MediaUnexpectedFile,
                        "media",
                        null,
                        null,
                        "The candidate media root contains a file the prepared import does not declare."));
                    continue;
                }

                found.Add(fullPath);
                var info = new FileInfo(fullPath);
                if (info.Length != descriptor.Length)
                {
                    failures.Add(Failure(
                        PortableLibraryVerificationErrorCodes.MediaLengthMismatch,
                        "media",
                        descriptor.BookId.ToString("D"),
                        descriptor.Kind,
                        $"Candidate media length {info.Length} does not match the prepared descriptor."));
                    continue;
                }

                await using var stream = new FileStream(
                    fullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    PortableArchiveLimits.CopyBufferBytes,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var hash = await PortableArchiveHashing
                    .HashStreamAsync(stream, ct, descriptor.Length)
                    .ConfigureAwait(false);
                if (hash.Length != descriptor.Length
                    || !PortableArchiveValidation.FixedHashEquals(hash.Sha256, descriptor.Sha256))
                {
                    failures.Add(Failure(
                        PortableLibraryVerificationErrorCodes.MediaHashMismatch,
                        "media",
                        descriptor.BookId.ToString("D"),
                        descriptor.Kind,
                        "Candidate media failed SHA-256 verification against the prepared descriptor."));
                    continue;
                }

                verifiedFiles++;
                verifiedBytes += info.Length;
            }
        }
        else if (expectedPaths.Count > 0)
        {
            failures.Add(Failure(
                PortableLibraryVerificationErrorCodes.MediaMissing,
                "media",
                null,
                null,
                "The candidate media root does not exist."));
        }

        foreach (var descriptor in expectedPaths
                     .Where(pair => !found.Contains(pair.Key))
                     .Select(pair => pair.Value))
        {
            failures.Add(Failure(
                PortableLibraryVerificationErrorCodes.MediaMissing,
                "media",
                descriptor.BookId.ToString("D"),
                descriptor.Kind,
                "A prepared media item is missing from the candidate media root."));
        }

        return (verifiedFiles, verifiedBytes);
    }

    internal static bool TryResolveCandidateMediaPath(
        string root,
        PortableArchiveMediaEntry descriptor,
        out string path,
        out PortableLibraryVerificationFailure? failure)
    {
        path = string.Empty;
        failure = null;

        if (descriptor.BookId == Guid.Empty)
        {
            failure = Failure(
                PortableLibraryVerificationErrorCodes.ExpectedStateInvalid,
                "media",
                null,
                nameof(descriptor.BookId),
                "An expected media entry has an empty book identifier.");
            return false;
        }

        if (!PortableArchiveFormat.IsKnownMediaKind(descriptor.Kind))
        {
            failure = Failure(
                PortableLibraryVerificationErrorCodes.ExpectedStateInvalid,
                "media",
                descriptor.BookId.ToString("D"),
                nameof(descriptor.Kind),
                "An expected media entry has an unsupported kind.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(descriptor.FileName)
            || Path.GetFileName(descriptor.FileName) != descriptor.FileName
            || descriptor.FileName.Contains('\\')
            || descriptor.FileName.Contains('/'))
        {
            failure = Failure(
                PortableLibraryVerificationErrorCodes.ExpectedStateInvalid,
                "media",
                descriptor.BookId.ToString("D"),
                nameof(descriptor.FileName),
                "An expected media entry has an unsafe filename.");
            return false;
        }

        if (PortableArchiveFormat.CanonicalMediaFileName(descriptor.Kind, descriptor.FileName)
            is not { } canonicalFileName)
        {
            failure = Failure(
                PortableLibraryVerificationErrorCodes.ExpectedStateInvalid,
                "media",
                descriptor.BookId.ToString("D"),
                nameof(descriptor.FileName),
                "An expected media entry uses an unsupported extension.");
            return false;
        }

        if (!string.Equals(descriptor.FileName, canonicalFileName, StringComparison.OrdinalIgnoreCase))
        {
            failure = Failure(
                PortableLibraryVerificationErrorCodes.ExpectedStateInvalid,
                "media",
                descriptor.BookId.ToString("D"),
                nameof(descriptor.FileName),
                "An expected media entry filename is not canonical.");
            return false;
        }

        var expectedArchivePath = PortableArchiveFormat.MediaPath(descriptor.BookId, canonicalFileName);
        if (!string.Equals(descriptor.Path, expectedArchivePath, StringComparison.Ordinal))
        {
            failure = Failure(
                PortableLibraryVerificationErrorCodes.ExpectedStateInvalid,
                "media",
                descriptor.BookId.ToString("D"),
                nameof(descriptor.Path),
                "An expected media entry path is not canonical.");
            return false;
        }

        path = Path.Combine(root, descriptor.BookId.ToString(), canonicalFileName);
        return true;
    }
}

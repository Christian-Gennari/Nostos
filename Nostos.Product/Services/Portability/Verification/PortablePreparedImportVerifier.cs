using System.Reflection;
using System.Text.Json;
using static Nostos.Backend.Services.Portability.Verification.PortableVerificationPrimitives;
using static Nostos.Backend.Services.Portability.Verification.PortableVerificationCounts;

namespace Nostos.Backend.Services.Portability.Verification;

/// <summary>
/// The prepared-import pass: reads the staged relational payload, manifest and
/// every media item, re-hashes all of them and reuses
/// <see cref="PortableArchiveValidation"/> for relational validation. It is the
/// only stage that re-hashes staged bytes.
/// </summary>
internal static class PortablePreparedImportVerifier
{
    internal static async Task<PortablePreparedImportVerification> VerifyAsync(
        IPortableImportStaging staging,
        IPreparedPortableImport prepared,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(staging);
        ArgumentNullException.ThrowIfNull(prepared);

        var failures = new FailureList();
        var metadata = prepared.Metadata;
        var stagedMedia = prepared.Media;
        var descriptors = stagedMedia.Select(item => item.Descriptor).ToArray();
        var stagingId = metadata.StagingId;

        if (stagingId.Value == Guid.Empty)
        {
            failures.Add(Failure(
                PortableLibraryVerificationErrorCodes.StagingUnavailable,
                "staging",
                null,
                null,
                "The prepared import does not identify a staging area."));
            return Result(metadata, stagedMedia, descriptors, data: null, failures);
        }

        var stagingName = stagingId.Value.ToString("D");

        if (!metadata.IntegrityVerified)
        {
            failures.Add(Failure(
                PortableLibraryVerificationErrorCodes.ImportNotIntegrityVerified,
                "import",
                stagingName,
                nameof(metadata.IntegrityVerified),
                "The prepared import was not recorded as integrity verified and must not be activated."));
        }

        IReadOnlyList<PortablePreparedMedia> inventory;
        try
        {
            inventory = await staging.ListMediaAsync(stagingId, ct).ConfigureAwait(false);
        }
        catch (PortableStagingException exception)
        {
            failures.Add(Failure(
                PortableLibraryVerificationErrorCodes.StagingUnavailable,
                "staging",
                stagingName,
                null,
                $"The staged media inventory could not be read ({exception.Code})."));
            return Result(metadata, stagedMedia, descriptors, data: null, failures);
        }

        CompareStagedInventory(stagedMedia, inventory, failures);

        var dataBytes = await TryReadStagedPayloadAsync(staging, stagingId, manifest: false, failures, ct)
            .ConfigureAwait(false);
        var manifestBytes = dataBytes is null
            ? null
            : await TryReadStagedPayloadAsync(staging, stagingId, manifest: true, failures, ct).ConfigureAwait(false);

        PortableLibraryData? data = null;
        PortableArchiveManifest? manifest = null;

        if (dataBytes is not null)
        {
            if (dataBytes.LongLength != metadata.DataBytes)
            {
                failures.Add(Failure(
                    PortableLibraryVerificationErrorCodes.DataLengthMismatch,
                    "data",
                    null,
                    nameof(metadata.DataBytes),
                    "The staged relational payload length does not match the prepared descriptor."));
            }
            else if (!PortableArchiveValidation.FixedHashEquals(
                PortableArchiveHashing.Sha256Hex(dataBytes),
                metadata.DataSha256))
            {
                failures.Add(Failure(
                    PortableLibraryVerificationErrorCodes.DataHashMismatch,
                    "data",
                    null,
                    nameof(metadata.DataSha256),
                    "The staged relational payload failed SHA-256 verification against the prepared descriptor."));
            }

            data = TryDeserializeData(dataBytes, failures);
        }

        if (manifestBytes is not null)
        {
            manifest = TryDeserializeManifest(manifestBytes, failures);
        }

        if (data is not null)
        {
            try
            {
                PortableArchiveValidation.ValidatePortableData(data);
            }
            catch (PortableArchiveException exception)
            {
                failures.Add(Failure(
                    PortableLibraryVerificationErrorCodes.RelationalInvalid,
                    "library",
                    null,
                    null,
                    $"Portable relational validation failed ({exception.Code})."));
            }
        }

        if (manifest is not null)
        {
            try
            {
                PortableArchiveValidation.ValidateManifest(manifest);
            }
            catch (PortableArchiveException exception)
            {
                failures.Add(Failure(
                    PortableLibraryVerificationErrorCodes.ManifestMalformed,
                    "manifest",
                    null,
                    null,
                    $"The staged archive manifest is invalid ({exception.Code})."));
            }
        }

        if (data is not null && manifest is not null)
        {
            CompareManifestAgainstPrepared(manifest, metadata, descriptors, failures);
            CompareManifestCounts(manifest, data, failures);
        }

        if (data is not null)
        {
            if (data.Version != metadata.DataVersion)
            {
                failures.Add(Failure(
                    PortableLibraryVerificationErrorCodes.ManifestDisagreement,
                    "data",
                    null,
                    nameof(PortableLibraryData.Version),
                    "The staged payload data version does not agree with the prepared descriptor."));
            }

            ComparePreparedCounts(data, descriptors.Length, metadata, failures);
            ValidatePreparedSingleton(metadata.Counts, failures);

            if (manifest is not null)
            {
                CompareManifestMedia(manifest, data, descriptors, failures);
            }
        }

        await HashStagedMediaAsync(staging, stagingId, stagedMedia, failures, ct).ConfigureAwait(false);

        return Result(metadata, stagedMedia, descriptors, data, failures);
    }

    internal static PortablePreparedImportVerification Result(
        PreparedPortableImportMetadata metadata,
        IReadOnlyList<PortablePreparedMedia> stagedMedia,
        IReadOnlyList<PortableArchiveMediaEntry> descriptors,
        PortableLibraryData? data,
        FailureList failures) =>
        new(failures.TotalCount == 0, failures, metadata, stagedMedia, descriptors, data);

    internal static async Task<byte[]?> TryReadStagedPayloadAsync(
        IPortableImportStaging staging,
        PortableStagingId stagingId,
        bool manifest,
        FailureList failures,
        CancellationToken ct)
    {
        Stream? stream = null;
        try
        {
            stream = manifest
                ? await staging.OpenManifestReadAsync(stagingId, ct).ConfigureAwait(false)
                : await staging.OpenDataReadAsync(stagingId, ct).ConfigureAwait(false);
            var maximum = manifest
                ? PortableArchiveLimits.MaxManifestBytes
                : PortableArchiveLimits.MaxDataBytes;
            return await ReadBoundedAsync(stream, maximum, ct).ConfigureAwait(false);
        }
        catch (PortableStagingException exception)
        {
            failures.Add(Failure(
                manifest
                    ? PortableLibraryVerificationErrorCodes.ManifestMalformed
                    : PortableLibraryVerificationErrorCodes.DataMissing,
                manifest ? "manifest" : "data",
                stagingId.Value.ToString("D"),
                null,
                $"The staged {(manifest ? "manifest" : "relational payload")} could not be read ({exception.Code})."));
            return null;
        }
        catch (PortableArchiveException exception)
        {
            failures.Add(Failure(
                manifest
                    ? PortableLibraryVerificationErrorCodes.ManifestMalformed
                    : PortableLibraryVerificationErrorCodes.DataMalformed,
                manifest ? "manifest" : "data",
                stagingId.Value.ToString("D"),
                null,
                $"The staged {(manifest ? "manifest" : "relational payload")} exceeds its limit ({exception.Code})."));
            return null;
        }
        finally
        {
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    internal static PortableLibraryData? TryDeserializeData(
        byte[] bytes,
        FailureList failures)
    {
        try
        {
            var data = PortableArchiveValidation.Deserialize<PortableLibraryData>(
                bytes,
                "malformed_data",
                "Portable archive relational payload is malformed.",
                PortableArchiveJson.Options);
            // The restore reads the same bytes through the same roll-up, so an
            // older archive is compared against what it was restored as.
            return data is null || data.Works is null || data.Books is null
                ? data
                : PortableLegacyBookValues.ToCurrent(data);
        }
        catch (PortableArchiveException exception)
        {
            failures.Add(Failure(
                PortableLibraryVerificationErrorCodes.DataMalformed,
                "data",
                null,
                null,
                $"The staged relational payload is malformed ({exception.Code})."));
            return null;
        }
    }

    internal static PortableArchiveManifest? TryDeserializeManifest(
        byte[] bytes,
        FailureList failures)
    {
        try
        {
            return PortableArchiveValidation.Deserialize<PortableArchiveManifest>(
                bytes,
                "malformed_manifest",
                "Portable archive manifest is malformed.",
                PortableArchiveJson.Options);
        }
        catch (PortableArchiveException exception)
        {
            failures.Add(Failure(
                PortableLibraryVerificationErrorCodes.ManifestMalformed,
                "manifest",
                null,
                null,
                $"The staged manifest is malformed ({exception.Code})."));
            return null;
        }
    }

    internal static void CompareStagedInventory(
        IReadOnlyList<PortablePreparedMedia> prepared,
        IReadOnlyList<PortablePreparedMedia> inventory,
        FailureList failures)
    {
        var preparedSet = prepared.ToHashSet();
        var inventorySet = inventory.ToHashSet();
        if (preparedSet.SetEquals(inventorySet))
        {
            return;
        }

        foreach (var item in prepared.Where(candidate => !inventorySet.Contains(candidate)))
        {
            failures.Add(Failure(
                PortableLibraryVerificationErrorCodes.InventoryMismatch,
                "media",
                item.Descriptor.BookId.ToString("D"),
                item.Descriptor.Kind,
                "A prepared media item is missing from the durable staged inventory."));
        }

        foreach (var item in inventory.Where(candidate => !preparedSet.Contains(candidate)))
        {
            failures.Add(Failure(
                PortableLibraryVerificationErrorCodes.InventoryMismatch,
                "media",
                item.Descriptor.BookId.ToString("D"),
                item.Descriptor.Kind,
                "The durable staged inventory contains an item the prepared import does not declare."));
        }
    }

    internal static void CompareManifestAgainstPrepared(
        PortableArchiveManifest manifest,
        PreparedPortableImportMetadata metadata,
        IReadOnlyList<PortableArchiveMediaEntry> descriptors,
        FailureList failures)
    {
        if (!string.Equals(manifest.Format, PortableArchiveFormat.Name, StringComparison.Ordinal)
            || manifest.FormatVersion != metadata.FormatVersion
            || manifest.DataVersion != metadata.DataVersion)
        {
            failures.Add(Failure(
                PortableLibraryVerificationErrorCodes.ManifestDisagreement,
                "manifest",
                null,
                nameof(manifest.FormatVersion),
                "The staged manifest format/version does not agree with the prepared descriptor."));
        }

        if (manifest.Data is null
            || !string.Equals(manifest.Data.Path, PortableArchiveFormat.DataPath, StringComparison.Ordinal)
            || manifest.Data.Length != metadata.DataBytes
            || !PortableArchiveValidation.FixedHashEquals(manifest.Data.Sha256, metadata.DataSha256))
        {
            failures.Add(Failure(
                PortableLibraryVerificationErrorCodes.ManifestDisagreement,
                "manifest",
                null,
                nameof(manifest.Data),
                "The staged manifest relational payload descriptor does not agree with the prepared descriptor."));
        }

        var manifestMedia = manifest.Media?.ToHashSet() ?? [];
        if (manifest.Media is null
            || manifest.Media.Count != descriptors.Count
            || !manifestMedia.SetEquals(descriptors))
        {
            failures.Add(Failure(
                PortableLibraryVerificationErrorCodes.ManifestDisagreement,
                "manifest",
                null,
                nameof(manifest.Media),
                "The staged manifest media inventory does not agree with the prepared descriptor."));
        }

        if (metadata.MediaFiles != descriptors.Count
            || metadata.Counts.MediaEntries != metadata.MediaFiles
            || metadata.MediaBytes != descriptors.Sum(item => item.Length))
        {
            failures.Add(Failure(
                PortableLibraryVerificationErrorCodes.CountMismatch,
                "media",
                null,
                nameof(metadata.MediaFiles),
                "The prepared descriptor media count or byte total does not match its media inventory."));
        }
    }

    internal static void CompareManifestMedia(
        PortableArchiveManifest manifest,
        PortableLibraryData data,
        IReadOnlyList<PortableArchiveMediaEntry> descriptors,
        FailureList failures)
    {
        try
        {
            PortableArchiveValidation.ValidateMediaManifest(manifest, data);
        }
        catch (PortableArchiveException exception)
        {
            failures.Add(Failure(
                PortableLibraryVerificationErrorCodes.ManifestDisagreement,
                "media",
                null,
                null,
                $"The staged manifest media does not agree with the relational payload ({exception.Code})."));
        }

        var manifestMedia = manifest.Media?.ToHashSet() ?? [];
        if (manifest.Media is null || !manifestMedia.SetEquals(descriptors))
        {
            failures.Add(Failure(
                PortableLibraryVerificationErrorCodes.ManifestDisagreement,
                "media",
                null,
                null,
                "The staged manifest media inventory does not match the prepared media inventory."));
        }
    }

    internal static async Task HashStagedMediaAsync(
        IPortableImportStaging staging,
        PortableStagingId stagingId,
        IReadOnlyList<PortablePreparedMedia> stagedMedia,
        FailureList failures,
        CancellationToken ct)
    {
        foreach (var item in stagedMedia)
        {
            ct.ThrowIfCancellationRequested();
            var entityId = item.Descriptor.BookId.ToString("D");
            try
            {
                await using var stream = await staging
                    .OpenMediaReadAsync(stagingId, item.Reference, ct)
                    .ConfigureAwait(false);
                var hash = await PortableArchiveHashing
                    .HashStreamAsync(stream, ct, item.Descriptor.Length)
                    .ConfigureAwait(false);
                if (hash.Length != item.Descriptor.Length)
                {
                    failures.Add(Failure(
                        PortableLibraryVerificationErrorCodes.MediaLengthMismatch,
                        "media",
                        entityId,
                        item.Descriptor.Kind,
                        "A staged media item length does not match the prepared descriptor."));
                }
                else if (!PortableArchiveValidation.FixedHashEquals(hash.Sha256, item.Descriptor.Sha256))
                {
                    failures.Add(Failure(
                        PortableLibraryVerificationErrorCodes.MediaHashMismatch,
                        "media",
                        entityId,
                        item.Descriptor.Kind,
                        "A staged media item failed SHA-256 verification against the prepared descriptor."));
                }
            }
            catch (PortableStagingException exception)
            {
                failures.Add(Failure(
                    PortableLibraryVerificationErrorCodes.MediaMissing,
                    "media",
                    entityId,
                    item.Descriptor.Kind,
                    $"A staged media item could not be read for verification ({exception.Code})."));
            }
            catch (PortableArchiveException exception)
            {
                failures.Add(Failure(
                    PortableLibraryVerificationErrorCodes.MediaTooLarge,
                    "media",
                    entityId,
                    item.Descriptor.Kind,
                    $"A staged media item exceeds its declared length ({exception.Code})."));
            }
        }
    }

    internal static async Task<byte[]> ReadBoundedAsync(
        Stream source,
        long maxBytes,
        CancellationToken ct)
    {
        var buffer = new byte[PortableArchiveLimits.CopyBufferBytes];
        using var output = new MemoryStream();
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total = checked(total + read);
            if (total > maxBytes)
            {
                throw new PortableArchiveException(
                    "entry_too_large",
                    "The staged payload exceeds its archive limit.");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }
}

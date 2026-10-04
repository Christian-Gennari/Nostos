using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;

namespace Nostos.Backend.Services.Portability;

/// <summary>
/// Authoritative portable-state verifier. The prepared-import pass reads the staged
/// relational payload, manifest and every media item, re-hashes all of them, and
/// reuses <see cref="PortableArchiveValidation"/> for relational/relationship
/// validation. The candidate pass compares the materialized candidate database and
/// media root against that verified expected state using read-only queries only;
/// host operational state is neither required to match the archive nor mutated.
/// </summary>
public sealed class PortableLibraryVerifier : IPortableLibraryVerifier
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private static readonly IReadOnlyDictionary<string, string> ManifestCountToDataCount =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(PortableArchiveCounts.Works)] = nameof(MigrationArchiveCounts.Works),
            [nameof(PortableArchiveCounts.Books)] = nameof(MigrationArchiveCounts.Books),
            [nameof(PortableArchiveCounts.Collections)] = nameof(MigrationArchiveCounts.Collections),
            [nameof(PortableArchiveCounts.BookCollections)] = nameof(MigrationArchiveCounts.CollectionMemberships),
            [nameof(PortableArchiveCounts.Notes)] = nameof(MigrationArchiveCounts.Notes),
            [nameof(PortableArchiveCounts.Topics)] = nameof(MigrationArchiveCounts.Topics),
            [nameof(PortableArchiveCounts.NoteTopics)] = nameof(MigrationArchiveCounts.NoteTopics),
            [nameof(PortableArchiveCounts.Writings)] = nameof(MigrationArchiveCounts.Writings),
            [nameof(PortableArchiveCounts.BookAcquisitions)] = nameof(MigrationArchiveCounts.Acquisitions),
        };

    private static readonly PropertyInfo[] CountProperties = typeof(MigrationArchiveCounts)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(property => property.CanWrite && property.PropertyType == typeof(long))
        .OrderBy(property => property.Name, StringComparer.Ordinal)
        .ToArray();

    internal static readonly IReadOnlyList<string> CandidateVerifiedKinds =
    [
        nameof(PortableWork),
        nameof(PortableBook),
        nameof(PortableBookMetadata),
        nameof(PortableReadingProgress),
        nameof(PortableCollection),
        nameof(PortableBookCollection),
        nameof(PortableNote),
        nameof(PortableTopic),
        nameof(PortableNoteTopic),
        nameof(PortableWriting),
        nameof(PortableWritingNote),
        nameof(PortableBookAcquisition),
        nameof(PortableAssistantSettings),
        nameof(PortableNoteImportBookLink),
        nameof(PortableArchiveMediaEntry),
    ];

    internal static IReadOnlyDictionary<string, PortableLibraryRecordCoverage> VerificationCoverage =>
        PortableLibraryVerificationCoverage.ByRecordName;

    public async Task<PortablePreparedImportVerification> VerifyPreparedImportAsync(
        IPortableImportStaging staging,
        IPreparedPortableImport prepared,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(staging);
        ArgumentNullException.ThrowIfNull(prepared);

        var failures = new List<PortableLibraryVerificationFailure>();
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
                Sha256Hex(dataBytes),
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

    public async Task<PortableLibraryVerificationReport> VerifyCandidateAsync(
        NostosDbContext candidateDatabase,
        string candidateMediaRoot,
        PortablePreparedImportVerification expected,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidateDatabase);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateMediaRoot);
        ArgumentNullException.ThrowIfNull(expected);

        if (!expected.Passed || expected.Data is null)
        {
            throw new PortableLibraryVerificationException(
                PortableLibraryVerificationErrorCodes.ExpectedStateInvalid,
                "Candidate verification requires a prepared import that passed full verification.");
        }

        var failures = new List<PortableLibraryVerificationFailure>();
        var data = expected.Data;

        var works = await candidateDatabase.Works.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var books = await candidateDatabase.Books.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var collections = await candidateDatabase.Collections.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var memberships = await candidateDatabase.BookCollections.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var notes = await candidateDatabase.Notes.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var topics = await candidateDatabase.Topics.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var noteTopics = await candidateDatabase.NoteTopics.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var writings = await candidateDatabase.Writings.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var writingNotes = await candidateDatabase.WritingNotes.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var acquisitions = await candidateDatabase.BookAcquisitions.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var importLinks = await candidateDatabase.NoteImportBookLinks.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var assistantSettings = await candidateDatabase.AssistantSettings.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);

        CompareWorks(data, works, failures);
        CompareBooks(data, books, failures);
        CompareCollections(data, collections, failures);
        CompareBookCollections(data, memberships, failures);
        CompareNotes(data, notes, failures);
        CompareTopics(data, topics, failures);
        CompareNoteTopics(data, noteTopics, failures);
        CompareWritings(data, writings, failures);
        CompareWritingNotes(data, writingNotes, failures);
        CompareAcquisitions(data, acquisitions, failures);
        CompareNoteImportBookLinks(data, importLinks, failures);
        CompareAssistantSettings(data, assistantSettings, failures);

        var (mediaFiles, mediaBytes) = await VerifyCandidateMediaRootAsync(
            candidateMediaRoot,
            expected.Descriptors,
            failures,
            ct).ConfigureAwait(false);

        CompareCandidateCounts(
            data,
            expected.Metadata.Counts,
            works.Count,
            books.Count,
            collections.Count,
            memberships.Count,
            notes.Count,
            topics.Count,
            noteTopics.Count,
            writings.Count,
            writingNotes.Count,
            acquisitions.Count,
            importLinks.Count,
            assistantSettings.Count(x => x.CaptureProcessingMode is not null),
            mediaFiles,
            failures);

        var rowsVerified =
            works.Count + books.Count + collections.Count + memberships.Count + notes.Count + topics.Count
            + noteTopics.Count + writings.Count + writingNotes.Count + acquisitions.Count + importLinks.Count
            + assistantSettings.Count(x => x.CaptureProcessingMode is not null);

        return new PortableLibraryVerificationReport(
            failures.Count == 0,
            failures,
            CandidateVerifiedKinds,
            rowsVerified,
            mediaFiles,
            mediaBytes);
    }

    public async Task<PortableLibraryVerificationReport> VerifyMediaAsync(
        IBookAssetStorage assets,
        IReadOnlyList<PortableArchiveMediaEntry> expected,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(expected);

        var failures = new List<PortableLibraryVerificationFailure>();
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
                if (descriptor.Kind == PortableArchiveFormat.BookMediaKind)
                {
                    info = await assets.GetBookFileInfoAsync(descriptor.BookId, ct).ConfigureAwait(false);
                    opened = await assets.OpenBookFileAsync(descriptor.BookId, null, ct).ConfigureAwait(false);
                }
                else if (descriptor.Kind == PortableArchiveFormat.CoverMediaKind)
                {
                    info = await assets.GetBookCoverInfoAsync(descriptor.BookId, ct).ConfigureAwait(false);
                    opened = await assets.OpenBookCoverAsync(descriptor.BookId, ct).ConfigureAwait(false);
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

                var hash = await HashStreamAsync(opened.Content, descriptor.Length, ct).ConfigureAwait(false);
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

        return new PortableLibraryVerificationReport(
            failures.Count == 0,
            failures,
            [nameof(PortableArchiveMediaEntry)],
            PortableRowsVerified: 0,
            files,
            bytes);
    }

    private static PortablePreparedImportVerification Result(
        PreparedPortableImportMetadata metadata,
        IReadOnlyList<PortablePreparedMedia> stagedMedia,
        IReadOnlyList<PortableArchiveMediaEntry> descriptors,
        PortableLibraryData? data,
        IReadOnlyList<PortableLibraryVerificationFailure> failures) =>
        new(failures.Count == 0, failures, metadata, stagedMedia, descriptors, data);

    private static PortableLibraryVerificationFailure Failure(
        string code,
        string entity,
        string? entityId,
        string? field,
        string detail) => new(code, entity, entityId, field, detail);

    private static async Task<byte[]?> TryReadStagedPayloadAsync(
        IPortableImportStaging staging,
        PortableStagingId stagingId,
        bool manifest,
        List<PortableLibraryVerificationFailure> failures,
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

    private static PortableLibraryData? TryDeserializeData(
        byte[] bytes,
        List<PortableLibraryVerificationFailure> failures)
    {
        try
        {
            return PortableArchiveValidation.Deserialize<PortableLibraryData>(
                bytes,
                "malformed_data",
                "Portable archive relational payload is malformed.",
                JsonOptions);
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

    private static PortableArchiveManifest? TryDeserializeManifest(
        byte[] bytes,
        List<PortableLibraryVerificationFailure> failures)
    {
        try
        {
            return PortableArchiveValidation.Deserialize<PortableArchiveManifest>(
                bytes,
                "malformed_manifest",
                "Portable archive manifest is malformed.",
                JsonOptions);
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

    private static void CompareStagedInventory(
        IReadOnlyList<PortablePreparedMedia> prepared,
        IReadOnlyList<PortablePreparedMedia> inventory,
        List<PortableLibraryVerificationFailure> failures)
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

    private static void CompareManifestAgainstPrepared(
        PortableArchiveManifest manifest,
        PreparedPortableImportMetadata metadata,
        IReadOnlyList<PortableArchiveMediaEntry> descriptors,
        List<PortableLibraryVerificationFailure> failures)
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

    private static void CompareManifestCounts(
        PortableArchiveManifest manifest,
        PortableLibraryData data,
        List<PortableLibraryVerificationFailure> failures)
    {
        var dataCounts = PortableLibraryCounts.ComputeCounts(data, mediaEntries: 0);
        foreach (var property in typeof(PortableArchiveCounts)
                     .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(property => property.CanWrite))
        {
            if (!ManifestCountToDataCount.TryGetValue(property.Name, out var dataPropertyName))
            {
                failures.Add(Failure(
                    PortableLibraryVerificationErrorCodes.CountUnverified,
                    "manifest",
                    null,
                    property.Name,
                    "A manifest count dimension is not mapped to portable data verification."));
                continue;
            }

            var actual = Convert.ToInt64(property.GetValue(manifest.Counts));
            var expected = (long)typeof(MigrationArchiveCounts).GetProperty(dataPropertyName)!.GetValue(dataCounts)!;
            if (actual != expected)
            {
                failures.Add(Failure(
                    PortableLibraryVerificationErrorCodes.CountMismatch,
                    "manifest",
                    null,
                    property.Name,
                    "A manifest count does not match the staged relational payload."));
            }
        }
    }

    private static void ComparePreparedCounts(
        PortableLibraryData data,
        int mediaEntries,
        PreparedPortableImportMetadata metadata,
        List<PortableLibraryVerificationFailure> failures)
    {
        var computed = PortableLibraryCounts.ComputeCounts(data, mediaEntries);
        foreach (var property in CountProperties)
        {
            var advertised = (long)property.GetValue(metadata.Counts)!;
            var actual = (long)property.GetValue(computed)!;
            if (advertised != actual)
            {
                failures.Add(Failure(
                    PortableLibraryVerificationErrorCodes.CountMismatch,
                    "counts",
                    null,
                    property.Name,
                    "The prepared descriptor count does not match the staged relational payload."));
            }
        }
    }

    private static void ValidatePreparedSingleton(
        MigrationArchiveCounts counts,
        List<PortableLibraryVerificationFailure> failures)
    {
        if (counts.AssistantSettings is not (0 or 1))
        {
            failures.Add(Failure(
                PortableLibraryVerificationErrorCodes.SingletonMismatch,
                "assistantSettings",
                null,
                nameof(counts.AssistantSettings),
                "The portable assistant setting must be a singleton or absent."));
        }
    }

    private static void CompareManifestMedia(
        PortableArchiveManifest manifest,
        PortableLibraryData data,
        IReadOnlyList<PortableArchiveMediaEntry> descriptors,
        List<PortableLibraryVerificationFailure> failures)
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

    private static async Task HashStagedMediaAsync(
        IPortableImportStaging staging,
        PortableStagingId stagingId,
        IReadOnlyList<PortablePreparedMedia> stagedMedia,
        List<PortableLibraryVerificationFailure> failures,
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
                var hash = await HashStreamAsync(stream, item.Descriptor.Length, ct).ConfigureAwait(false);
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

    private static void CompareWorks(
        PortableLibraryData data,
        List<WorkModel> works,
        List<PortableLibraryVerificationFailure> failures)
    {
        var expected = data.Works.ToDictionary(item => item.Id);
        var actual = works.ToDictionary(item => item.Id);
        CompareIdSets("work", expected.Keys, actual.Keys, failures);
        foreach (var (id, item) in expected)
        {
            if (!actual.TryGetValue(id, out var row))
            {
                continue;
            }

            CompareField(failures, "work", id, nameof(PortableWork.Title), row.Title, item.Title);
            CompareField(failures, "work", id, nameof(PortableWork.Author), row.Author, item.Author);
            CompareField(failures, "work", id, nameof(PortableWork.CreatedAt), row.CreatedAt, item.CreatedAt);
        }
    }

    private static void CompareBooks(
        PortableLibraryData data,
        List<BookModel> books,
        List<PortableLibraryVerificationFailure> failures)
    {
        var expected = data.Books.ToDictionary(item => item.Id);
        var actual = books.ToDictionary(item => item.Id);
        CompareIdSets("book", expected.Keys, actual.Keys, failures);
        foreach (var (id, item) in expected)
        {
            if (!actual.TryGetValue(id, out var row))
            {
                continue;
            }

            CompareField(failures, "book", id, "Type", ConcreteType(row), item.Type, ignoreCase: true);
            CompareField(failures, "book", id, nameof(BookModel.WorkId), row.WorkId, item.WorkId);
            CompareField(failures, "book", id, nameof(BookModel.Status), row.Status.ToString(), item.Status, ignoreCase: true);
            CompareField(failures, "book", id, nameof(BookModel.StatusMessage), row.StatusMessage, item.StatusMessage);
            CompareField(failures, "book", id, nameof(BookModel.Title), row.Title, item.Title);
            CompareField(failures, "book", id, nameof(BookModel.Author), row.Author, item.Author);
            CompareField(failures, "book", id, nameof(BookModel.CreatedAt), row.CreatedAt, item.CreatedAt);

            var metadata = row.Metadata;
            CompareField(failures, "book", id, nameof(BookMetadata.Subtitle), metadata.Subtitle, item.Metadata.Subtitle);
            CompareField(failures, "book", id, nameof(BookMetadata.Description), metadata.Description, item.Metadata.Description);
            CompareField(failures, "book", id, nameof(BookMetadata.Editor), metadata.Editor, item.Metadata.Editor);
            CompareField(failures, "book", id, nameof(BookMetadata.Translator), metadata.Translator, item.Metadata.Translator);
            CompareField(failures, "book", id, nameof(BookMetadata.Publisher), metadata.Publisher, item.Metadata.Publisher);
            CompareField(failures, "book", id, nameof(BookMetadata.PlaceOfPublication), metadata.PlaceOfPublication, item.Metadata.PlaceOfPublication);
            CompareField(failures, "book", id, nameof(BookMetadata.PublishedDate), metadata.PublishedDate, item.Metadata.PublishedDate);
            CompareField(failures, "book", id, nameof(BookMetadata.Language), metadata.Language, item.Metadata.Language);
            CompareField(failures, "book", id, nameof(BookMetadata.Categories), metadata.Categories, item.Metadata.Categories);
            CompareField(failures, "book", id, nameof(BookMetadata.Edition), metadata.Edition, item.Metadata.Edition);
            CompareField(failures, "book", id, nameof(BookMetadata.Series), metadata.Series, item.Metadata.Series);
            CompareField(failures, "book", id, nameof(BookMetadata.VolumeNumber), metadata.VolumeNumber, item.Metadata.VolumeNumber);

            var progress = row.Progress;
            CompareField(failures, "book", id, nameof(ReadingProgress.LastLocation), progress.LastLocation, item.Progress.LastLocation);
            CompareField(failures, "book", id, nameof(ReadingProgress.ProgressPercent), progress.ProgressPercent, item.Progress.ProgressPercent);
            CompareField(failures, "book", id, nameof(ReadingProgress.Rating), progress.Rating, item.Progress.Rating);
            CompareField(failures, "book", id, nameof(ReadingProgress.IsFavorite), progress.IsFavorite, item.Progress.IsFavorite);
            CompareField(failures, "book", id, nameof(ReadingProgress.PersonalReview), progress.PersonalReview, item.Progress.PersonalReview);
            CompareField(failures, "book", id, nameof(ReadingProgress.LastReadAt), progress.LastReadAt, item.Progress.LastReadAt);
            CompareField(failures, "book", id, nameof(ReadingProgress.FinishedAt), progress.FinishedAt, item.Progress.FinishedAt);

            var file = row.FileDetails;
            CompareField(failures, "book", id, nameof(FileInfoDetails.HasFile), file.HasFile, item.HasBookFile);
            CompareField(
                failures,
                "book",
                id,
                "HasCover",
                !string.IsNullOrWhiteSpace(file.CoverFileName),
                item.HasCover);
            CompareField(failures, "book", id, nameof(FileInfoDetails.ChaptersJson), file.ChaptersJson, item.ChaptersJson);

            var isbn = row switch
            {
                PhysicalBookModel physical => physical.Isbn,
                EBookModel ebook => ebook.Isbn,
                _ => null,
            };
            var pageCount = row switch
            {
                PhysicalBookModel physical => physical.PageCount,
                EBookModel ebook => ebook.PageCount,
                _ => null,
            };
            var audio = row as AudioBookModel;
            CompareField(failures, "book", id, nameof(PhysicalBookModel.Isbn), isbn, item.Isbn);
            CompareField(failures, "book", id, nameof(PhysicalBookModel.PageCount), pageCount, item.PageCount);
            CompareField(failures, "book", id, nameof(AudioBookModel.Asin), audio?.Asin, item.Asin);
            CompareField(failures, "book", id, nameof(AudioBookModel.Duration), audio?.Duration, item.Duration);
            CompareField(failures, "book", id, nameof(AudioBookModel.Narrator), audio?.Narrator, item.Narrator);
        }
    }

    private static void CompareCollections(
        PortableLibraryData data,
        List<CollectionModel> collections,
        List<PortableLibraryVerificationFailure> failures)
    {
        var expected = data.Collections.ToDictionary(item => item.Id);
        var actual = collections.ToDictionary(item => item.Id);
        CompareIdSets("collection", expected.Keys, actual.Keys, failures);
        foreach (var (id, item) in expected)
        {
            if (!actual.TryGetValue(id, out var row))
            {
                continue;
            }

            CompareField(failures, "collection", id, nameof(CollectionModel.Name), row.Name, item.Name);
            CompareField(failures, "collection", id, nameof(CollectionModel.ParentId), row.ParentId, item.ParentId);
        }
    }

    private static void CompareBookCollections(
        PortableLibraryData data,
        List<BookCollectionModel> memberships,
        List<PortableLibraryVerificationFailure> failures)
    {
        var expected = data.BookCollections
            .GroupBy(item => (item.BookId, item.CollectionId))
            .ToDictionary(group => group.Key, group => group.First().AddedAt);
        var actual = memberships
            .GroupBy(item => (item.BookId, item.CollectionId))
            .ToDictionary(group => group.Key, group => group.First().AddedAt);

        CompareRelationshipKeys(
            "bookCollection",
            expected.Keys,
            actual.Keys,
            failures,
            "collection membership");
        foreach (var key in expected.Keys.Where(actual.ContainsKey))
        {
            if (actual[key] != expected[key])
            {
                failures.Add(Failure(
                    PortableLibraryVerificationErrorCodes.FieldMismatch,
                    "bookCollection",
                    key.BookId.ToString("D"),
                    nameof(PortableBookCollection.AddedAt),
                    "A collection membership timestamp does not match the prepared import."));
            }
        }
    }

    private static void CompareNotes(
        PortableLibraryData data,
        List<NoteModel> notes,
        List<PortableLibraryVerificationFailure> failures)
    {
        var expected = data.Notes.ToDictionary(item => item.Id);
        var actual = notes.ToDictionary(item => item.Id);
        CompareIdSets("note", expected.Keys, actual.Keys, failures);
        foreach (var (id, item) in expected)
        {
            if (!actual.TryGetValue(id, out var row))
            {
                continue;
            }

            CompareField(failures, "note", id, nameof(NoteModel.BookId), row.BookId, item.BookId);
            CompareField(failures, "note", id, nameof(NoteModel.Content), row.Content, item.Content);
            CompareField(failures, "note", id, nameof(NoteModel.CfiRange), row.CfiRange, item.CfiRange);
            CompareField(failures, "note", id, nameof(NoteModel.SelectedText), row.SelectedText, item.SelectedText);
            CompareField(failures, "note", id, nameof(NoteModel.CreatedAt), row.CreatedAt, item.CreatedAt);
            CompareField(failures, "note", id, nameof(NoteModel.RawContent), row.RawContent, item.RawContent);
            CompareField(failures, "note", id, nameof(NoteModel.CaptureSource), row.CaptureSource, item.CaptureSource);
            CompareField(failures, "note", id, nameof(NoteModel.ProcessingMode), row.ProcessingMode, item.ProcessingMode);
            CompareField(failures, "note", id, nameof(NoteModel.SourceAnchorKind), row.SourceAnchorKind, item.SourceAnchorKind);
            CompareField(failures, "note", id, nameof(NoteModel.SourceAnchorValue), row.SourceAnchorValue, item.SourceAnchorValue);
            CompareField(failures, "note", id, nameof(NoteModel.AnchorVerified), row.AnchorVerified, item.AnchorVerified);
        }
    }

    private static void CompareTopics(
        PortableLibraryData data,
        List<TopicModel> topics,
        List<PortableLibraryVerificationFailure> failures)
    {
        var expected = data.Topics.ToDictionary(item => item.Id);
        var actual = topics.ToDictionary(item => item.Id);
        CompareIdSets("topic", expected.Keys, actual.Keys, failures);
        foreach (var (id, item) in expected)
        {
            if (!actual.TryGetValue(id, out var row))
            {
                continue;
            }

            CompareField(failures, "topic", id, nameof(TopicModel.Topic), row.Topic, item.Topic);
        }
    }

    private static void CompareNoteTopics(
        PortableLibraryData data,
        List<NoteTopicModel> noteTopics,
        List<PortableLibraryVerificationFailure> failures)
    {
        var expected = data.NoteTopics.Select(item => (item.NoteId, item.TopicId)).ToHashSet();
        var actual = noteTopics.Select(item => (item.NoteId, item.TopicId)).ToHashSet();
        CompareRelationshipKeys("noteTopic", expected, actual, failures, "note/topic link");
    }

    private static void CompareWritings(
        PortableLibraryData data,
        List<WritingModel> writings,
        List<PortableLibraryVerificationFailure> failures)
    {
        var expected = data.Writings.ToDictionary(item => item.Id);
        var actual = writings.ToDictionary(item => item.Id);
        CompareIdSets("writing", expected.Keys, actual.Keys, failures);
        foreach (var (id, item) in expected)
        {
            if (!actual.TryGetValue(id, out var row))
            {
                continue;
            }

            CompareField(failures, "writing", id, nameof(WritingModel.Name), row.Name, item.Name);
            CompareField(failures, "writing", id, nameof(WritingModel.Type), row.Type.ToString(), item.Type, ignoreCase: true);
            CompareField(failures, "writing", id, nameof(WritingModel.Content), row.Content, item.Content);
            CompareField(failures, "writing", id, nameof(WritingModel.ParentId), row.ParentId, item.ParentId);
            CompareField(failures, "writing", id, nameof(WritingModel.CreatedAt), row.CreatedAt, item.CreatedAt);
            CompareField(failures, "writing", id, nameof(WritingModel.UpdatedAt), row.UpdatedAt, item.UpdatedAt);
        }
    }

    private static void CompareWritingNotes(
        PortableLibraryData data,
        List<WritingNoteModel> writingNotes,
        List<PortableLibraryVerificationFailure> failures)
    {
        var expected = (data.WritingNotes ?? [])
            .GroupBy(item => (item.WritingId, item.NoteId))
            .ToDictionary(group => group.Key, group => group.First().AddedAt);
        var actual = writingNotes
            .GroupBy(item => (item.WritingId, item.NoteId))
            .ToDictionary(group => group.Key, group => group.First().AddedAt);

        CompareRelationshipKeys(
            "writingNote",
            expected.Keys,
            actual.Keys,
            failures,
            "writing/note link");
        foreach (var key in expected.Keys.Where(actual.ContainsKey))
        {
            if (actual[key] != expected[key])
            {
                failures.Add(Failure(
                    PortableLibraryVerificationErrorCodes.FieldMismatch,
                    "writingNote",
                    key.WritingId.ToString("D"),
                    nameof(PortableWritingNote.AddedAt),
                    "A writing/note link timestamp does not match the prepared import."));
            }
        }
    }

    private static void CompareAcquisitions(
        PortableLibraryData data,
        List<BookAcquisitionModel> acquisitions,
        List<PortableLibraryVerificationFailure> failures)
    {
        var expected = data.BookAcquisitions.ToDictionary(item => item.Id);
        var actual = acquisitions.ToDictionary(item => item.Id);
        CompareIdSets("acquisition", expected.Keys, actual.Keys, failures);
        foreach (var (id, item) in expected)
        {
            if (!actual.TryGetValue(id, out var row))
            {
                continue;
            }

            CompareField(failures, "acquisition", id, nameof(BookAcquisitionModel.BookId), row.BookId, item.BookId);
            CompareField(failures, "acquisition", id, nameof(BookAcquisitionModel.ProviderId), row.ProviderId, item.ProviderId);
            CompareField(failures, "acquisition", id, nameof(BookAcquisitionModel.ProviderDisplayName), row.ProviderDisplayName, item.ProviderDisplayName);
            CompareField(failures, "acquisition", id, nameof(BookAcquisitionModel.ExternalId), row.ExternalId, item.ExternalId);
            CompareField(failures, "acquisition", id, nameof(BookAcquisitionModel.AssetId), row.AssetId, item.AssetId);
            CompareField(failures, "acquisition", id, nameof(BookAcquisitionModel.AssetFormat), row.AssetFormat, item.AssetFormat);
            CompareField(failures, "acquisition", id, nameof(BookAcquisitionModel.ImportedExtension), row.ImportedExtension, item.ImportedExtension);
            CompareField(failures, "acquisition", id, nameof(BookAcquisitionModel.SourceUrl), row.SourceUrl, item.SourceUrl);
            CompareField(failures, "acquisition", id, nameof(BookAcquisitionModel.RightsStatement), row.RightsStatement, item.RightsStatement);
            CompareField(failures, "acquisition", id, nameof(BookAcquisitionModel.AcquiredAt), row.AcquiredAt, item.AcquiredAt);
        }
    }

    private static void CompareNoteImportBookLinks(
        PortableLibraryData data,
        List<NoteImportBookLink> importLinks,
        List<PortableLibraryVerificationFailure> failures)
    {
        var expected = (data.NoteImportBookLinks ?? []).ToDictionary(item => item.Id);
        var actual = importLinks.ToDictionary(item => item.Id);
        CompareIdSets("noteImportBookLink", expected.Keys, actual.Keys, failures);
        foreach (var (id, item) in expected)
        {
            if (!actual.TryGetValue(id, out var row))
            {
                continue;
            }

            CompareField(failures, "noteImportBookLink", id, nameof(NoteImportBookLink.Source), row.Source, item.Source);
            CompareField(failures, "noteImportBookLink", id, nameof(NoteImportBookLink.SourceKey), row.SourceKey, item.SourceKey);
            CompareField(failures, "noteImportBookLink", id, nameof(NoteImportBookLink.BookId), row.BookId, item.BookId);
            CompareField(failures, "noteImportBookLink", id, nameof(NoteImportBookLink.CreatedAtUtc), row.CreatedAtUtc, item.CreatedAtUtc);
        }
    }

    private static void CompareAssistantSettings(
        PortableLibraryData data,
        List<AssistantSettingsModel> assistantSettings,
        List<PortableLibraryVerificationFailure> failures)
    {
        if (assistantSettings.Count > 1)
        {
            failures.Add(Failure(
                PortableLibraryVerificationErrorCodes.SingletonMismatch,
                "assistantSettings",
                null,
                "Count",
                "The candidate database contains more than one assistant settings row."));
        }

        var actualRows = assistantSettings
            .Where(row => row.CaptureProcessingMode is not null)
            .ToList();
        if (data.AssistantSettings is { CaptureProcessingMode: not null } expected)
        {
            if (actualRows.Count != 1)
            {
                failures.Add(Failure(
                    PortableLibraryVerificationErrorCodes.SingletonMismatch,
                    "assistantSettings",
                    null,
                    nameof(AssistantSettingsModel.CaptureProcessingMode),
                    "The prepared import carries a portable assistant setting but the candidate does not have exactly one active value."));
                return;
            }

            CompareField(
                failures,
                "assistantSettings",
                AssistantSettingsModel.SingletonId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                nameof(AssistantSettingsModel.CaptureProcessingMode),
                actualRows[0].CaptureProcessingMode,
                expected.CaptureProcessingMode);
            CompareField(
                failures,
                "assistantSettings",
                AssistantSettingsModel.SingletonId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                nameof(AssistantSettingsModel.UpdatedAtUtc),
                actualRows[0].UpdatedAtUtc,
                expected.UpdatedAtUtc);
        }
        else if (actualRows.Count != 0)
        {
            failures.Add(Failure(
                PortableLibraryVerificationErrorCodes.SingletonMismatch,
                "assistantSettings",
                null,
                nameof(AssistantSettingsModel.CaptureProcessingMode),
                "The prepared import carries no portable assistant setting but the candidate has an active value."));
        }
    }

    private static void CompareCandidateCounts(
        PortableLibraryData data,
        MigrationArchiveCounts expectedCounts,
        long works,
        long books,
        long collections,
        long memberships,
        long notes,
        long topics,
        long noteTopics,
        long writings,
        long writingNotes,
        long acquisitions,
        long importLinks,
        long assistantPortableValues,
        long mediaFiles,
        List<PortableLibraryVerificationFailure> failures)
    {
        var actual = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            [nameof(MigrationArchiveCounts.Works)] = works,
            [nameof(MigrationArchiveCounts.Books)] = books,
            [nameof(MigrationArchiveCounts.Notes)] = notes,
            [nameof(MigrationArchiveCounts.Topics)] = topics,
            [nameof(MigrationArchiveCounts.NoteTopics)] = noteTopics,
            [nameof(MigrationArchiveCounts.Writings)] = writings,
            [nameof(MigrationArchiveCounts.WritingNotes)] = writingNotes,
            [nameof(MigrationArchiveCounts.Collections)] = collections,
            [nameof(MigrationArchiveCounts.CollectionMemberships)] = memberships,
            [nameof(MigrationArchiveCounts.Acquisitions)] = acquisitions,
            [nameof(MigrationArchiveCounts.AssistantSettings)] = assistantPortableValues,
            [nameof(MigrationArchiveCounts.NoteImportBookLinks)] = importLinks,
            [nameof(MigrationArchiveCounts.MediaEntries)] = mediaFiles,
        };

        var expectedAssistant = data.AssistantSettings is { CaptureProcessingMode: not null } ? 1L : 0L;
        foreach (var property in CountProperties)
        {
            if (!actual.TryGetValue(property.Name, out var value))
            {
                failures.Add(Failure(
                    PortableLibraryVerificationErrorCodes.CountUnverified,
                    "counts",
                    null,
                    property.Name,
                    "A portable count dimension has no candidate verification mapping."));
                continue;
            }

            var expected = property.Name == nameof(MigrationArchiveCounts.AssistantSettings)
                ? expectedAssistant
                : (long)property.GetValue(expectedCounts)!;
            if (value != expected)
            {
                failures.Add(Failure(
                    PortableLibraryVerificationErrorCodes.CountMismatch,
                    "counts",
                    null,
                    property.Name,
                    "A candidate portable count does not match the prepared import."));
            }
        }
    }

    private static async Task<(long Files, long Bytes)> VerifyCandidateMediaRootAsync(
        string candidateMediaRoot,
        IReadOnlyList<PortableArchiveMediaEntry> expected,
        List<PortableLibraryVerificationFailure> failures,
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

        var seen = new HashSet<string>(equality);
        long bytes = 0;

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
                    if (IsReconstructibleDerivedMedia(Path.GetFileName(fullPath)))
                    {
                        continue;
                    }

                    failures.Add(Failure(
                        PortableLibraryVerificationErrorCodes.MediaUnexpectedFile,
                        "media",
                        null,
                        null,
                        "The candidate media root contains a file the prepared import does not declare."));
                    continue;
                }

                seen.Add(fullPath);
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
                var hash = await HashStreamAsync(stream, descriptor.Length, ct).ConfigureAwait(false);
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

                bytes += info.Length;
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
                     .Where(pair => !seen.Contains(pair.Key))
                     .Select(pair => pair.Value))
        {
            failures.Add(Failure(
                PortableLibraryVerificationErrorCodes.MediaMissing,
                "media",
                descriptor.BookId.ToString("D"),
                descriptor.Kind,
                "A prepared media item is missing from the candidate media root."));
        }

        return (seen.Count, bytes);
    }

    private static bool TryResolveCandidateMediaPath(
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

        if (descriptor.Kind is not (PortableArchiveFormat.BookMediaKind or PortableArchiveFormat.CoverMediaKind))
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

        string canonicalFileName;
        try
        {
            canonicalFileName = descriptor.Kind == PortableArchiveFormat.BookMediaKind
                ? "book" + BookAssetFormats.RequireBookExtension(descriptor.FileName)
                : "cover" + BookAssetFormats.RequireCoverExtension(descriptor.FileName);
        }
        catch (InvalidOperationException)
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

        var extension = Path.GetExtension(descriptor.FileName).ToLowerInvariant();
        var expectedArchivePath = $"media/books/{descriptor.BookId:N}/{descriptor.Kind}{extension}";
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

    private static bool IsReconstructibleDerivedMedia(string fileName) =>
        fileName.StartsWith("cover-thumb-", StringComparison.OrdinalIgnoreCase)
        && fileName.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);

    private static void CompareIdSets(
        string entity,
        IEnumerable<Guid> expected,
        IEnumerable<Guid> actual,
        List<PortableLibraryVerificationFailure> failures)
    {
        var expectedSet = expected.ToHashSet();
        var actualSet = actual.ToHashSet();
        foreach (var missing in expectedSet.Except(actualSet).OrderBy(id => id))
        {
            failures.Add(Failure(
                PortableLibraryVerificationErrorCodes.MissingEntity,
                entity,
                missing.ToString("D"),
                null,
                $"A {entity} from the prepared import is missing from the candidate database."));
        }

        foreach (var extra in actualSet.Except(expectedSet).OrderBy(id => id))
        {
            failures.Add(Failure(
                PortableLibraryVerificationErrorCodes.UnexpectedEntity,
                entity,
                extra.ToString("D"),
                null,
                $"The candidate database contains a {entity} the prepared import does not declare."));
        }
    }

    private static void CompareRelationshipKeys<TKey>(
        string entity,
        IEnumerable<TKey> expected,
        IEnumerable<TKey> actual,
        List<PortableLibraryVerificationFailure> failures,
        string relationship)
        where TKey : notnull
    {
        var expectedSet = expected.ToHashSet();
        var actualSet = actual.ToHashSet();
        var missing = expectedSet.Except(actualSet).Count();
        var extra = actualSet.Except(expectedSet).Count();
        if (missing == 0 && extra == 0)
        {
            return;
        }

        failures.Add(Failure(
            PortableLibraryVerificationErrorCodes.RelationshipMismatch,
            entity,
            null,
            relationship,
            $"The candidate {relationship} set differs from the prepared import ({missing} missing, {extra} unexpected)."));
    }

    private static void CompareField<T>(
        List<PortableLibraryVerificationFailure> failures,
        string entity,
        Guid id,
        string field,
        T actual,
        T expected) =>
        CompareField(failures, entity, id.ToString("D"), field, actual, expected);

    private static void CompareField<T>(
        List<PortableLibraryVerificationFailure> failures,
        string entity,
        string? id,
        string field,
        T actual,
        T expected)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
        {
            failures.Add(Failure(
                PortableLibraryVerificationErrorCodes.FieldMismatch,
                entity,
                id,
                field,
                $"{entity} field '{field}' does not match the prepared import."));
        }
    }

    private static void CompareField(
        List<PortableLibraryVerificationFailure> failures,
        string entity,
        Guid id,
        string field,
        string? actual,
        string? expected,
        bool ignoreCase)
    {
        var equal = ignoreCase
            ? string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)
            : string.Equals(actual, expected, StringComparison.Ordinal);
        if (!equal)
        {
            failures.Add(Failure(
                PortableLibraryVerificationErrorCodes.FieldMismatch,
                entity,
                id.ToString("D"),
                field,
                $"{entity} field '{field}' does not match the prepared import."));
        }
    }

    private static string ConcreteType(BookModel book) => book switch
    {
        PhysicalBookModel => "physical",
        EBookModel => "ebook",
        AudioBookModel => "audiobook",
        _ => throw new PortableLibraryVerificationException(
            PortableLibraryVerificationErrorCodes.ExpectedStateInvalid,
            "The candidate database contains an unsupported book type."),
    };

    private static async Task<byte[]> ReadBoundedAsync(
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

    private static async Task<(long Length, string Sha256)> HashStreamAsync(
        Stream source,
        long maxBytes,
        CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[PortableArchiveLimits.CopyBufferBytes];
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
                    "The payload exceeds its declared length.");
            }

            hash.AppendData(buffer, 0, read);
        }

        return (total, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

}

/// <summary>
/// The verifier's declared coverage for one portable archive record. The parity test
/// over the portable archive records fails when a record or property gains no
/// compared/derived decision here.
/// </summary>
internal sealed record PortableLibraryRecordCoverage(
    IReadOnlySet<string> ComparedProperties,
    IReadOnlyDictionary<string, string> NotComparedProperties);

/// <summary>
/// Two-way coverage map between every archive record reachable from
/// <see cref="PortableLibraryData"/> and the comparison performed by
/// <see cref="PortableLibraryVerifier"/>. A newly added archive property that is not
/// listed here fails the verifier-coverage parity test.
/// </summary>
internal static class PortableLibraryVerificationCoverage
{
    internal static IReadOnlyDictionary<string, PortableLibraryRecordCoverage> ByRecordName { get; } =
        new Dictionary<string, PortableLibraryRecordCoverage>(StringComparer.Ordinal)
        {
            [nameof(PortableLibraryData)] = Compared(
                nameof(PortableLibraryData.Version),
                nameof(PortableLibraryData.Works),
                nameof(PortableLibraryData.Books),
                nameof(PortableLibraryData.Collections),
                nameof(PortableLibraryData.BookCollections),
                nameof(PortableLibraryData.Notes),
                nameof(PortableLibraryData.Topics),
                nameof(PortableLibraryData.NoteTopics),
                nameof(PortableLibraryData.Writings),
                nameof(PortableLibraryData.BookAcquisitions),
                nameof(PortableLibraryData.AssistantSettings),
                nameof(PortableLibraryData.WritingNotes),
                nameof(PortableLibraryData.NoteImportBookLinks)),

            [nameof(PortableWork)] = Compared(
                nameof(PortableWork.Id),
                nameof(PortableWork.Title),
                nameof(PortableWork.Author),
                nameof(PortableWork.CreatedAt)),

            [nameof(PortableBook)] = Compared(
                nameof(PortableBook.Id),
                nameof(PortableBook.WorkId),
                nameof(PortableBook.Type),
                nameof(PortableBook.Status),
                nameof(PortableBook.StatusMessage),
                nameof(PortableBook.Title),
                nameof(PortableBook.Author),
                nameof(PortableBook.Metadata),
                nameof(PortableBook.Progress),
                nameof(PortableBook.CreatedAt),
                nameof(PortableBook.Isbn),
                nameof(PortableBook.PageCount),
                nameof(PortableBook.Asin),
                nameof(PortableBook.Duration),
                nameof(PortableBook.Narrator),
                nameof(PortableBook.ChaptersJson),
                nameof(PortableBook.HasBookFile),
                nameof(PortableBook.HasCover)),

            [nameof(PortableBookMetadata)] = Compared(
                nameof(PortableBookMetadata.Subtitle),
                nameof(PortableBookMetadata.Description),
                nameof(PortableBookMetadata.Editor),
                nameof(PortableBookMetadata.Translator),
                nameof(PortableBookMetadata.Publisher),
                nameof(PortableBookMetadata.PlaceOfPublication),
                nameof(PortableBookMetadata.PublishedDate),
                nameof(PortableBookMetadata.Language),
                nameof(PortableBookMetadata.Categories),
                nameof(PortableBookMetadata.Edition),
                nameof(PortableBookMetadata.Series),
                nameof(PortableBookMetadata.VolumeNumber)),

            [nameof(PortableReadingProgress)] = Compared(
                nameof(PortableReadingProgress.LastLocation),
                nameof(PortableReadingProgress.ProgressPercent),
                nameof(PortableReadingProgress.Rating),
                nameof(PortableReadingProgress.IsFavorite),
                nameof(PortableReadingProgress.PersonalReview),
                nameof(PortableReadingProgress.LastReadAt),
                nameof(PortableReadingProgress.FinishedAt)),

            [nameof(PortableCollection)] = Compared(
                nameof(PortableCollection.Id),
                nameof(PortableCollection.Name),
                nameof(PortableCollection.ParentId)),

            [nameof(PortableBookCollection)] = Compared(
                nameof(PortableBookCollection.BookId),
                nameof(PortableBookCollection.CollectionId),
                nameof(PortableBookCollection.AddedAt)),

            [nameof(PortableNote)] = Compared(
                nameof(PortableNote.Id),
                nameof(PortableNote.Content),
                nameof(PortableNote.CfiRange),
                nameof(PortableNote.SelectedText),
                nameof(PortableNote.CreatedAt),
                nameof(PortableNote.BookId),
                nameof(PortableNote.RawContent),
                nameof(PortableNote.CaptureSource),
                nameof(PortableNote.ProcessingMode),
                nameof(PortableNote.SourceAnchorKind),
                nameof(PortableNote.SourceAnchorValue),
                nameof(PortableNote.AnchorVerified)),

            [nameof(PortableTopic)] = Compared(
                nameof(PortableTopic.Id),
                nameof(PortableTopic.Topic)),

            [nameof(PortableNoteTopic)] = Compared(
                nameof(PortableNoteTopic.NoteId),
                nameof(PortableNoteTopic.TopicId)),

            [nameof(PortableWriting)] = Compared(
                nameof(PortableWriting.Id),
                nameof(PortableWriting.Name),
                nameof(PortableWriting.Type),
                nameof(PortableWriting.Content),
                nameof(PortableWriting.ParentId),
                nameof(PortableWriting.CreatedAt),
                nameof(PortableWriting.UpdatedAt)),

            [nameof(PortableWritingNote)] = Compared(
                nameof(PortableWritingNote.WritingId),
                nameof(PortableWritingNote.NoteId),
                nameof(PortableWritingNote.AddedAt)),

            [nameof(PortableBookAcquisition)] = Compared(
                nameof(PortableBookAcquisition.Id),
                nameof(PortableBookAcquisition.BookId),
                nameof(PortableBookAcquisition.ProviderId),
                nameof(PortableBookAcquisition.ProviderDisplayName),
                nameof(PortableBookAcquisition.ExternalId),
                nameof(PortableBookAcquisition.AssetId),
                nameof(PortableBookAcquisition.AssetFormat),
                nameof(PortableBookAcquisition.ImportedExtension),
                nameof(PortableBookAcquisition.SourceUrl),
                nameof(PortableBookAcquisition.RightsStatement),
                nameof(PortableBookAcquisition.AcquiredAt)),

            [nameof(PortableAssistantSettings)] = Compared(
                nameof(PortableAssistantSettings.CaptureProcessingMode),
                nameof(PortableAssistantSettings.UpdatedAtUtc)),

            [nameof(PortableNoteImportBookLink)] = Compared(
                nameof(PortableNoteImportBookLink.Id),
                nameof(PortableNoteImportBookLink.Source),
                nameof(PortableNoteImportBookLink.SourceKey),
                nameof(PortableNoteImportBookLink.BookId),
                nameof(PortableNoteImportBookLink.CreatedAtUtc)),

            [nameof(PortableArchiveManifest)] = Partial(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [nameof(PortableArchiveManifest.ExportedAtUtc)] =
                        "Exporter timestamp metadata is not portable library state and is not recreated by activation.",
                    [nameof(PortableArchiveManifest.ApplicationVersion)] =
                        "Exporter product version metadata is informational and is not portable library state.",
                },
                nameof(PortableArchiveManifest.Format),
                nameof(PortableArchiveManifest.FormatVersion),
                nameof(PortableArchiveManifest.DataVersion),
                nameof(PortableArchiveManifest.Counts),
                nameof(PortableArchiveManifest.Data),
                nameof(PortableArchiveManifest.Media)),

            [nameof(PortableArchiveCounts)] = Compared(
                nameof(PortableArchiveCounts.Works),
                nameof(PortableArchiveCounts.Books),
                nameof(PortableArchiveCounts.Collections),
                nameof(PortableArchiveCounts.BookCollections),
                nameof(PortableArchiveCounts.Notes),
                nameof(PortableArchiveCounts.Topics),
                nameof(PortableArchiveCounts.NoteTopics),
                nameof(PortableArchiveCounts.Writings),
                nameof(PortableArchiveCounts.BookAcquisitions)),

            [nameof(PortableArchivePayload)] = Compared(
                nameof(PortableArchivePayload.Path),
                nameof(PortableArchivePayload.Length),
                nameof(PortableArchivePayload.Sha256)),

            [nameof(PortableArchiveMediaEntry)] = Partial(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [nameof(PortableArchiveMediaEntry.ContentType)] =
                        "Content type is HTTP response metadata; it is not persisted into the candidate library and is not portable library state.",
                },
                nameof(PortableArchiveMediaEntry.BookId),
                nameof(PortableArchiveMediaEntry.Kind),
                nameof(PortableArchiveMediaEntry.Path),
                nameof(PortableArchiveMediaEntry.FileName),
                nameof(PortableArchiveMediaEntry.Length),
                nameof(PortableArchiveMediaEntry.Sha256)),
        };

    private static PortableLibraryRecordCoverage Compared(params string[] properties) =>
        new(
            properties.ToHashSet(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal));

    private static PortableLibraryRecordCoverage Partial(
        IReadOnlyDictionary<string, string> notCompared,
        params string[] compared) =>
        new(compared.ToHashSet(StringComparer.Ordinal), notCompared);
}

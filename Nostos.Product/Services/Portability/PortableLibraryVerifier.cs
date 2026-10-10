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
/// Every candidate comparison is declared in an executable comparison table, and
/// <see cref="CandidateComparisonSpecs"/> projects those same tables so the
/// coverage tests enforce property coverage without a parallel hand-written list.
/// </summary>
public sealed class PortableLibraryVerifier : IPortableLibraryVerifier
{
    /// <summary>
    /// Maximum number of specific failure details retained in a report. Additional
    /// mismatches are counted in <see cref="PortableLibraryVerificationReport.FailureCount"/>
    /// but not retained, so a heavily divergent candidate cannot exhaust memory.
    /// </summary>
    internal const int MaxRetainedFailures = 64;

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

    private static readonly string[] ManifestCountProperties = typeof(PortableArchiveCounts)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(property => property.CanWrite)
        .Select(property => property.Name)
        .OrderBy(name => name, StringComparer.Ordinal)
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

    /// <summary>
    /// Kinds verified by <see cref="VerifyCandidateDatabaseAsync"/>: every relational
    /// portable kind. The media entry kind lives in asset storage, not in the
    /// relational candidate, and is verified by <see cref="VerifyMediaAsync"/>.
    /// </summary>
    internal static readonly IReadOnlyList<string> CandidateDatabaseVerifiedKinds =
        CandidateVerifiedKinds
            .Where(kind => !string.Equals(
                kind,
                nameof(PortableArchiveMediaEntry),
                StringComparison.Ordinal))
            .ToArray();

    private static readonly IReadOnlyDictionary<string, string> LibraryDataExclusions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(PortableLibraryData.Works)] = "Container; every PortableWork element is compared by the PortableWork and WorkModel comparison table.",
            [nameof(PortableLibraryData.Books)] = "Container; every PortableBook element is compared by the PortableBook comparison table.",
            [nameof(PortableLibraryData.Collections)] = "Container; every PortableCollection element is compared by the PortableCollection comparison table.",
            [nameof(PortableLibraryData.BookCollections)] = "Container; every PortableBookCollection element is compared by the PortableBookCollection comparison table.",
            [nameof(PortableLibraryData.Notes)] = "Container; every PortableNote element is compared by the PortableNote comparison table.",
            [nameof(PortableLibraryData.Topics)] = "Container; every PortableTopic element is compared by the PortableTopic comparison table.",
            [nameof(PortableLibraryData.NoteTopics)] = "Container; every PortableNoteTopic element is compared by the PortableNoteTopic comparison table.",
            [nameof(PortableLibraryData.Writings)] = "Container; every PortableWriting element is compared by the PortableWriting comparison table.",
            [nameof(PortableLibraryData.BookAcquisitions)] = "Container; every PortableBookAcquisition element is compared by the PortableBookAcquisition comparison table.",
            [nameof(PortableLibraryData.AssistantSettings)] = "Singleton; presence and both fields are compared by CompareAssistantSettings and the PortableAssistantSettings comparison table.",
            [nameof(PortableLibraryData.WritingNotes)] = "Container; every PortableWritingNote element is compared by the PortableWritingNote comparison table.",
            [nameof(PortableLibraryData.NoteImportBookLinks)] = "Container; every PortableNoteImportBookLink element is compared by the PortableNoteImportBookLink comparison table.",
        };

    private static readonly IReadOnlyDictionary<string, string> ManifestExclusions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(PortableArchiveManifest.ExportedAtUtc)] =
                "Exporter timestamp metadata is not portable library state and is not recreated by activation.",
            [nameof(PortableArchiveManifest.ApplicationVersion)] =
                "Exporter product version metadata is informational and is not portable library state.",
        };

    private static readonly IReadOnlyDictionary<string, string> MediaEntryExclusions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(PortableArchiveMediaEntry.ContentType)] =
                "Content type is HTTP response metadata; it is not persisted into the candidate library and is not portable library state.",
        };

    private static readonly IReadOnlyDictionary<string, string> EmptyExclusions =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public async Task<PortablePreparedImportVerification> VerifyPreparedImportAsync(
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

    public async Task<PortableLibraryVerificationReport> VerifyCandidateDatabaseAsync(
        NostosDbContext candidateDatabase,
        IPreparedPortableImport prepared,
        PortablePreparedImportVerification expected,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidateDatabase);
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(expected);

        if (ValidateExpectedState(prepared, expected) is { } mismatch)
        {
            return mismatch;
        }

        var comparison = await CompareCandidateDatabaseAsync(
            candidateDatabase,
            expected.Data!,
            expected.Descriptors,
            ct).ConfigureAwait(false);
        CompareCandidateCounts(
            expected.Metadata.Counts,
            comparison.Counts,
            mediaFiles: null,
            comparison.Failures);

        return new PortableLibraryVerificationReport(
            comparison.Failures.TotalCount == 0,
            comparison.Failures,
            CandidateDatabaseVerifiedKinds,
            comparison.RowsVerified,
            MediaFilesVerified: 0,
            MediaBytesVerified: 0,
            comparison.Failures.TotalCount);
    }

    public async Task<PortableLibraryVerificationReport> VerifyCandidateAsync(
        NostosDbContext candidateDatabase,
        string candidateMediaRoot,
        IPreparedPortableImport prepared,
        PortablePreparedImportVerification expected,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidateDatabase);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateMediaRoot);
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(expected);

        if (ValidateExpectedState(prepared, expected) is { } mismatch)
        {
            return mismatch;
        }

        var comparison = await CompareCandidateDatabaseAsync(
            candidateDatabase,
            expected.Data!,
            expected.Descriptors,
            ct).ConfigureAwait(false);
        var (mediaFiles, mediaBytes) = await VerifyCandidateMediaRootAsync(
            candidateMediaRoot,
            expected.Descriptors,
            comparison.Failures,
            ct).ConfigureAwait(false);
        CompareCandidateCounts(
            expected.Metadata.Counts,
            comparison.Counts,
            mediaFiles,
            comparison.Failures);

        return new PortableLibraryVerificationReport(
            comparison.Failures.TotalCount == 0,
            comparison.Failures,
            CandidateVerifiedKinds,
            comparison.RowsVerified,
            mediaFiles,
            mediaBytes,
            comparison.Failures.TotalCount);
    }

    private static PortableLibraryVerificationReport? ValidateExpectedState(
        IPreparedPortableImport prepared,
        PortablePreparedImportVerification expected)
    {
        if (!expected.Passed || expected.Data is null)
        {
            throw new PortableLibraryVerificationException(
                PortableLibraryVerificationErrorCodes.ExpectedStateInvalid,
                "Candidate verification requires a prepared import that passed full verification.");
        }

        if (expected.StagingId != prepared.Metadata.StagingId
            || expected.DataBytes != prepared.Metadata.DataBytes
            || !PortableArchiveValidation.FixedHashEquals(
                expected.DataSha256,
                prepared.Metadata.DataSha256))
        {
            var mismatch = new FailureList();
            mismatch.Add(Failure(
                PortableLibraryVerificationErrorCodes.ExpectedStateMismatch,
                "expected",
                prepared.Metadata.StagingId.Value.ToString("D"),
                nameof(PreparedPortableImportMetadata.DataSha256),
                "The verified expected state does not belong to the supplied prepared import."));
            return new PortableLibraryVerificationReport(
                Passed: false,
                mismatch,
                VerifiedKinds: [],
                PortableRowsVerified: 0,
                MediaFilesVerified: 0,
                MediaBytesVerified: 0,
                FailureCount: mismatch.TotalCount);
        }

        return null;
    }

    /// <summary>
    /// The one relational candidate comparison shared by
    /// <see cref="VerifyCandidateAsync"/> and
    /// <see cref="VerifyCandidateDatabaseAsync"/>. It reads the supplied context
    /// with read-only, provider-neutral LINQ queries only: no raw SQL, no PRAGMA and
    /// no row identifiers, so any EF provider can serve the comparison. The caller
    /// owns media verification and adds that dimension's count facts.
    /// </summary>
    private static async Task<CandidateDatabaseComparison> CompareCandidateDatabaseAsync(
        NostosDbContext candidateDatabase,
        PortableLibraryData data,
        IReadOnlyList<PortableArchiveMediaEntry> descriptors,
        CancellationToken ct)
    {
        var failures = new FailureList();
        var mediaByKey = descriptors
            .GroupBy(descriptor => (descriptor.BookId, descriptor.Kind))
            .ToDictionary(group => group.Key, group => group.First());

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
        CompareBooks(data, books, mediaByKey, failures);
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

        var rowsVerified =
            works.Count + books.Count + collections.Count + memberships.Count + notes.Count + topics.Count
            + noteTopics.Count + writings.Count + writingNotes.Count + acquisitions.Count + importLinks.Count
            + assistantSettings.Count;

        return new CandidateDatabaseComparison(
            failures,
            new CandidateRowCounts(
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
                assistantSettings.Count),
            rowsVerified);
    }

    /// <summary>
    /// Verifies only the relational portable state of a database against an
    /// extracted recovery payload. Recovery restore uses this after the swap:
    /// the previous library's media is verified separately against the retained
    /// recovery manifest, which describes every retained file (including
    /// derived thumbnails the portable media inventory never carries).
    /// </summary>
    public async Task<PortableLibraryVerificationReport> VerifyDatabaseAgainstExpectedAsync(
        NostosDbContext database,
        PortableRecoveryPayload expected,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(expected);

        var comparison = await CompareCandidateDatabaseAsync(
            database,
            expected.Data,
            expected.PrimaryMedia,
            ct).ConfigureAwait(false);
        CompareCandidateCounts(
            expected.Counts,
            comparison.Counts,
            expected.PrimaryMedia.Count,
            comparison.Failures);

        return new PortableLibraryVerificationReport(
            comparison.Failures.TotalCount == 0,
            comparison.Failures,
            CandidateDatabaseVerifiedKinds,
            comparison.RowsVerified,
            MediaFilesVerified: 0,
            MediaBytesVerified: 0,
            comparison.Failures.TotalCount);
    }

    public Task<PortableLibraryVerificationReport> VerifyMediaAsync(
        IBookAssetStorage assets,
        IReadOnlyList<PortableArchiveMediaEntry> expected,
        CancellationToken ct = default) =>
        VerifyMediaAsync(assets, tracks: null, expected, ct);

    /// <summary>
    /// Verifies stored media including the tracks of multi-track audiobooks.
    /// Without <paramref name="tracks"/> an expected track is reported as a
    /// failure, never skipped.
    /// </summary>
    public async Task<PortableLibraryVerificationReport> VerifyMediaAsync(
        IBookAssetStorage assets,
        IBookTrackStorage? tracks,
        IReadOnlyList<PortableArchiveMediaEntry> expected,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(expected);

        var failures = new FailureList();
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
            failures.TotalCount == 0,
            failures,
            [nameof(PortableArchiveMediaEntry)],
            PortableRowsVerified: 0,
            files,
            bytes,
            failures.TotalCount);
    }

    private static PortablePreparedImportVerification Result(
        PreparedPortableImportMetadata metadata,
        IReadOnlyList<PortablePreparedMedia> stagedMedia,
        IReadOnlyList<PortableArchiveMediaEntry> descriptors,
        PortableLibraryData? data,
        FailureList failures) =>
        new(failures.TotalCount == 0, failures, metadata, stagedMedia, descriptors, data);

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

    private static PortableLibraryData? TryDeserializeData(
        byte[] bytes,
        FailureList failures)
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
        FailureList failures)
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

    private static void CompareManifestAgainstPrepared(
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

    private static void CompareManifestCounts(
        PortableArchiveManifest manifest,
        PortableLibraryData data,
        FailureList failures)
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
        FailureList failures)
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
        FailureList failures)
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

    private static async Task HashStagedMediaAsync(
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
        FailureList failures)
    {
        var expected = data.Works.ToDictionary(item => item.Id);
        var actual = works.ToDictionary(item => item.Id);
        CompareIdSets("work", expected.Keys, actual.Keys, failures);
        foreach (var (id, item) in expected)
        {
            if (actual.TryGetValue(id, out var row))
            {
                Run(WorkComparisons, new WorkContext(row, item), failures);
            }
        }
    }

    private static void CompareBooks(
        PortableLibraryData data,
        List<BookModel> books,
        IReadOnlyDictionary<(Guid BookId, string Kind), PortableArchiveMediaEntry> media,
        FailureList failures)
    {
        var expected = data.Books.ToDictionary(item => item.Id);
        var actual = books.ToDictionary(item => item.Id);
        CompareIdSets("book", expected.Keys, actual.Keys, failures);
        foreach (var (id, item) in expected)
        {
            if (actual.TryGetValue(id, out var row))
            {
                Run(BookComparisons, new BookContext(row, item, media), failures);
            }
        }
    }

    private static void CompareCollections(
        PortableLibraryData data,
        List<CollectionModel> collections,
        FailureList failures)
    {
        var expected = data.Collections.ToDictionary(item => item.Id);
        var actual = collections.ToDictionary(item => item.Id);
        CompareIdSets("collection", expected.Keys, actual.Keys, failures);
        foreach (var (id, item) in expected)
        {
            if (actual.TryGetValue(id, out var row))
            {
                Run(CollectionComparisons, new CollectionContext(row, item), failures);
            }
        }
    }

    private static void CompareBookCollections(
        PortableLibraryData data,
        List<BookCollectionModel> memberships,
        FailureList failures) =>
        Run(
            BookCollectionComparisons,
            new MembershipContext(memberships, data.BookCollections),
            failures);

    private static void CompareNotes(
        PortableLibraryData data,
        List<NoteModel> notes,
        FailureList failures)
    {
        var expected = data.Notes.ToDictionary(item => item.Id);
        var actual = notes.ToDictionary(item => item.Id);
        CompareIdSets("note", expected.Keys, actual.Keys, failures);
        foreach (var (id, item) in expected)
        {
            if (actual.TryGetValue(id, out var row))
            {
                Run(NoteComparisons, new NoteContext(row, item), failures);
            }
        }
    }

    private static void CompareTopics(
        PortableLibraryData data,
        List<TopicModel> topics,
        FailureList failures)
    {
        var expected = data.Topics.ToDictionary(item => item.Id);
        var actual = topics.ToDictionary(item => item.Id);
        CompareIdSets("topic", expected.Keys, actual.Keys, failures);
        foreach (var (id, item) in expected)
        {
            if (actual.TryGetValue(id, out var row))
            {
                Run(TopicComparisons, new TopicContext(row, item), failures);
            }
        }
    }

    private static void CompareNoteTopics(
        PortableLibraryData data,
        List<NoteTopicModel> noteTopics,
        FailureList failures) =>
        Run(
            NoteTopicComparisons,
            new NoteTopicContext(noteTopics, data.NoteTopics),
            failures);

    private static void CompareWritings(
        PortableLibraryData data,
        List<WritingModel> writings,
        FailureList failures)
    {
        var expected = data.Writings.ToDictionary(item => item.Id);
        var actual = writings.ToDictionary(item => item.Id);
        CompareIdSets("writing", expected.Keys, actual.Keys, failures);
        foreach (var (id, item) in expected)
        {
            if (actual.TryGetValue(id, out var row))
            {
                Run(WritingComparisons, new WritingContext(row, item), failures);
            }
        }
    }

    private static void CompareWritingNotes(
        PortableLibraryData data,
        List<WritingNoteModel> writingNotes,
        FailureList failures) =>
        Run(
            WritingNoteComparisons,
            new WritingNoteContext(writingNotes, data.WritingNotes ?? []),
            failures);

    private static void CompareAcquisitions(
        PortableLibraryData data,
        List<BookAcquisitionModel> acquisitions,
        FailureList failures)
    {
        var expected = data.BookAcquisitions.ToDictionary(item => item.Id);
        var actual = acquisitions.ToDictionary(item => item.Id);
        CompareIdSets("acquisition", expected.Keys, actual.Keys, failures);
        foreach (var (id, item) in expected)
        {
            if (actual.TryGetValue(id, out var row))
            {
                Run(AcquisitionComparisons, new AcquisitionContext(row, item), failures);
            }
        }
    }

    private static void CompareNoteImportBookLinks(
        PortableLibraryData data,
        List<NoteImportBookLink> importLinks,
        FailureList failures)
    {
        var expected = (data.NoteImportBookLinks ?? []).ToDictionary(item => item.Id);
        var actual = importLinks.ToDictionary(item => item.Id);
        CompareIdSets("noteImportBookLink", expected.Keys, actual.Keys, failures);
        foreach (var (id, item) in expected)
        {
            if (actual.TryGetValue(id, out var row))
            {
                Run(ImportLinkComparisons, new ImportLinkContext(row, item), failures);
            }
        }
    }

    private static void CompareAssistantSettings(
        PortableLibraryData data,
        List<AssistantSettingsModel> assistantSettings,
        FailureList failures)
    {
        if (data.AssistantSettings is null)
        {
            if (assistantSettings.Count != 0)
            {
                failures.Add(Failure(
                    PortableLibraryVerificationErrorCodes.SingletonMismatch,
                    "assistantSettings",
                    null,
                    nameof(AssistantSettingsModel.CaptureProcessingMode),
                    "The prepared import carries no portable assistant setting but the candidate has a row."));
            }

            return;
        }

        if (assistantSettings.Count != 1)
        {
            failures.Add(Failure(
                PortableLibraryVerificationErrorCodes.SingletonMismatch,
                "assistantSettings",
                null,
                nameof(AssistantSettingsModel.CaptureProcessingMode),
                "The prepared import carries a portable assistant setting but the candidate does not have exactly one row."));
            return;
        }

        Run(AssistantComparisons, new AssistantContext(assistantSettings[0], data.AssistantSettings), failures);
    }

    private static async Task<(long Files, long Bytes)> VerifyCandidateMediaRootAsync(
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

    private static void CompareCandidateCounts(
        MigrationArchiveCounts expectedCounts,
        CandidateRowCounts counts,
        long? mediaFiles,
        FailureList failures)
    {
        var actual = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            [nameof(MigrationArchiveCounts.Works)] = counts.Works,
            [nameof(MigrationArchiveCounts.Books)] = counts.Books,
            [nameof(MigrationArchiveCounts.Notes)] = counts.Notes,
            [nameof(MigrationArchiveCounts.Topics)] = counts.Topics,
            [nameof(MigrationArchiveCounts.NoteTopics)] = counts.NoteTopics,
            [nameof(MigrationArchiveCounts.Writings)] = counts.Writings,
            [nameof(MigrationArchiveCounts.WritingNotes)] = counts.WritingNotes,
            [nameof(MigrationArchiveCounts.Collections)] = counts.Collections,
            [nameof(MigrationArchiveCounts.CollectionMemberships)] = counts.Memberships,
            [nameof(MigrationArchiveCounts.Acquisitions)] = counts.Acquisitions,
            [nameof(MigrationArchiveCounts.AssistantSettings)] = counts.AssistantSettings,
            [nameof(MigrationArchiveCounts.NoteImportBookLinks)] = counts.ImportLinks,
        };

        if (mediaFiles is { } verifiedMediaFiles)
        {
            actual[nameof(MigrationArchiveCounts.MediaEntries)] = verifiedMediaFiles;
        }

        foreach (var property in CountProperties)
        {
            if (mediaFiles is null
                && string.Equals(
                    property.Name,
                    nameof(MigrationArchiveCounts.MediaEntries),
                    StringComparison.Ordinal))
            {
                // Media objects live in the host's asset storage, not in the
                // relational candidate; VerifyMediaAsync owns that dimension.
                continue;
            }

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

            var expected = (long)property.GetValue(expectedCounts)!;
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

    private static void CompareIdSets(
        string entity,
        IEnumerable<Guid> expected,
        IEnumerable<Guid> actual,
        FailureList failures)
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

    private static void CompareField<T>(
        FailureList failures,
        string entity,
        Guid id,
        string field,
        T actual,
        T expected) =>
        CompareField(failures, entity, id.ToString("D"), field, actual, expected);

    private static void CompareField<T>(
        FailureList failures,
        string entity,
        string? id,
        string field,
        T actual,
        T expected)
    {
        if (!CanonicalValuesEqual(actual, expected))
        {
            failures.Add(Failure(
                PortableLibraryVerificationErrorCodes.FieldMismatch,
                entity,
                id,
                field,
                $"{entity} field '{field}' does not match the prepared import."));
        }
    }

    /// <summary>
    /// Structural equality with temporal values canonicalised to UTC microsecond
    /// precision. Some relational providers persist microsecond precision while
    /// .NET keeps 100 ns ticks, so a value written and read back can differ from its
    /// source by up to nine ticks; flooring to microseconds removes exactly that
    /// provider rounding and nothing more. It is not a tolerance: any difference of
    /// one microsecond or more still compares unequal. <see cref="DateTime"/> uses
    /// its wall-clock ticks (matching its own equality); <see cref="DateTimeOffset"/>
    /// uses its UTC ticks (matching its instant equality), so the same instant
    /// expressed with a different offset stays equal.
    /// </summary>
    internal static bool CanonicalValuesEqual<T>(T actual, T expected)
    {
        if (typeof(T) == typeof(DateTime))
        {
            return Canonicalize((DateTime)(object)actual!)
                == Canonicalize((DateTime)(object)expected!);
        }

        if (typeof(T) == typeof(DateTime?))
        {
            return Canonicalize((DateTime?)(object?)actual)
                == Canonicalize((DateTime?)(object?)expected);
        }

        if (typeof(T) == typeof(DateTimeOffset))
        {
            return Canonicalize((DateTimeOffset)(object)actual!)
                == Canonicalize((DateTimeOffset)(object)expected!);
        }

        if (typeof(T) == typeof(DateTimeOffset?))
        {
            return Canonicalize((DateTimeOffset?)(object?)actual)
                == Canonicalize((DateTimeOffset?)(object?)expected);
        }

        return EqualityComparer<T>.Default.Equals(actual, expected);
    }

    internal static DateTime Canonicalize(DateTime value) =>
        new(value.Ticks - value.Ticks % 10, value.Kind);

    internal static DateTime? Canonicalize(DateTime? value) =>
        value is { } present ? Canonicalize(present) : null;

    internal static DateTimeOffset Canonicalize(DateTimeOffset value) =>
        new(value.UtcDateTime.Ticks - value.UtcDateTime.Ticks % 10, TimeSpan.Zero);

    internal static DateTimeOffset? Canonicalize(DateTimeOffset? value) =>
        value is { } present ? Canonicalize(present) : null;

    private static void CompareField(
        FailureList failures,
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

    private static void CompareProjectedKeys<TKey>(
        FailureList failures,
        string entity,
        string field,
        IEnumerable<TKey> expected,
        IEnumerable<TKey> actual)
        where TKey : notnull
    {
        var expectedList = expected.ToList();
        var actualList = actual.ToList();
        if (expectedList.Count == actualList.Count
            && expectedList.ToHashSet().SetEquals(actualList.ToHashSet()))
        {
            return;
        }

        failures.Add(Failure(
            PortableLibraryVerificationErrorCodes.RelationshipMismatch,
            entity,
            null,
            field,
            $"The candidate {entity} {field} set differs from the prepared import."));
    }

    private static void CompareAddedAt<TKey>(
        FailureList failures,
        string entity,
        string field,
        IReadOnlyDictionary<TKey, DateTime> expected,
        IEnumerable<(TKey Key, DateTime AddedAt)> actual)
        where TKey : notnull
    {
        foreach (var (key, addedAt) in actual)
        {
            if (expected.TryGetValue(key, out var expectedAddedAt)
                && !CanonicalValuesEqual(addedAt, expectedAddedAt))
            {
                failures.Add(Failure(
                    PortableLibraryVerificationErrorCodes.FieldMismatch,
                    entity,
                    null,
                    field,
                    $"A candidate {entity} {field} value does not match the prepared import."));
            }
        }
    }

    private static string? ResolveMediaFileName(
        IReadOnlyDictionary<(Guid BookId, string Kind), PortableArchiveMediaEntry> media,
        Guid bookId,
        string kind) =>
        media.TryGetValue((bookId, kind), out var descriptor) ? descriptor.FileName : null;

    private static string ConcreteType(BookModel book) => book switch
    {
        PhysicalBookModel => "physical",
        EBookModel => "ebook",
        AudioBookModel => "audiobook",
        _ => throw new PortableLibraryVerificationException(
            PortableLibraryVerificationErrorCodes.ExpectedStateInvalid,
            "The candidate database contains an unsupported book type."),
    };

    private static void Run<TContext>(
        IReadOnlyList<ComparisonEntry<TContext>> entries,
        TContext context,
        FailureList failures)
    {
        foreach (var entry in entries)
        {
            entry.Compare(context, failures);
        }
    }

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

    /// <summary>An entity property checked by an executable comparison entry.</summary>
    internal sealed record EntityField(string Entity, string Property);

    /// <summary>
    /// Coverage view projected from the executable comparison tables. A record
    /// property is covered when an entry with that property name exists; there is no
    /// way to declare coverage without also declaring the comparison delegate.
    /// </summary>
    internal sealed record RecordComparisonSpec(
        IReadOnlyList<string> ComparedProperties,
        IReadOnlyList<EntityField> CandidateFields,
        IReadOnlyDictionary<string, string> NotComparedProperties);

    private sealed record ComparisonEntry<TContext>(
        string ArchiveProperty,
        IReadOnlyList<EntityField> CandidateFields,
        Action<TContext, FailureList> Compare);

    /// <summary>
    /// Retains a bounded number of specific mismatch details while counting every
    /// detected mismatch, so a heavily divergent candidate cannot exhaust memory.
    /// </summary>
    private sealed class FailureList : List<PortableLibraryVerificationFailure>
    {
        internal long TotalCount { get; private set; }

        internal new void Add(PortableLibraryVerificationFailure failure)
        {
            TotalCount++;
            if (Count < MaxRetainedFailures)
            {
                base.Add(failure);
            }
        }
    }

    /// <summary>
    /// Candidate row counts measured by the shared relational comparison. The media
    /// entry dimension is not part of this record because it is not relational.
    /// </summary>
    private sealed record CandidateRowCounts(
        long Works,
        long Books,
        long Collections,
        long Memberships,
        long Notes,
        long Topics,
        long NoteTopics,
        long Writings,
        long WritingNotes,
        long Acquisitions,
        long ImportLinks,
        long AssistantSettings);

    private sealed record CandidateDatabaseComparison(
        FailureList Failures,
        CandidateRowCounts Counts,
        long RowsVerified);

    private sealed record WorkContext(WorkModel Row, PortableWork Expected);

    private sealed record BookContext(
        BookModel Row,
        PortableBook Expected,
        IReadOnlyDictionary<(Guid BookId, string Kind), PortableArchiveMediaEntry> Media);

    private sealed record BookMetadataContext(Guid BookId, BookMetadata Row, PortableBookMetadata Expected);

    private sealed record BookProgressContext(Guid BookId, ReadingProgress Row, PortableReadingProgress Expected);

    private sealed record CollectionContext(CollectionModel Row, PortableCollection Expected);

    private sealed record NoteContext(NoteModel Row, PortableNote Expected);

    private sealed record TopicContext(TopicModel Row, PortableTopic Expected);

    private sealed record WritingContext(WritingModel Row, PortableWriting Expected);

    private sealed record AcquisitionContext(BookAcquisitionModel Row, PortableBookAcquisition Expected);

    private sealed record ImportLinkContext(NoteImportBookLink Row, PortableNoteImportBookLink Expected);

    private sealed record AssistantContext(AssistantSettingsModel Row, PortableAssistantSettings Expected);

    private sealed record MembershipContext(
        IReadOnlyList<BookCollectionModel> Rows,
        IReadOnlyList<PortableBookCollection> Expected);

    private sealed record NoteTopicContext(
        IReadOnlyList<NoteTopicModel> Rows,
        IReadOnlyList<PortableNoteTopic> Expected);

    private sealed record WritingNoteContext(
        IReadOnlyList<WritingNoteModel> Rows,
        IReadOnlyList<PortableWritingNote> Expected);

    private static IReadOnlyList<EntityField> NoFields => [];

    private static IReadOnlyList<EntityField> Ef(params (string Entity, string Property)[] fields) =>
        fields.Select(field => new EntityField(field.Entity, field.Property)).ToArray();

    private static ComparisonEntry<TContext> Entry<TContext>(
        string archiveProperty,
        IReadOnlyList<EntityField> candidateFields,
        Action<TContext, FailureList> compare) =>
        new(archiveProperty, candidateFields, compare);

    private static readonly IReadOnlyList<ComparisonEntry<WorkContext>> WorkComparisons =
    [
        Entry<WorkContext>(
            nameof(PortableWork.Id),
            Ef(("WorkModel", nameof(WorkModel.Id))),
            (context, failures) => CompareField(failures, "work", context.Row.Id, nameof(PortableWork.Id), context.Row.Id, context.Expected.Id)),
        Entry<WorkContext>(
            nameof(PortableWork.Title),
            Ef(("WorkModel", nameof(WorkModel.Title))),
            (context, failures) => CompareField(failures, "work", context.Row.Id, nameof(PortableWork.Title), context.Row.Title, context.Expected.Title)),
        Entry<WorkContext>(
            nameof(PortableWork.Author),
            Ef(("WorkModel", nameof(WorkModel.Author))),
            (context, failures) => CompareField(failures, "work", context.Row.Id, nameof(PortableWork.Author), context.Row.Author, context.Expected.Author)),
        Entry<WorkContext>(
            nameof(PortableWork.CreatedAt),
            Ef(("WorkModel", nameof(WorkModel.CreatedAt))),
            (context, failures) => CompareField(failures, "work", context.Row.Id, nameof(PortableWork.CreatedAt), context.Row.CreatedAt, context.Expected.CreatedAt)),
    ];

    private static readonly IReadOnlyList<ComparisonEntry<BookContext>> BookComparisons =
    [
        Entry<BookContext>(
            nameof(PortableBook.Id),
            Ef(("BookModel", nameof(BookModel.Id))),
            (context, failures) => CompareField(failures, "book", context.Row.Id, nameof(PortableBook.Id), context.Row.Id, context.Expected.Id)),
        Entry<BookContext>(
            nameof(PortableBook.WorkId),
            Ef(("BookModel", nameof(BookModel.WorkId))),
            (context, failures) => CompareField(failures, "book", context.Row.Id, nameof(PortableBook.WorkId), context.Row.WorkId, context.Expected.WorkId)),
        Entry<BookContext>(
            nameof(PortableBook.Type),
            NoFields,
            (context, failures) => CompareField(failures, "book", context.Row.Id, "Type", ConcreteType(context.Row), context.Expected.Type, ignoreCase: true)),
        Entry<BookContext>(
            nameof(PortableBook.Status),
            Ef(("BookModel", nameof(BookModel.Status))),
            (context, failures) => CompareField(failures, "book", context.Row.Id, nameof(PortableBook.Status), context.Row.Status.ToString(), context.Expected.Status, ignoreCase: true)),
        Entry<BookContext>(
            nameof(PortableBook.StatusMessage),
            Ef(("BookModel", nameof(BookModel.StatusMessage))),
            (context, failures) => CompareField(failures, "book", context.Row.Id, nameof(PortableBook.StatusMessage), context.Row.StatusMessage, context.Expected.StatusMessage)),
        Entry<BookContext>(
            nameof(PortableBook.Title),
            Ef(("BookModel", nameof(BookModel.Title))),
            (context, failures) => CompareField(failures, "book", context.Row.Id, nameof(PortableBook.Title), context.Row.Title, context.Expected.Title)),
        Entry<BookContext>(
            nameof(PortableBook.Author),
            Ef(("BookModel", nameof(BookModel.Author))),
            (context, failures) => CompareField(failures, "book", context.Row.Id, nameof(PortableBook.Author), context.Row.Author, context.Expected.Author)),
        Entry<BookContext>(
            nameof(PortableBook.Metadata),
            Ef(
                ("BookMetadata", nameof(BookMetadata.Subtitle)),
                ("BookMetadata", nameof(BookMetadata.Description)),
                ("BookMetadata", nameof(BookMetadata.Editor)),
                ("BookMetadata", nameof(BookMetadata.Translator)),
                ("BookMetadata", nameof(BookMetadata.Publisher)),
                ("BookMetadata", nameof(BookMetadata.PlaceOfPublication)),
                ("BookMetadata", nameof(BookMetadata.PublishedDate)),
                ("BookMetadata", nameof(BookMetadata.Language)),
                ("BookMetadata", nameof(BookMetadata.Categories)),
                ("BookMetadata", nameof(BookMetadata.Edition)),
                ("BookMetadata", nameof(BookMetadata.Series)),
                ("BookMetadata", nameof(BookMetadata.VolumeNumber))),
            (context, failures) => Run(
                BookMetadataComparisons,
                new BookMetadataContext(context.Row.Id, context.Row.Metadata, context.Expected.Metadata),
                failures)),
        Entry<BookContext>(
            nameof(PortableBook.Progress),
            Ef(
                ("ReadingProgress", nameof(ReadingProgress.LastLocation)),
                ("ReadingProgress", nameof(ReadingProgress.ProgressPercent)),
                ("ReadingProgress", nameof(ReadingProgress.Rating)),
                ("ReadingProgress", nameof(ReadingProgress.IsFavorite)),
                ("ReadingProgress", nameof(ReadingProgress.PersonalReview)),
                ("ReadingProgress", nameof(ReadingProgress.LastReadAt)),
                ("ReadingProgress", nameof(ReadingProgress.FinishedAt))),
            (context, failures) => Run(
                BookProgressComparisons,
                new BookProgressContext(context.Row.Id, context.Row.Progress, context.Expected.Progress),
                failures)),
        Entry<BookContext>(
            nameof(PortableBook.CreatedAt),
            Ef(("BookModel", nameof(BookModel.CreatedAt))),
            (context, failures) => CompareField(failures, "book", context.Row.Id, nameof(PortableBook.CreatedAt), context.Row.CreatedAt, context.Expected.CreatedAt)),
        Entry<BookContext>(
            nameof(PortableBook.Isbn),
            Ef(("PhysicalBookModel", nameof(PhysicalBookModel.Isbn)), ("EBookModel", nameof(EBookModel.Isbn))),
            (context, failures) =>
            {
                var isbn = context.Row switch
                {
                    PhysicalBookModel physical => physical.Isbn,
                    EBookModel ebook => ebook.Isbn,
                    _ => null,
                };
                CompareField(failures, "book", context.Row.Id, nameof(PhysicalBookModel.Isbn), isbn, context.Expected.Isbn);
            }),
        Entry<BookContext>(
            nameof(PortableBook.PageCount),
            Ef(("PhysicalBookModel", nameof(PhysicalBookModel.PageCount)), ("EBookModel", nameof(EBookModel.PageCount))),
            (context, failures) =>
            {
                var pageCount = context.Row switch
                {
                    PhysicalBookModel physical => physical.PageCount,
                    EBookModel ebook => ebook.PageCount,
                    _ => null,
                };
                CompareField(failures, "book", context.Row.Id, nameof(PhysicalBookModel.PageCount), pageCount, context.Expected.PageCount);
            }),
        Entry<BookContext>(
            nameof(PortableBook.Asin),
            Ef(("AudioBookModel", nameof(AudioBookModel.Asin))),
            (context, failures) => CompareField(failures, "book", context.Row.Id, nameof(AudioBookModel.Asin), (context.Row as AudioBookModel)?.Asin, context.Expected.Asin)),
        Entry<BookContext>(
            nameof(PortableBook.Duration),
            Ef(("AudioBookModel", nameof(AudioBookModel.Duration))),
            (context, failures) => CompareField(failures, "book", context.Row.Id, nameof(AudioBookModel.Duration), (context.Row as AudioBookModel)?.Duration, context.Expected.Duration)),
        Entry<BookContext>(
            nameof(PortableBook.Narrator),
            Ef(("AudioBookModel", nameof(AudioBookModel.Narrator))),
            (context, failures) => CompareField(failures, "book", context.Row.Id, nameof(AudioBookModel.Narrator), (context.Row as AudioBookModel)?.Narrator, context.Expected.Narrator)),
        Entry<BookContext>(
            nameof(PortableBook.ChaptersJson),
            Ef(("FileInfoDetails", nameof(FileInfoDetails.ChaptersJson))),
            (context, failures) => CompareField(failures, "book", context.Row.Id, nameof(FileInfoDetails.ChaptersJson), context.Row.FileDetails.ChaptersJson, context.Expected.ChaptersJson)),
        Entry<BookContext>(
            nameof(PortableBook.TracksJson),
            Ef(("FileInfoDetails", nameof(FileInfoDetails.TracksJson))),
            (context, failures) => CompareField(
                failures,
                "book",
                context.Row.Id,
                nameof(FileInfoDetails.TracksJson),
                context.Row.FileDetails.TracksJson,
                string.IsNullOrWhiteSpace(context.Expected.TracksJson) ? null : context.Expected.TracksJson)),
        Entry<BookContext>(
            nameof(PortableBook.HasBookFile),
            Ef(("FileInfoDetails", nameof(FileInfoDetails.HasFile)), ("FileInfoDetails", nameof(FileInfoDetails.FileName))),
            (context, failures) =>
            {
                CompareField(
                    failures,
                    "book",
                    context.Row.Id,
                    nameof(FileInfoDetails.HasFile),
                    context.Row.FileDetails.HasFile,
                    // A multi-track audiobook has a file to play without a
                    // primary book file.
                    context.Expected.HasBookFile || !string.IsNullOrWhiteSpace(context.Expected.TracksJson));
                CompareField(
                    failures,
                    "book",
                    context.Row.Id,
                    nameof(FileInfoDetails.FileName),
                    context.Row.FileDetails.FileName,
                    ResolveMediaFileName(context.Media, context.Row.Id, PortableArchiveFormat.BookMediaKind));
            }),
        Entry<BookContext>(
            nameof(PortableBook.HasCover),
            Ef(("FileInfoDetails", nameof(FileInfoDetails.CoverFileName))),
            (context, failures) =>
            {
                var expectedName = ResolveMediaFileName(context.Media, context.Row.Id, PortableArchiveFormat.CoverMediaKind);
                CompareField(
                    failures,
                    "book",
                    context.Row.Id,
                    "HasCover",
                    context.Row.FileDetails.CoverFileName is not null,
                    context.Expected.HasCover);
                CompareField(
                    failures,
                    "book",
                    context.Row.Id,
                    nameof(FileInfoDetails.CoverFileName),
                    context.Row.FileDetails.CoverFileName,
                    expectedName);
            }),
    ];

    private static readonly IReadOnlyList<ComparisonEntry<BookMetadataContext>> BookMetadataComparisons =
    [
        Entry<BookMetadataContext>(
            nameof(PortableBookMetadata.Subtitle),
            Ef(("BookMetadata", nameof(BookMetadata.Subtitle))),
            (context, failures) => CompareField(failures, "book", context.BookId, nameof(BookMetadata.Subtitle), context.Row.Subtitle, context.Expected.Subtitle)),
        Entry<BookMetadataContext>(
            nameof(PortableBookMetadata.Description),
            Ef(("BookMetadata", nameof(BookMetadata.Description))),
            (context, failures) => CompareField(failures, "book", context.BookId, nameof(BookMetadata.Description), context.Row.Description, context.Expected.Description)),
        Entry<BookMetadataContext>(
            nameof(PortableBookMetadata.Editor),
            Ef(("BookMetadata", nameof(BookMetadata.Editor))),
            (context, failures) => CompareField(failures, "book", context.BookId, nameof(BookMetadata.Editor), context.Row.Editor, context.Expected.Editor)),
        Entry<BookMetadataContext>(
            nameof(PortableBookMetadata.Translator),
            Ef(("BookMetadata", nameof(BookMetadata.Translator))),
            (context, failures) => CompareField(failures, "book", context.BookId, nameof(BookMetadata.Translator), context.Row.Translator, context.Expected.Translator)),
        Entry<BookMetadataContext>(
            nameof(PortableBookMetadata.Publisher),
            Ef(("BookMetadata", nameof(BookMetadata.Publisher))),
            (context, failures) => CompareField(failures, "book", context.BookId, nameof(BookMetadata.Publisher), context.Row.Publisher, context.Expected.Publisher)),
        Entry<BookMetadataContext>(
            nameof(PortableBookMetadata.PlaceOfPublication),
            Ef(("BookMetadata", nameof(BookMetadata.PlaceOfPublication))),
            (context, failures) => CompareField(failures, "book", context.BookId, nameof(BookMetadata.PlaceOfPublication), context.Row.PlaceOfPublication, context.Expected.PlaceOfPublication)),
        Entry<BookMetadataContext>(
            nameof(PortableBookMetadata.PublishedDate),
            Ef(("BookMetadata", nameof(BookMetadata.PublishedDate))),
            (context, failures) => CompareField(failures, "book", context.BookId, nameof(BookMetadata.PublishedDate), context.Row.PublishedDate, context.Expected.PublishedDate)),
        Entry<BookMetadataContext>(
            nameof(PortableBookMetadata.Language),
            Ef(("BookMetadata", nameof(BookMetadata.Language))),
            (context, failures) => CompareField(failures, "book", context.BookId, nameof(BookMetadata.Language), context.Row.Language, context.Expected.Language)),
        Entry<BookMetadataContext>(
            nameof(PortableBookMetadata.Categories),
            Ef(("BookMetadata", nameof(BookMetadata.Categories))),
            (context, failures) => CompareField(failures, "book", context.BookId, nameof(BookMetadata.Categories), context.Row.Categories, context.Expected.Categories)),
        Entry<BookMetadataContext>(
            nameof(PortableBookMetadata.Edition),
            Ef(("BookMetadata", nameof(BookMetadata.Edition))),
            (context, failures) => CompareField(failures, "book", context.BookId, nameof(BookMetadata.Edition), context.Row.Edition, context.Expected.Edition)),
        Entry<BookMetadataContext>(
            nameof(PortableBookMetadata.Series),
            Ef(("BookMetadata", nameof(BookMetadata.Series))),
            (context, failures) => CompareField(failures, "book", context.BookId, nameof(BookMetadata.Series), context.Row.Series, context.Expected.Series)),
        Entry<BookMetadataContext>(
            nameof(PortableBookMetadata.VolumeNumber),
            Ef(("BookMetadata", nameof(BookMetadata.VolumeNumber))),
            (context, failures) => CompareField(failures, "book", context.BookId, nameof(BookMetadata.VolumeNumber), context.Row.VolumeNumber, context.Expected.VolumeNumber)),
    ];

    private static readonly IReadOnlyList<ComparisonEntry<BookProgressContext>> BookProgressComparisons =
    [
        Entry<BookProgressContext>(
            nameof(PortableReadingProgress.LastLocation),
            Ef(("ReadingProgress", nameof(ReadingProgress.LastLocation))),
            (context, failures) => CompareField(failures, "book", context.BookId, nameof(ReadingProgress.LastLocation), context.Row.LastLocation, context.Expected.LastLocation)),
        Entry<BookProgressContext>(
            nameof(PortableReadingProgress.ProgressPercent),
            Ef(("ReadingProgress", nameof(ReadingProgress.ProgressPercent))),
            (context, failures) => CompareField(failures, "book", context.BookId, nameof(ReadingProgress.ProgressPercent), context.Row.ProgressPercent, context.Expected.ProgressPercent)),
        Entry<BookProgressContext>(
            nameof(PortableReadingProgress.Rating),
            Ef(("ReadingProgress", nameof(ReadingProgress.Rating))),
            (context, failures) => CompareField(failures, "book", context.BookId, nameof(ReadingProgress.Rating), context.Row.Rating, context.Expected.Rating)),
        Entry<BookProgressContext>(
            nameof(PortableReadingProgress.IsFavorite),
            Ef(("ReadingProgress", nameof(ReadingProgress.IsFavorite))),
            (context, failures) => CompareField(failures, "book", context.BookId, nameof(ReadingProgress.IsFavorite), context.Row.IsFavorite, context.Expected.IsFavorite)),
        Entry<BookProgressContext>(
            nameof(PortableReadingProgress.PersonalReview),
            Ef(("ReadingProgress", nameof(ReadingProgress.PersonalReview))),
            (context, failures) => CompareField(failures, "book", context.BookId, nameof(ReadingProgress.PersonalReview), context.Row.PersonalReview, context.Expected.PersonalReview)),
        Entry<BookProgressContext>(
            nameof(PortableReadingProgress.LastReadAt),
            Ef(("ReadingProgress", nameof(ReadingProgress.LastReadAt))),
            (context, failures) => CompareField(failures, "book", context.BookId, nameof(ReadingProgress.LastReadAt), context.Row.LastReadAt, context.Expected.LastReadAt)),
        Entry<BookProgressContext>(
            nameof(PortableReadingProgress.FinishedAt),
            Ef(("ReadingProgress", nameof(ReadingProgress.FinishedAt))),
            (context, failures) => CompareField(failures, "book", context.BookId, nameof(ReadingProgress.FinishedAt), context.Row.FinishedAt, context.Expected.FinishedAt)),
    ];

    private static readonly IReadOnlyList<ComparisonEntry<CollectionContext>> CollectionComparisons =
    [
        Entry<CollectionContext>(
            nameof(PortableCollection.Id),
            Ef(("CollectionModel", nameof(CollectionModel.Id))),
            (context, failures) => CompareField(failures, "collection", context.Row.Id, nameof(PortableCollection.Id), context.Row.Id, context.Expected.Id)),
        Entry<CollectionContext>(
            nameof(PortableCollection.Name),
            Ef(("CollectionModel", nameof(CollectionModel.Name))),
            (context, failures) => CompareField(failures, "collection", context.Row.Id, nameof(PortableCollection.Name), context.Row.Name, context.Expected.Name)),
        Entry<CollectionContext>(
            nameof(PortableCollection.ParentId),
            Ef(("CollectionModel", nameof(CollectionModel.ParentId))),
            (context, failures) => CompareField(failures, "collection", context.Row.Id, nameof(PortableCollection.ParentId), context.Row.ParentId, context.Expected.ParentId)),
    ];

    private static readonly IReadOnlyList<ComparisonEntry<NoteContext>> NoteComparisons =
    [
        Entry<NoteContext>(
            nameof(PortableNote.Id),
            Ef(("NoteModel", nameof(NoteModel.Id))),
            (context, failures) => CompareField(failures, "note", context.Row.Id, nameof(PortableNote.Id), context.Row.Id, context.Expected.Id)),
        Entry<NoteContext>(
            nameof(PortableNote.BookId),
            Ef(("NoteModel", nameof(NoteModel.BookId))),
            (context, failures) => CompareField(failures, "note", context.Row.Id, nameof(PortableNote.BookId), context.Row.BookId, context.Expected.BookId)),
        Entry<NoteContext>(
            nameof(PortableNote.Content),
            Ef(("NoteModel", nameof(NoteModel.Content))),
            (context, failures) => CompareField(failures, "note", context.Row.Id, nameof(PortableNote.Content), context.Row.Content, context.Expected.Content)),
        Entry<NoteContext>(
            nameof(PortableNote.CfiRange),
            Ef(("NoteModel", nameof(NoteModel.CfiRange))),
            (context, failures) => CompareField(failures, "note", context.Row.Id, nameof(PortableNote.CfiRange), context.Row.CfiRange, context.Expected.CfiRange)),
        Entry<NoteContext>(
            nameof(PortableNote.SelectedText),
            Ef(("NoteModel", nameof(NoteModel.SelectedText))),
            (context, failures) => CompareField(failures, "note", context.Row.Id, nameof(PortableNote.SelectedText), context.Row.SelectedText, context.Expected.SelectedText)),
        Entry<NoteContext>(
            nameof(PortableNote.CreatedAt),
            Ef(("NoteModel", nameof(NoteModel.CreatedAt))),
            (context, failures) => CompareField(failures, "note", context.Row.Id, nameof(PortableNote.CreatedAt), context.Row.CreatedAt, context.Expected.CreatedAt)),
        Entry<NoteContext>(
            nameof(PortableNote.RawContent),
            Ef(("NoteModel", nameof(NoteModel.RawContent))),
            (context, failures) => CompareField(failures, "note", context.Row.Id, nameof(PortableNote.RawContent), context.Row.RawContent, context.Expected.RawContent)),
        Entry<NoteContext>(
            nameof(PortableNote.CaptureSource),
            Ef(("NoteModel", nameof(NoteModel.CaptureSource))),
            (context, failures) => CompareField(failures, "note", context.Row.Id, nameof(PortableNote.CaptureSource), context.Row.CaptureSource, context.Expected.CaptureSource)),
        Entry<NoteContext>(
            nameof(PortableNote.ProcessingMode),
            Ef(("NoteModel", nameof(NoteModel.ProcessingMode))),
            (context, failures) => CompareField(failures, "note", context.Row.Id, nameof(PortableNote.ProcessingMode), context.Row.ProcessingMode, context.Expected.ProcessingMode)),
        Entry<NoteContext>(
            nameof(PortableNote.SourceAnchorKind),
            Ef(("NoteModel", nameof(NoteModel.SourceAnchorKind))),
            (context, failures) => CompareField(failures, "note", context.Row.Id, nameof(PortableNote.SourceAnchorKind), context.Row.SourceAnchorKind, context.Expected.SourceAnchorKind)),
        Entry<NoteContext>(
            nameof(PortableNote.SourceAnchorValue),
            Ef(("NoteModel", nameof(NoteModel.SourceAnchorValue))),
            (context, failures) => CompareField(failures, "note", context.Row.Id, nameof(PortableNote.SourceAnchorValue), context.Row.SourceAnchorValue, context.Expected.SourceAnchorValue)),
        Entry<NoteContext>(
            nameof(PortableNote.AnchorVerified),
            Ef(("NoteModel", nameof(NoteModel.AnchorVerified))),
            (context, failures) => CompareField(failures, "note", context.Row.Id, nameof(PortableNote.AnchorVerified), context.Row.AnchorVerified, context.Expected.AnchorVerified)),
    ];

    private static readonly IReadOnlyList<ComparisonEntry<TopicContext>> TopicComparisons =
    [
        Entry<TopicContext>(
            nameof(PortableTopic.Id),
            Ef(("TopicModel", nameof(TopicModel.Id))),
            (context, failures) => CompareField(failures, "topic", context.Row.Id, nameof(PortableTopic.Id), context.Row.Id, context.Expected.Id)),
        Entry<TopicContext>(
            nameof(PortableTopic.Topic),
            Ef(("TopicModel", nameof(TopicModel.Topic))),
            (context, failures) => CompareField(failures, "topic", context.Row.Id, nameof(PortableTopic.Topic), context.Row.Topic, context.Expected.Topic)),
    ];

    private static readonly IReadOnlyList<ComparisonEntry<WritingContext>> WritingComparisons =
    [
        Entry<WritingContext>(
            nameof(PortableWriting.Id),
            Ef(("WritingModel", nameof(WritingModel.Id))),
            (context, failures) => CompareField(failures, "writing", context.Row.Id, nameof(PortableWriting.Id), context.Row.Id, context.Expected.Id)),
        Entry<WritingContext>(
            nameof(PortableWriting.Name),
            Ef(("WritingModel", nameof(WritingModel.Name))),
            (context, failures) => CompareField(failures, "writing", context.Row.Id, nameof(PortableWriting.Name), context.Row.Name, context.Expected.Name)),
        Entry<WritingContext>(
            nameof(PortableWriting.Type),
            Ef(("WritingModel", nameof(WritingModel.Type))),
            (context, failures) => CompareField(failures, "writing", context.Row.Id, nameof(PortableWriting.Type), context.Row.Type.ToString(), context.Expected.Type, ignoreCase: true)),
        Entry<WritingContext>(
            nameof(PortableWriting.Content),
            Ef(("WritingModel", nameof(WritingModel.Content))),
            (context, failures) => CompareField(failures, "writing", context.Row.Id, nameof(PortableWriting.Content), context.Row.Content, context.Expected.Content)),
        Entry<WritingContext>(
            nameof(PortableWriting.ParentId),
            Ef(("WritingModel", nameof(WritingModel.ParentId))),
            (context, failures) => CompareField(failures, "writing", context.Row.Id, nameof(PortableWriting.ParentId), context.Row.ParentId, context.Expected.ParentId)),
        Entry<WritingContext>(
            nameof(PortableWriting.CreatedAt),
            Ef(("WritingModel", nameof(WritingModel.CreatedAt))),
            (context, failures) => CompareField(failures, "writing", context.Row.Id, nameof(PortableWriting.CreatedAt), context.Row.CreatedAt, context.Expected.CreatedAt)),
        Entry<WritingContext>(
            nameof(PortableWriting.UpdatedAt),
            Ef(("WritingModel", nameof(WritingModel.UpdatedAt))),
            (context, failures) => CompareField(failures, "writing", context.Row.Id, nameof(PortableWriting.UpdatedAt), context.Row.UpdatedAt, context.Expected.UpdatedAt)),
    ];

    private static readonly IReadOnlyList<ComparisonEntry<AcquisitionContext>> AcquisitionComparisons =
    [
        Entry<AcquisitionContext>(
            nameof(PortableBookAcquisition.Id),
            Ef(("BookAcquisitionModel", nameof(BookAcquisitionModel.Id))),
            (context, failures) => CompareField(failures, "acquisition", context.Row.Id, nameof(PortableBookAcquisition.Id), context.Row.Id, context.Expected.Id)),
        Entry<AcquisitionContext>(
            nameof(PortableBookAcquisition.BookId),
            Ef(("BookAcquisitionModel", nameof(BookAcquisitionModel.BookId))),
            (context, failures) => CompareField(failures, "acquisition", context.Row.Id, nameof(PortableBookAcquisition.BookId), context.Row.BookId, context.Expected.BookId)),
        Entry<AcquisitionContext>(
            nameof(PortableBookAcquisition.ProviderId),
            Ef(("BookAcquisitionModel", nameof(BookAcquisitionModel.ProviderId))),
            (context, failures) => CompareField(failures, "acquisition", context.Row.Id, nameof(PortableBookAcquisition.ProviderId), context.Row.ProviderId, context.Expected.ProviderId)),
        Entry<AcquisitionContext>(
            nameof(PortableBookAcquisition.ProviderDisplayName),
            Ef(("BookAcquisitionModel", nameof(BookAcquisitionModel.ProviderDisplayName))),
            (context, failures) => CompareField(failures, "acquisition", context.Row.Id, nameof(PortableBookAcquisition.ProviderDisplayName), context.Row.ProviderDisplayName, context.Expected.ProviderDisplayName)),
        Entry<AcquisitionContext>(
            nameof(PortableBookAcquisition.ExternalId),
            Ef(("BookAcquisitionModel", nameof(BookAcquisitionModel.ExternalId))),
            (context, failures) => CompareField(failures, "acquisition", context.Row.Id, nameof(PortableBookAcquisition.ExternalId), context.Row.ExternalId, context.Expected.ExternalId)),
        Entry<AcquisitionContext>(
            nameof(PortableBookAcquisition.AssetId),
            Ef(("BookAcquisitionModel", nameof(BookAcquisitionModel.AssetId))),
            (context, failures) => CompareField(failures, "acquisition", context.Row.Id, nameof(PortableBookAcquisition.AssetId), context.Row.AssetId, context.Expected.AssetId)),
        Entry<AcquisitionContext>(
            nameof(PortableBookAcquisition.AssetFormat),
            Ef(("BookAcquisitionModel", nameof(BookAcquisitionModel.AssetFormat))),
            (context, failures) => CompareField(failures, "acquisition", context.Row.Id, nameof(PortableBookAcquisition.AssetFormat), context.Row.AssetFormat, context.Expected.AssetFormat)),
        Entry<AcquisitionContext>(
            nameof(PortableBookAcquisition.ImportedExtension),
            Ef(("BookAcquisitionModel", nameof(BookAcquisitionModel.ImportedExtension))),
            (context, failures) => CompareField(failures, "acquisition", context.Row.Id, nameof(PortableBookAcquisition.ImportedExtension), context.Row.ImportedExtension, context.Expected.ImportedExtension)),
        Entry<AcquisitionContext>(
            nameof(PortableBookAcquisition.SourceUrl),
            Ef(("BookAcquisitionModel", nameof(BookAcquisitionModel.SourceUrl))),
            (context, failures) => CompareField(failures, "acquisition", context.Row.Id, nameof(PortableBookAcquisition.SourceUrl), context.Row.SourceUrl, context.Expected.SourceUrl)),
        Entry<AcquisitionContext>(
            nameof(PortableBookAcquisition.RightsStatement),
            Ef(("BookAcquisitionModel", nameof(BookAcquisitionModel.RightsStatement))),
            (context, failures) => CompareField(failures, "acquisition", context.Row.Id, nameof(PortableBookAcquisition.RightsStatement), context.Row.RightsStatement, context.Expected.RightsStatement)),
        Entry<AcquisitionContext>(
            nameof(PortableBookAcquisition.AcquiredAt),
            Ef(("BookAcquisitionModel", nameof(BookAcquisitionModel.AcquiredAt))),
            (context, failures) => CompareField(failures, "acquisition", context.Row.Id, nameof(PortableBookAcquisition.AcquiredAt), context.Row.AcquiredAt, context.Expected.AcquiredAt)),
    ];

    private static readonly IReadOnlyList<ComparisonEntry<AssistantContext>> AssistantComparisons =
    [
        Entry<AssistantContext>(
            nameof(PortableAssistantSettings.CaptureProcessingMode),
            Ef(("AssistantSettingsModel", nameof(AssistantSettingsModel.CaptureProcessingMode))),
            (context, failures) => CompareField(failures, "assistantSettings", context.Row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), nameof(AssistantSettingsModel.CaptureProcessingMode), context.Row.CaptureProcessingMode, context.Expected.CaptureProcessingMode)),
        Entry<AssistantContext>(
            nameof(PortableAssistantSettings.UpdatedAtUtc),
            Ef(("AssistantSettingsModel", nameof(AssistantSettingsModel.UpdatedAtUtc))),
            (context, failures) => CompareField(failures, "assistantSettings", context.Row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), nameof(AssistantSettingsModel.UpdatedAtUtc), context.Row.UpdatedAtUtc, context.Expected.UpdatedAtUtc)),
    ];

    private static readonly IReadOnlyList<ComparisonEntry<ImportLinkContext>> ImportLinkComparisons =
    [
        Entry<ImportLinkContext>(
            nameof(PortableNoteImportBookLink.Id),
            Ef(("NoteImportBookLink", nameof(NoteImportBookLink.Id))),
            (context, failures) => CompareField(failures, "noteImportBookLink", context.Row.Id, nameof(PortableNoteImportBookLink.Id), context.Row.Id, context.Expected.Id)),
        Entry<ImportLinkContext>(
            nameof(PortableNoteImportBookLink.Source),
            Ef(("NoteImportBookLink", nameof(NoteImportBookLink.Source))),
            (context, failures) => CompareField(failures, "noteImportBookLink", context.Row.Id, nameof(PortableNoteImportBookLink.Source), context.Row.Source, context.Expected.Source)),
        Entry<ImportLinkContext>(
            nameof(PortableNoteImportBookLink.SourceKey),
            Ef(("NoteImportBookLink", nameof(NoteImportBookLink.SourceKey))),
            (context, failures) => CompareField(failures, "noteImportBookLink", context.Row.Id, nameof(PortableNoteImportBookLink.SourceKey), context.Row.SourceKey, context.Expected.SourceKey)),
        Entry<ImportLinkContext>(
            nameof(PortableNoteImportBookLink.BookId),
            Ef(("NoteImportBookLink", nameof(NoteImportBookLink.BookId))),
            (context, failures) => CompareField(failures, "noteImportBookLink", context.Row.Id, nameof(PortableNoteImportBookLink.BookId), context.Row.BookId, context.Expected.BookId)),
        Entry<ImportLinkContext>(
            nameof(PortableNoteImportBookLink.CreatedAtUtc),
            Ef(("NoteImportBookLink", nameof(NoteImportBookLink.CreatedAtUtc))),
            (context, failures) => CompareField(failures, "noteImportBookLink", context.Row.Id, nameof(PortableNoteImportBookLink.CreatedAtUtc), context.Row.CreatedAtUtc, context.Expected.CreatedAtUtc)),
    ];

    private static readonly IReadOnlyList<ComparisonEntry<MembershipContext>> BookCollectionComparisons =
    [
        Entry<MembershipContext>(
            nameof(PortableBookCollection.BookId),
            Ef(("BookCollectionModel", nameof(BookCollectionModel.BookId))),
            (context, failures) => CompareProjectedKeys(
                failures,
                "bookCollection",
                nameof(PortableBookCollection.BookId),
                context.Expected.Select(item => item.BookId),
                context.Rows.Select(row => row.BookId))),
        Entry<MembershipContext>(
            nameof(PortableBookCollection.CollectionId),
            Ef(("BookCollectionModel", nameof(BookCollectionModel.CollectionId))),
            (context, failures) => CompareProjectedKeys(
                failures,
                "bookCollection",
                nameof(PortableBookCollection.CollectionId),
                context.Expected.Select(item => item.CollectionId),
                context.Rows.Select(row => row.CollectionId))),
        Entry<MembershipContext>(
            nameof(PortableBookCollection.AddedAt),
            Ef(("BookCollectionModel", nameof(BookCollectionModel.AddedAt))),
            (context, failures) =>
            {
                var expected = context.Expected
                    .GroupBy(item => (item.BookId, item.CollectionId))
                    .ToDictionary(group => group.Key, group => group.First().AddedAt);
                CompareAddedAt(
                    failures,
                    "bookCollection",
                    nameof(PortableBookCollection.AddedAt),
                    expected,
                    context.Rows.Select(row => ((row.BookId, row.CollectionId), row.AddedAt)));
            }),
    ];

    private static readonly IReadOnlyList<ComparisonEntry<NoteTopicContext>> NoteTopicComparisons =
    [
        Entry<NoteTopicContext>(
            nameof(PortableNoteTopic.NoteId),
            Ef(("NoteTopicModel", nameof(NoteTopicModel.NoteId))),
            (context, failures) => CompareProjectedKeys(
                failures,
                "noteTopic",
                nameof(PortableNoteTopic.NoteId),
                context.Expected.Select(item => item.NoteId),
                context.Rows.Select(row => row.NoteId))),
        Entry<NoteTopicContext>(
            nameof(PortableNoteTopic.TopicId),
            Ef(("NoteTopicModel", nameof(NoteTopicModel.TopicId))),
            (context, failures) => CompareProjectedKeys(
                failures,
                "noteTopic",
                nameof(PortableNoteTopic.TopicId),
                context.Expected.Select(item => item.TopicId),
                context.Rows.Select(row => row.TopicId))),
    ];

    private static readonly IReadOnlyList<ComparisonEntry<WritingNoteContext>> WritingNoteComparisons =
    [
        Entry<WritingNoteContext>(
            nameof(PortableWritingNote.WritingId),
            Ef(("WritingNoteModel", nameof(WritingNoteModel.WritingId))),
            (context, failures) => CompareProjectedKeys(
                failures,
                "writingNote",
                nameof(PortableWritingNote.WritingId),
                context.Expected.Select(item => item.WritingId),
                context.Rows.Select(row => row.WritingId))),
        Entry<WritingNoteContext>(
            nameof(PortableWritingNote.NoteId),
            Ef(("WritingNoteModel", nameof(WritingNoteModel.NoteId))),
            (context, failures) => CompareProjectedKeys(
                failures,
                "writingNote",
                nameof(PortableWritingNote.NoteId),
                context.Expected.Select(item => item.NoteId),
                context.Rows.Select(row => row.NoteId))),
        Entry<WritingNoteContext>(
            nameof(PortableWritingNote.AddedAt),
            Ef(("WritingNoteModel", nameof(WritingNoteModel.AddedAt))),
            (context, failures) =>
            {
                var expected = context.Expected
                    .GroupBy(item => (item.WritingId, item.NoteId))
                    .ToDictionary(group => group.Key, group => group.First().AddedAt);
                CompareAddedAt(
                    failures,
                    "writingNote",
                    nameof(PortableWritingNote.AddedAt),
                    expected,
                    context.Rows.Select(row => ((row.WritingId, row.NoteId), row.AddedAt)));
            }),
    ];

    /// <summary>
    /// A new portable archive record or property makes the archive-coverage test
    /// fail until it is registered here (or given a reasoned exclusion), and
    /// registering it requires an executable comparison delegate.
    /// </summary>
    internal static IReadOnlyDictionary<string, RecordComparisonSpec> CandidateComparisonSpecs { get; } =
        BuildSpecs();

    private static IReadOnlyDictionary<string, RecordComparisonSpec> BuildSpecs() =>
        new Dictionary<string, RecordComparisonSpec>(StringComparer.Ordinal)
        {
            [nameof(PortableLibraryData)] = new(
                [nameof(PortableLibraryData.Version)],
                NoFields,
                LibraryDataExclusions),
            [nameof(PortableWork)] = Spec(WorkComparisons),
            [nameof(PortableBook)] = Spec(BookComparisons),
            [nameof(PortableBookMetadata)] = Spec(BookMetadataComparisons),
            [nameof(PortableReadingProgress)] = Spec(BookProgressComparisons),
            [nameof(PortableCollection)] = Spec(CollectionComparisons),
            [nameof(PortableBookCollection)] = Spec(BookCollectionComparisons),
            [nameof(PortableNote)] = Spec(NoteComparisons),
            [nameof(PortableTopic)] = Spec(TopicComparisons),
            [nameof(PortableNoteTopic)] = Spec(NoteTopicComparisons),
            [nameof(PortableWriting)] = Spec(WritingComparisons),
            [nameof(PortableWritingNote)] = Spec(WritingNoteComparisons),
            [nameof(PortableBookAcquisition)] = Spec(AcquisitionComparisons),
            [nameof(PortableAssistantSettings)] = Spec(AssistantComparisons),
            [nameof(PortableNoteImportBookLink)] = Spec(ImportLinkComparisons),
            [nameof(PortableArchiveManifest)] = new(
                [
                    nameof(PortableArchiveManifest.Format),
                    nameof(PortableArchiveManifest.FormatVersion),
                    nameof(PortableArchiveManifest.DataVersion),
                    nameof(PortableArchiveManifest.Counts),
                    nameof(PortableArchiveManifest.Data),
                    nameof(PortableArchiveManifest.Media),
                ],
                NoFields,
                ManifestExclusions),
            [nameof(PortableArchiveCounts)] = new(
                ManifestCountProperties,
                NoFields,
                EmptyExclusions),
            [nameof(PortableArchivePayload)] = new(
                [
                    nameof(PortableArchivePayload.Path),
                    nameof(PortableArchivePayload.Length),
                    nameof(PortableArchivePayload.Sha256),
                ],
                NoFields,
                EmptyExclusions),
            [nameof(PortableArchiveMediaEntry)] = new(
                [
                    nameof(PortableArchiveMediaEntry.BookId),
                    nameof(PortableArchiveMediaEntry.Kind),
                    nameof(PortableArchiveMediaEntry.Path),
                    nameof(PortableArchiveMediaEntry.FileName),
                    nameof(PortableArchiveMediaEntry.Length),
                    nameof(PortableArchiveMediaEntry.Sha256),
                ],
                NoFields,
                MediaEntryExclusions),
        };

    private static RecordComparisonSpec Spec<TContext>(
        IReadOnlyList<ComparisonEntry<TContext>> entries) =>
        new(
            entries.Select(entry => entry.ArchiveProperty).ToArray(),
            entries.SelectMany(entry => entry.CandidateFields).ToArray(),
            EmptyExclusions);
}

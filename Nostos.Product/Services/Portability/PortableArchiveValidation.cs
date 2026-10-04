using System.Security.Cryptography;
using System.Text.Json;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Library;
using Nostos.Shared.Enums;

namespace Nostos.Backend.Services.Portability;

internal static class PortableArchiveValidation
{
    internal static string MediaPath(Guid bookId, string kind, string extension) =>
        $"media/books/{bookId:N}/{kind}{extension.ToLowerInvariant()}";

    internal static void ValidateArchiveEntryCount(int count)
    {
        if (count > PortableArchiveLimits.MaxArchiveEntries)
        {
            throw new PortableArchiveException(
                "too_many_entries",
                $"Portable archive contains more than {PortableArchiveLimits.MaxArchiveEntries} entries.");
        }
    }

    internal static void ValidateDirectoryEntryName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new PortableArchiveException(
                "directory_entry_not_allowed",
                "Portable archive v1 does not allow explicit directory entries.");
        }
    }

    internal static void ValidateUniqueArchivePath(string path, bool added)
    {
        if (!added)
        {
            throw new PortableArchiveException(
                "duplicate_path",
                $"Portable archive contains duplicate path '{path}'.");
        }
    }

    internal static long ValidateDeclaredEntry(
        string path,
        long length,
        long compressedLength,
        long currentUncompressedBytes)
    {
        if (length < 0 || length > PortableArchiveLimits.MaxSingleEntryBytes)
        {
            throw new PortableArchiveException(
                "entry_too_large",
                $"Portable archive entry '{path}' exceeds the v1 entry-size limit.");
        }

        var totalUncompressed = checked(currentUncompressedBytes + length);
        if (totalUncompressed > PortableArchiveLimits.MaxUncompressedBytes)
        {
            throw new PortableArchiveException(
                "archive_expands_too_large",
                "Portable archive declares too much uncompressed data.");
        }

        if (length > 1024 * 1024)
        {
            if (compressedLength <= 0
                || length / (double)compressedLength > PortableArchiveLimits.MaxCompressionRatio)
            {
                throw new PortableArchiveException(
                    "suspicious_compression",
                    $"Portable archive entry '{path}' has a suspicious compression ratio.");
            }
        }

        return totalUncompressed;
    }

    internal static void ValidateArchiveSize(long totalBytes)
    {
        if (totalBytes > PortableArchiveLimits.MaxArchiveBytes)
        {
            throw new PortableArchiveException(
                "archive_too_large",
                $"Portable archive exceeds the {PortableArchiveLimits.MaxArchiveBytes} byte v1 compressed-size limit.");
        }
    }

    internal static void ValidateStreamedDataMatchesPreflight(
        long streamedLength,
        string streamedSha256,
        long preflightLength,
        string preflightSha256)
    {
        if (streamedLength != preflightLength
            || !FixedHashEquals(streamedSha256, preflightSha256))
        {
            throw new PortableArchiveException(
                "data_serialization_mismatch",
                "Portable relational data serialized differently between the preflight and the archive write.");
        }
    }

    internal static void ValidateManifestEntryFound(bool found)
    {
        if (!found)
        {
            throw new PortableArchiveException(
                "missing_manifest",
                "Portable archive is missing manifest.json.");
        }
    }

    internal static void ValidateDataEntryFound(string path, bool found)
    {
        if (!found)
        {
            throw new PortableArchiveException(
                "missing_data",
                $"Portable archive is missing '{path}'.");
        }
    }

    internal static void ValidateDataEntryLength(long entryLength, long manifestLength)
    {
        if (entryLength != manifestLength)
        {
            throw new PortableArchiveException(
                "data_length_mismatch",
                "Portable archive relational payload length does not match its manifest.");
        }
    }

    internal static void ValidateDataVersionAgreement(int manifestVersion, int payloadVersion)
    {
        if (manifestVersion != payloadVersion)
        {
            throw new PortableArchiveException(
                "data_version_mismatch",
                $"Portable archive manifest data version {manifestVersion} does not match payload version {payloadVersion}.");
        }
    }

    internal static void ValidateManifestCounts(
        PortableArchiveCounts actual,
        PortableArchiveCounts declared)
    {
        if (actual != declared)
        {
            throw new PortableArchiveException(
                "count_mismatch",
                "Portable archive manifest counts do not match relational data.");
        }
    }

    internal static void ValidateArchiveInventory(
        PortableArchiveManifest manifest,
        IEnumerable<string> actualPaths)
    {
        var actual = actualPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            PortableArchiveFormat.ManifestPath,
            PortableArchiveFormat.DataPath,
        };
        foreach (var media in manifest.Media)
            expectedPaths.Add(media.Path);

        var missing = expectedPaths
            .Where(path => !actual.Contains(path))
            .OrderBy(path => path)
            .FirstOrDefault();
        if (missing is not null)
        {
            throw new PortableArchiveException(
                "missing_referenced_media",
                $"Portable archive is missing referenced entry '{missing}'.");
        }

        var unexpected = actual
            .Where(path => !expectedPaths.Contains(path))
            .OrderBy(path => path)
            .FirstOrDefault();
        if (unexpected is not null)
        {
            throw new PortableArchiveException(
                "unexpected_entry",
                $"Portable archive contains unexpected entry '{unexpected}'.");
        }
    }

    internal static void ValidateMediaEntryLength(string path, long entryLength, long manifestLength)
    {
        if (entryLength != manifestLength)
        {
            throw new PortableArchiveException(
                "media_length_mismatch",
                $"Portable media '{path}' length does not match its manifest.");
        }
    }

    internal static void ValidateDataHash(string actualHash, string expectedHash)
    {
        if (!FixedHashEquals(actualHash, expectedHash))
        {
            throw new PortableArchiveException(
                "data_checksum_mismatch",
                "Portable archive relational payload failed SHA-256 verification.");
        }
    }

    internal static void ValidateMediaHash(
        string path,
        long actualLength,
        long expectedLength,
        string actualHash,
        string expectedHash)
    {
        if (actualLength != expectedLength || !FixedHashEquals(actualHash, expectedHash))
        {
            throw new PortableArchiveException(
                "media_checksum_mismatch",
                $"Portable media '{path}' failed integrity verification.");
        }
    }

    internal static void ValidateDeclaredReadSize(string path, long length, long maximum)
    {
        if (length > maximum)
        {
            throw new PortableArchiveException(
                "entry_too_large",
                $"Portable archive entry '{path}' exceeds its v1 limit.");
        }
    }

    internal static void ValidateObservedReadSize(string path, long length, long maximum)
    {
        if (length > maximum)
        {
            throw new PortableArchiveException(
                "entry_too_large",
                $"Portable archive entry '{path}' expands beyond its v1 limit.");
        }
    }

    internal static void ValidateCopiedMediaSize(long length, long maximum)
    {
        if (length > maximum)
        {
            throw new PortableArchiveException(
                "entry_too_large",
                "Portable media exceeds the v1 entry-size limit.");
        }
    }

    internal static void ValidateExpectedMediaSize(long length, long maximum)
    {
        if (length > maximum)
        {
            throw new PortableArchiveException(
                "entry_too_large",
                "Portable media exceeds the expected size.");
        }
    }

    internal static void ValidateStoredMediaLength(
        string path,
        long? actualLength,
        long expectedLength)
    {
        if (actualLength is null || actualLength.Value != expectedLength)
        {
            throw new PortableArchiveException(
                "integrity_failed",
                $"Imported media '{path}' is missing or has the wrong length.");
        }
    }

    internal static void ValidateStoredMediaCanReopen(string path, bool opened)
    {
        if (!opened)
        {
            throw new PortableArchiveException(
                "integrity_failed",
                $"Imported media '{path}' could not be reopened.");
        }
    }

    internal static void ValidateStoredMediaHash(
        string path,
        long actualLength,
        long expectedLength,
        string actualHash,
        string expectedHash)
    {
        if (actualLength != expectedLength || !FixedHashEquals(actualHash, expectedHash))
        {
            throw new PortableArchiveException(
                "integrity_failed",
                $"Imported media '{path}' failed post-write SHA-256 verification.");
        }
    }

    internal static void ValidateImportedCounts(
        PortableArchiveCounts actual,
        PortableArchiveCounts expected)
    {
        if (actual != expected)
        {
            throw new PortableArchiveException(
                "integrity_failed",
                "Imported relational entity counts do not match the archive manifest.");
        }
    }

    internal static void ValidateImportedRelationshipSet<T>(
        IEnumerable<T> expected,
        IEnumerable<T> actual,
        string message)
    {
        if (!expected.ToHashSet().SetEquals(actual))
        {
            throw new PortableArchiveException("integrity_failed", message);
        }
    }

    internal static void ValidateImportedBookType(
        Guid bookId,
        string expectedType,
        string actualType)
    {
        if (!string.Equals(actualType, expectedType, StringComparison.Ordinal))
        {
            throw new PortableArchiveException(
                "integrity_failed",
                $"Imported book {bookId} format or reading state does not match the archive.");
        }
    }

    internal static void ValidateImportedBookProgress(
        PortableBook expected,
        string? lastLocation,
        int progressPercent,
        int rating,
        bool isFavorite,
        string? personalReview)
    {
        if (lastLocation != expected.Progress.LastLocation
            || progressPercent != expected.Progress.ProgressPercent
            || rating != expected.Progress.Rating
            || isFavorite != expected.Progress.IsFavorite
            || personalReview != expected.Progress.PersonalReview)
        {
            throw new PortableArchiveException(
                "integrity_failed",
                $"Imported book {expected.Id} format or reading state does not match the archive.");
        }
    }

    internal static void ValidateImportedWritingState(
        PortableWriting expected,
        Guid? parentId,
        string name,
        string? content,
        string type)
    {
        if (parentId != expected.ParentId
            || name != expected.Name
            || content != expected.Content
            || !string.Equals(type, expected.Type, StringComparison.OrdinalIgnoreCase))
        {
            throw new PortableArchiveException(
                "integrity_failed",
                $"Imported writing {expected.Id} does not match the archive.");
        }
    }

    internal static void ValidateManifest(PortableArchiveManifest manifest)
    {
        if (!string.Equals(
            manifest.Format,
            PortableArchiveFormat.Name,
            StringComparison.Ordinal))
        {
            throw new PortableArchiveException(
                "unsupported_format",
                "Archive is not a Nostos portable archive.");
        }

        if (manifest.FormatVersion != PortableArchiveFormat.Version)
        {
            throw new PortableArchiveException(
                "unsupported_version",
                $"Portable archive format version {manifest.FormatVersion} is not supported. "
                + $"This build supports version {PortableArchiveFormat.Version}.");
        }

        if (manifest.DataVersion is not (1 or 2 or 3))
        {
            throw new PortableArchiveException(
                "unsupported_data_version",
                $"Portable archive data version {manifest.DataVersion} is not supported.");
        }

        if (manifest.Counts is null || manifest.Data is null || manifest.Media is null)
        {
            throw new PortableArchiveException(
                "malformed_manifest",
                "Portable archive manifest is incomplete.");
        }

        if (!string.Equals(
            manifest.Data.Path,
            PortableArchiveFormat.DataPath,
            StringComparison.Ordinal))
        {
            throw new PortableArchiveException(
                "invalid_data_path",
                "Portable archive relational payload path is not canonical.");
        }

        if (manifest.Data.Length < 0 || manifest.Data.Length > PortableArchiveLimits.MaxDataBytes)
        {
            throw new PortableArchiveException(
                "data_too_large",
                "Portable archive relational payload exceeds the v1 limit.");
        }

        RequireSha256(manifest.Data.Sha256, manifest.Data.Path);
    }

    internal static void ValidateMediaManifest(
        PortableArchiveManifest manifest,
        PortableLibraryData data)
    {
        var bookIds = data.Books.Select(x => x.Id).ToHashSet();
        var mediaKeys = new HashSet<(Guid BookId, string Kind)>();
        var mediaPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var media in manifest.Media)
        {
            if (!bookIds.Contains(media.BookId))
            {
                throw new PortableArchiveException(
                    "media_unknown_book",
                    $"Portable media '{media.Path}' references an unknown book.");
            }

            if (media.Kind is not (
                PortableArchiveFormat.BookMediaKind
                or PortableArchiveFormat.CoverMediaKind))
            {
                throw new PortableArchiveException(
                    "invalid_media_kind",
                    $"Portable media '{media.Path}' has an unsupported kind.");
            }

            if (!mediaKeys.Add((media.BookId, media.Kind)))
            {
                throw new PortableArchiveException(
                    "duplicate_media",
                    $"Book {media.BookId} has duplicate '{media.Kind}' media.");
            }

            if (!mediaPaths.Add(media.Path))
            {
                throw new PortableArchiveException(
                    "duplicate_path",
                    $"Portable archive contains duplicate media path '{media.Path}'.");
            }

            if (Path.GetFileName(media.FileName) != media.FileName
                || media.FileName.Contains('\\')
                || media.FileName.Contains('/'))
            {
                throw new PortableArchiveException(
                    "invalid_media_filename",
                    $"Portable media '{media.Path}' has an unsafe filename.");
            }

            var extension = Path.GetExtension(media.FileName).ToLowerInvariant();
            var canonicalFileName = $"{media.Kind}{extension}";
            if (!string.Equals(
                media.FileName,
                canonicalFileName,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new PortableArchiveException(
                    "invalid_media_filename",
                    $"Portable media '{media.Path}' filename is not canonical.");
            }

            try
            {
                if (media.Kind == PortableArchiveFormat.BookMediaKind)
                    BookAssetFormats.RequireBookExtension(media.FileName);
                else
                    BookAssetFormats.RequireCoverExtension(media.FileName);
            }
            catch (InvalidOperationException exception)
            {
                throw new PortableArchiveException(
                    "invalid_media_filename",
                    $"Portable media '{media.Path}' uses an unsupported extension.",
                    exception);
            }

            var expectedPath = MediaPath(media.BookId, media.Kind, extension);
            if (!string.Equals(
                media.Path,
                expectedPath,
                StringComparison.Ordinal))
            {
                throw new PortableArchiveException(
                    "invalid_media_path",
                    $"Portable media path '{media.Path}' is not canonical.");
            }

            if (media.Length < 0 || media.Length > PortableArchiveLimits.MaxSingleEntryBytes)
            {
                throw new PortableArchiveException(
                    "entry_too_large",
                    $"Portable media '{media.Path}' exceeds the v1 entry-size limit.");
            }

            RequireSha256(media.Sha256, media.Path);
        }

        foreach (var book in data.Books)
        {
            var hasBook = mediaKeys.Contains((
                book.Id,
                PortableArchiveFormat.BookMediaKind));
            var hasCover = mediaKeys.Contains((
                book.Id,
                PortableArchiveFormat.CoverMediaKind));

            if (book.HasBookFile != hasBook)
            {
                throw new PortableArchiveException(
                    "missing_referenced_media",
                    $"Book {book.Id} book-file state does not match the archive media manifest.");
            }

            if (book.HasCover != hasCover)
            {
                throw new PortableArchiveException(
                    "missing_referenced_media",
                    $"Book {book.Id} cover state does not match the archive media manifest.");
            }
        }
    }

    internal static void ValidatePortableData(PortableLibraryData data)
    {
        if (data.Version is not (1 or 2 or 3))
        {
            throw new PortableArchiveException(
                "unsupported_data_version",
                $"Portable relational data version {data.Version} is not supported.");
        }

        if (data.Works is null
            || data.Books is null
            || data.Collections is null
            || data.BookCollections is null
            || data.Notes is null
            || data.Topics is null
            || data.NoteTopics is null
            || data.Writings is null
            || data.BookAcquisitions is null
            || (data.Version >= 2 && data.WritingNotes is null)
            || (data.Version >= 3 && data.NoteImportBookLinks is null))
        {
            throw new PortableArchiveException(
                "malformed_data",
                "Portable relational data is incomplete.");
        }

        if (data.Version < 2 && data.WritingNotes is not null)
        {
            throw new PortableArchiveException(
                "unexpected_version_data",
                $"Portable relational data version {data.Version} must not carry writing notes.");
        }

        if (data.Version < 3 && data.NoteImportBookLinks is not null)
        {
            throw new PortableArchiveException(
                "unexpected_version_data",
                $"Portable relational data version {data.Version} must not carry note import book links.");
        }

        RequireUniqueGuids(data.Works.Select(x => x.Id), "work");
        RequireUniqueGuids(data.Books.Select(x => x.Id), "book");
        RequireUniqueGuids(data.Collections.Select(x => x.Id), "collection");
        RequireUniqueGuids(data.Notes.Select(x => x.Id), "note");
        RequireUniqueGuids(data.Topics.Select(x => x.Id), "topic");
        RequireUniqueGuids(data.Writings.Select(x => x.Id), "writing");
        RequireUniqueGuids(data.BookAcquisitions.Select(x => x.Id), "book acquisition");
        if (data.NoteImportBookLinks is not null)
        {
            RequireUniqueGuids(data.NoteImportBookLinks.Select(x => x.Id), "note import book link");
        }

        var workIds = data.Works.Select(x => x.Id).ToHashSet();
        var bookIds = data.Books.Select(x => x.Id).ToHashSet();
        var collectionIds = data.Collections.Select(x => x.Id).ToHashSet();
        var noteIds = data.Notes.Select(x => x.Id).ToHashSet();
        var topicIds = data.Topics.Select(x => x.Id).ToHashSet();
        var writingIds = data.Writings.Select(x => x.Id).ToHashSet();

        var normalizedIsbns = new HashSet<string>(StringComparer.Ordinal);
        var normalizedAsins = new HashSet<string>(StringComparer.Ordinal);

        foreach (var book in data.Books)
        {
            if (!workIds.Contains(book.WorkId))
            {
                throw new PortableArchiveException(
                    "malformed_relationship",
                    $"Book {book.Id} references unknown work {book.WorkId}.");
            }

            if (book.Type is not ("physical" or "ebook" or "audiobook"))
            {
                throw new PortableArchiveException(
                    "unsupported_book_type",
                    $"Book {book.Id} has unsupported type '{book.Type}'.");
            }

            if (!Enum.TryParse<BookStatus>(book.Status, ignoreCase: true, out _))
            {
                throw new PortableArchiveException(
                    "invalid_book_status",
                    $"Book {book.Id} has invalid status '{book.Status}'.");
            }

            if (book.Type is "physical" or "ebook")
            {
                var normalized = BookIdentityNormalizer.NormalizeIsbn(book.Isbn);
                if (normalized is not null && !normalizedIsbns.Add(normalized))
                {
                    throw new PortableArchiveException(
                        "duplicate_book_identity",
                        $"Portable archive contains duplicate normalized ISBN '{normalized}'.");
                }
            }

            if (book.Type == "audiobook")
            {
                var normalized = BookIdentityNormalizer.NormalizeAsin(book.Asin);
                if (normalized is not null && !normalizedAsins.Add(normalized))
                {
                    throw new PortableArchiveException(
                        "duplicate_book_identity",
                        $"Portable archive contains duplicate normalized ASIN '{normalized}'.");
                }
            }
        }

        var bookCollectionKeys = new HashSet<(Guid BookId, Guid CollectionId)>();
        foreach (var membership in data.BookCollections)
        {
            if (!bookIds.Contains(membership.BookId)
                || !collectionIds.Contains(membership.CollectionId))
            {
                throw new PortableArchiveException(
                    "malformed_relationship",
                    "Portable archive contains a collection membership with a missing endpoint.");
            }

            if (!bookCollectionKeys.Add((
                membership.BookId,
                membership.CollectionId)))
            {
                throw new PortableArchiveException(
                    "duplicate_relationship",
                    "Portable archive contains a duplicate book/collection membership.");
            }
        }

        foreach (var collection in data.Collections)
        {
            if (collection.ParentId is { } parentId)
            {
                if (!collectionIds.Contains(parentId) || parentId == collection.Id)
                {
                    throw new PortableArchiveException(
                        "malformed_relationship",
                        $"Collection {collection.Id} has an invalid parent.");
                }
            }
        }
        RequireAcyclic(
            data.Collections.ToDictionary(x => x.Id, x => x.ParentId),
            "collection");

        foreach (var note in data.Notes)
        {
            if (!bookIds.Contains(note.BookId))
            {
                throw new PortableArchiveException(
                    "malformed_relationship",
                    $"Note {note.Id} references unknown book {note.BookId}.");
            }
        }

        var topicNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var topic in data.Topics)
        {
            if (!topicNames.Add(topic.Topic))
            {
                throw new PortableArchiveException(
                    "duplicate_topic",
                    $"Portable archive contains duplicate topic '{topic.Topic}'.");
            }
        }

        var noteTopicKeys = new HashSet<(Guid NoteId, Guid TopicId)>();
        foreach (var link in data.NoteTopics)
        {
            if (!noteIds.Contains(link.NoteId) || !topicIds.Contains(link.TopicId))
            {
                throw new PortableArchiveException(
                    "malformed_relationship",
                    "Portable archive contains a note/topic link with a missing endpoint.");
            }

            if (!noteTopicKeys.Add((link.NoteId, link.TopicId)))
            {
                throw new PortableArchiveException(
                    "duplicate_relationship",
                    "Portable archive contains a duplicate note/topic link.");
            }
        }

        var writingNoteKeys = new HashSet<(Guid WritingId, Guid NoteId)>();
        foreach (var link in data.WritingNotes ?? [])
        {
            if (!writingIds.Contains(link.WritingId) || !noteIds.Contains(link.NoteId))
            {
                throw new PortableArchiveException(
                    "malformed_relationship",
                    "Portable archive contains a writing/note link with a missing endpoint.");
            }

            if (!writingNoteKeys.Add((link.WritingId, link.NoteId)))
            {
                throw new PortableArchiveException(
                    "duplicate_relationship",
                    "Portable archive contains a duplicate writing/note link.");
            }
        }

        foreach (var writing in data.Writings)
        {
            if (!Enum.TryParse<WritingType>(writing.Type, ignoreCase: true, out _))
            {
                throw new PortableArchiveException(
                    "invalid_writing_type",
                    $"Writing {writing.Id} has invalid type '{writing.Type}'.");
            }

            if (writing.ParentId is { } parentId)
            {
                if (!writingIds.Contains(parentId) || parentId == writing.Id)
                {
                    throw new PortableArchiveException(
                        "malformed_relationship",
                        $"Writing {writing.Id} has an invalid parent.");
                }
            }
        }
        RequireAcyclic(
            data.Writings.ToDictionary(x => x.Id, x => x.ParentId),
            "writing");

        var acquisitionBooks = new HashSet<Guid>();
        var acquisitionKeys = new HashSet<(string Provider, string External, string Asset)>();
        foreach (var acquisition in data.BookAcquisitions)
        {
            if (!bookIds.Contains(acquisition.BookId))
            {
                throw new PortableArchiveException(
                    "malformed_relationship",
                    $"Acquisition {acquisition.Id} references an unknown book.");
            }

            if (!acquisitionBooks.Add(acquisition.BookId))
            {
                throw new PortableArchiveException(
                    "duplicate_relationship",
                    $"Book {acquisition.BookId} has multiple acquisition records.");
            }

            if (!acquisitionKeys.Add((
                acquisition.ProviderId,
                acquisition.ExternalId,
                acquisition.AssetId)))
            {
                throw new PortableArchiveException(
                    "duplicate_acquisition",
                    "Portable archive contains duplicate acquisition provenance.");
            }
        }

        if (data.NoteImportBookLinks is not null)
        {
            var importLinkKeys = new HashSet<(string Source, string SourceKey)>();
            foreach (var link in data.NoteImportBookLinks)
            {
                if (!bookIds.Contains(link.BookId))
                {
                    throw new PortableArchiveException(
                        "malformed_relationship",
                        $"Note import book link {link.Id} references an unknown book.");
                }

                if (!importLinkKeys.Add((link.Source, link.SourceKey)))
                {
                    throw new PortableArchiveException(
                        "duplicate_relationship",
                        $"Portable archive contains duplicate note import book link for source '{link.Source}' and key '{link.SourceKey}'.");
                }
            }
        }
    }

    internal static string ValidateArchivePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.StartsWith("/", StringComparison.Ordinal)
            || path.StartsWith('\\')
            || path.Contains('\\')
            || path.Contains('\0')
            || path.Contains(':'))
        {
            throw new PortableArchiveException(
                "unsafe_archive_path",
                $"Portable archive contains unsafe path '{path}'.");
        }

        var segments = path.Split('/');
        if (segments.Any(segment =>
            segment.Length == 0
            || segment == "."
            || segment == ".."
            || segment.Length > 255))
        {
            throw new PortableArchiveException(
                "unsafe_archive_path",
                $"Portable archive contains unsafe path '{path}'.");
        }

        return string.Join('/', segments);
    }

    internal static void RequireUniqueGuids(
        IEnumerable<Guid> ids,
        string entityName)
    {
        var seen = new HashSet<Guid>();
        foreach (var id in ids)
        {
            if (id == Guid.Empty || !seen.Add(id))
            {
                throw new PortableArchiveException(
                    "duplicate_id",
                    $"Portable archive contains an empty or duplicate {entityName} ID.");
            }
        }
    }

    internal static void RequireAcyclic(
        IReadOnlyDictionary<Guid, Guid?> parents,
        string entityName)
    {
        var complete = new HashSet<Guid>();

        foreach (var start in parents.Keys)
        {
            if (complete.Contains(start))
                continue;

            var current = start;
            var chain = new HashSet<Guid>();
            while (parents.TryGetValue(current, out var parent) && parent is { } parentId)
            {
                if (!chain.Add(current))
                {
                    throw new PortableArchiveException(
                        "malformed_relationship",
                        $"Portable archive contains a cyclic {entityName} hierarchy.");
                }

                current = parentId;
            }

            foreach (var visited in chain)
                complete.Add(visited);
        }
    }

    internal static void RequireSameIds(
        IEnumerable<Guid> expected,
        IEnumerable<Guid> actual,
        string entityName)
    {
        if (!expected.ToHashSet().SetEquals(actual))
        {
            throw new PortableArchiveException(
                "integrity_failed",
                $"Imported {entityName} IDs do not match the archive.");
        }
    }

    internal static void RequireSha256(string hash, string path)
    {
        if (hash is null
            || hash.Length != 64
            || hash.Any(ch => !Uri.IsHexDigit(ch)))
        {
            throw new PortableArchiveException(
                "invalid_checksum",
                $"Portable archive entry '{path}' has an invalid SHA-256 checksum.");
        }
    }

    internal static bool FixedHashEquals(string left, string right)
    {
        if (left.Length != right.Length)
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left),
            Convert.FromHexString(right));
    }

    internal static T Deserialize<T>(
        byte[] bytes,
        string code,
        string message,
        JsonSerializerOptions options)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, options)
                ?? throw new PortableArchiveException(code, message);
        }
        catch (PortableArchiveException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException
            or NotSupportedException)
        {
            throw new PortableArchiveException(code, message, exception);
        }
    }
}

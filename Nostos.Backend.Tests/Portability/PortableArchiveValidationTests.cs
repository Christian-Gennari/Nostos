using System.Text;
using System.Text.Json;
using FluentAssertions;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Portability;
using Nostos.Shared.Enums;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

public sealed class PortableArchiveValidationTests
{
    private static readonly string ValidHash = new('a', 64);

    public static IEnumerable<object[]> RejectedPaths()
    {
        yield return ["/absolute"];
        yield return ["\\absolute"];
        yield return ["C:\\absolute"];
        yield return ["../escape"];
        yield return ["./escape"];
        yield return ["foo/../bar"];
        yield return ["foo\\..\\bar"];
        yield return ["foo//bar"];
        yield return ["file\0name"];
        yield return [new string('x', 256)];
    }

    [Theory]
    [MemberData(nameof(RejectedPaths))]
    public void Archive_paths_preserve_current_hostile_path_rejections(string path)
    {
        var exception = ThrowPortableError(
            () => PortableArchiveValidation.ValidateArchivePath(path),
            "unsafe_archive_path");

        exception.Message.Should().Be($"Portable archive contains unsafe path '{path}'.");
    }

    [Fact]
    public void Archive_paths_preserve_current_empty_and_control_character_behavior()
    {
        ThrowPortableError(
            () => PortableArchiveValidation.ValidateArchivePath(string.Empty),
            "unsafe_archive_path");
        ThrowPortableError(
            () => PortableArchiveValidation.ValidateDirectoryEntryName(string.Empty),
            "directory_entry_not_allowed");

        var validBoundary = new string('x', 255);
        PortableArchiveValidation.ValidateArchivePath(validBoundary).Should().Be(validBoundary);

        var otherControlCharacter = "file\u0001name";
        PortableArchiveValidation.ValidateArchivePath(otherControlCharacter)
            .Should().Be(otherControlCharacter);
    }

    [Fact]
    public void Entry_count_and_compressed_archive_size_keep_their_inclusive_boundaries()
    {
        PortableArchiveValidation.ValidateArchiveEntryCount(
            PortableArchiveLimits.MaxArchiveEntries);
        ThrowPortableError(
            () => PortableArchiveValidation.ValidateArchiveEntryCount(
                PortableArchiveLimits.MaxArchiveEntries + 1),
            "too_many_entries");

        PortableArchiveValidation.ValidateArchiveSize(PortableArchiveLimits.MaxArchiveBytes);
        ThrowPortableError(
            () => PortableArchiveValidation.ValidateArchiveSize(
                PortableArchiveLimits.MaxArchiveBytes + 1),
            "archive_too_large");
    }

    [Fact]
    public void Declared_entry_length_and_aggregate_limits_keep_their_inclusive_boundaries()
    {
        var compressedAtSingleEntryLimit =
            (PortableArchiveLimits.MaxSingleEntryBytes + 999) / 1000;
        PortableArchiveValidation.ValidateDeclaredEntry(
            "payload.bin",
            PortableArchiveLimits.MaxSingleEntryBytes,
            compressedAtSingleEntryLimit,
            0).Should().Be(PortableArchiveLimits.MaxSingleEntryBytes);

        ThrowPortableError(
            () => PortableArchiveValidation.ValidateDeclaredEntry(
                "payload.bin",
                PortableArchiveLimits.MaxSingleEntryBytes + 1,
                1,
                0),
            "entry_too_large");
        ThrowPortableError(
            () => PortableArchiveValidation.ValidateDeclaredEntry(
                "payload.bin",
                -1,
                1,
                0),
            "entry_too_large");

        PortableArchiveValidation.ValidateDeclaredEntry(
            "zero.bin",
            0,
            0,
            PortableArchiveLimits.MaxUncompressedBytes)
            .Should().Be(PortableArchiveLimits.MaxUncompressedBytes);
        ThrowPortableError(
            () => PortableArchiveValidation.ValidateDeclaredEntry(
                "one.bin",
                1,
                1,
                PortableArchiveLimits.MaxUncompressedBytes),
            "archive_expands_too_large");
    }

    [Fact]
    public void Compression_ratio_guard_keeps_its_threshold_and_strict_comparison()
    {
        PortableArchiveValidation.ValidateDeclaredEntry(
            "ratio-1000.bin",
            2_000_000,
            2_000,
            0).Should().Be(2_000_000);
        PortableArchiveValidation.ValidateDeclaredEntry(
            "ratio-not-checked.bin",
            1024 * 1024,
            0,
            0).Should().Be(1024 * 1024);

        ThrowPortableError(
            () => PortableArchiveValidation.ValidateDeclaredEntry(
                "ratio-over-1000.bin",
                2_000_001,
                2_000,
                0),
            "suspicious_compression");
        ThrowPortableError(
            () => PortableArchiveValidation.ValidateDeclaredEntry(
                "zero-compressed.bin",
                1024 * 1024 + 1,
                0,
                0),
            "suspicious_compression");
    }

    [Fact]
    public void Manifest_and_payload_stream_size_guards_keep_their_boundaries()
    {
        PortableArchiveValidation.ValidateExportDataSize(PortableArchiveLimits.MaxDataBytes);
        ThrowPortableError(
            () => PortableArchiveValidation.ValidateExportDataSize(
                PortableArchiveLimits.MaxDataBytes + 1),
            "data_too_large");

        PortableArchiveValidation.ValidateDeclaredReadSize(
            "manifest.json",
            PortableArchiveLimits.MaxManifestBytes,
            PortableArchiveLimits.MaxManifestBytes);
        ThrowPortableError(
            () => PortableArchiveValidation.ValidateDeclaredReadSize(
                "manifest.json",
                PortableArchiveLimits.MaxManifestBytes + 1,
                PortableArchiveLimits.MaxManifestBytes),
            "entry_too_large");
        PortableArchiveValidation.ValidateObservedReadSize(
            "data/library.json",
            PortableArchiveLimits.MaxDataBytes,
            PortableArchiveLimits.MaxDataBytes);
        ThrowPortableError(
            () => PortableArchiveValidation.ValidateObservedReadSize(
                "data/library.json",
                PortableArchiveLimits.MaxDataBytes + 1,
                PortableArchiveLimits.MaxDataBytes),
            "entry_too_large");
        PortableArchiveValidation.ValidateCopiedMediaSize(10, 10);
        PortableArchiveValidation.ValidateExpectedMediaSize(10, 10);
        ThrowPortableError(
            () => PortableArchiveValidation.ValidateCopiedMediaSize(11, 10),
            "entry_too_large");
        ThrowPortableError(
            () => PortableArchiveValidation.ValidateExpectedMediaSize(11, 10),
            "entry_too_large");
    }

    [Fact]
    public void Duplicate_path_decision_keeps_case_insensitive_archive_inventory_semantics()
    {
        PortableArchiveValidation.ValidateUniqueArchivePath("payload.bin", added: true);
        var exception = ThrowPortableError(
            () => PortableArchiveValidation.ValidateUniqueArchivePath(
                "PAYLOAD.BIN",
                added: false),
            "duplicate_path");

        exception.Message.Should()
            .Be("Portable archive contains duplicate path 'PAYLOAD.BIN'.");
    }

    [Fact]
    public void Manifest_validation_preserves_format_version_and_data_version_rules()
    {
        foreach (var version in new[] { 1, 2, 3 })
        {
            PortableArchiveValidation.ValidateManifest(Manifest(dataVersion: version));
        }

        ThrowPortableError(
            () => PortableArchiveValidation.ValidateManifest(
                Manifest() with { Format = "other" }),
            "unsupported_format");
        ThrowPortableError(
            () => PortableArchiveValidation.ValidateManifest(
                Manifest() with { FormatVersion = 999 }),
            "unsupported_version");
        ThrowPortableError(
            () => PortableArchiveValidation.ValidateManifest(
                Manifest() with { DataVersion = 999 }),
            "unsupported_data_version");
        ThrowPortableError(
            () => PortableArchiveValidation.ValidateManifest(
                Manifest() with { Counts = null! }),
            "malformed_manifest");
        ThrowPortableError(
            () => PortableArchiveValidation.ValidateManifest(
                Manifest() with { Data = null! }),
            "malformed_manifest");
        ThrowPortableError(
            () => PortableArchiveValidation.ValidateManifest(
                Manifest() with { Media = null! }),
            "malformed_manifest");
        ThrowPortableError(
            () => PortableArchiveValidation.ValidateManifest(
                Manifest() with
                {
                    Data = new PortableArchivePayload("data/other.json", 0, ValidHash),
                }),
            "invalid_data_path");
        ThrowPortableError(
            () => PortableArchiveValidation.ValidateManifest(
                Manifest() with
                {
                    Data = new PortableArchivePayload(
                        PortableArchiveFormat.DataPath,
                        PortableArchiveLimits.MaxDataBytes + 1,
                        ValidHash),
                }),
            "data_too_large");
        ThrowPortableError(
            () => PortableArchiveValidation.ValidateManifest(
                Manifest() with
                {
                    Data = new PortableArchivePayload(
                        PortableArchiveFormat.DataPath,
                        -1,
                        ValidHash),
                }),
            "data_too_large");
        ThrowPortableError(
            () => PortableArchiveValidation.ValidateManifest(
                Manifest() with
                {
                    Data = new PortableArchivePayload(
                        PortableArchiveFormat.DataPath,
                        0,
                        "not-a-checksum"),
                }),
            "invalid_checksum");
    }

    [Fact]
    public void Manifest_entry_lookup_length_hash_version_and_count_decisions_keep_their_codes()
    {
        ThrowPortableError(
            () => PortableArchiveValidation.ValidateManifestEntryFound(found: false),
            "missing_manifest");
        ThrowPortableError(
            () => PortableArchiveValidation.ValidateDataEntryFound(
                PortableArchiveFormat.DataPath,
                found: false),
            "missing_data");
        ThrowPortableError(
            () => PortableArchiveValidation.ValidateDataEntryLength(2, 3),
            "data_length_mismatch");
        ThrowPortableError(
            () => PortableArchiveValidation.ValidateDataVersionAgreement(2, 3),
            "data_version_mismatch");
        ThrowPortableError(
            () => PortableArchiveValidation.ValidateManifestCounts(
                new PortableArchiveCounts(1, 0, 0, 0, 0, 0, 0, 0, 0),
                EmptyCounts()),
            "count_mismatch");
    }

    [Fact]
    public void Manifest_and_data_json_parse_errors_keep_their_typed_wrapping()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        var malformed = ThrowPortableError(
            () => PortableArchiveValidation.Deserialize<PortableArchiveManifest>(
                Encoding.UTF8.GetBytes("{ invalid"),
                "malformed_manifest",
                "Portable archive manifest is malformed.",
                options),
            "malformed_manifest");
        malformed.InnerException.Should().BeOfType<JsonException>();

        var nullData = ThrowPortableError(
            () => PortableArchiveValidation.Deserialize<PortableLibraryData>(
                Encoding.UTF8.GetBytes("null"),
                "malformed_data",
                "Portable archive relational payload is malformed.",
                options),
            "malformed_data");
        nullData.Message.Should().Be("Portable archive relational payload is malformed.");
        nullData.InnerException.Should().BeNull();
    }

    [Fact]
    public void Expected_entry_inventory_rejects_missing_and_unexpected_paths()
    {
        var manifest = Manifest();
        var expected = new[]
        {
            PortableArchiveFormat.ManifestPath,
            PortableArchiveFormat.DataPath,
        };
        PortableArchiveValidation.ValidateArchiveInventory(manifest, expected);
        PortableArchiveValidation.ValidateArchiveInventory(
            manifest,
            ["MANIFEST.JSON", "DATA/LIBRARY.JSON"]);

        ThrowPortableError(
            () => PortableArchiveValidation.ValidateArchiveInventory(
                manifest,
                [PortableArchiveFormat.ManifestPath]),
            "missing_referenced_media");
        var withMedia = Manifest(media: [
            new PortableArchiveMediaEntry(
                Guid.NewGuid(),
                PortableArchiveFormat.BookMediaKind,
                "media/books/example/book.epub",
                "book.epub",
                "application/epub+zip",
                0,
                ValidHash),
        ]);
        ThrowPortableError(
            () => PortableArchiveValidation.ValidateArchiveInventory(withMedia, expected),
            "missing_referenced_media");
        ThrowPortableError(
            () => PortableArchiveValidation.ValidateArchiveInventory(
                manifest,
                [.. expected, "payload.bin"]),
            "unexpected_entry");
    }

    [Fact]
    public void Data_and_media_hash_decisions_preserve_checksum_errors()
    {
        PortableArchiveValidation.ValidateDataHash(ValidHash, ValidHash.ToUpperInvariant());
        ThrowPortableError(
            () => PortableArchiveValidation.ValidateDataHash(ValidHash, new string('b', 64)),
            "data_checksum_mismatch");

        PortableArchiveValidation.ValidateMediaEntryLength("media/item.epub", 10, 10);
        PortableArchiveValidation.ValidateMediaHash("media/item.epub", 10, 10, ValidHash, ValidHash);
        ThrowPortableError(
            () => PortableArchiveValidation.ValidateMediaEntryLength("media/item.epub", 9, 10),
            "media_length_mismatch");
        ThrowPortableError(
            () => PortableArchiveValidation.ValidateMediaHash(
                "media/item.epub",
                9,
                10,
                ValidHash,
                ValidHash),
            "media_checksum_mismatch");
        ThrowPortableError(
            () => PortableArchiveValidation.ValidateMediaHash(
                "media/item.epub",
                10,
                10,
                ValidHash,
                new string('b', 64)),
            "media_checksum_mismatch");
    }

    [Fact]
    public void Stored_media_length_reopen_and_hash_decisions_preserve_integrity_errors()
    {
        PortableArchiveValidation.ValidateStoredMediaLength("media/item.epub", 10, 10);
        PortableArchiveValidation.ValidateStoredMediaCanReopen("media/item.epub", opened: true);
        PortableArchiveValidation.ValidateStoredMediaHash(
            "media/item.epub",
            10,
            10,
            ValidHash,
            ValidHash);

        ThrowPortableError(
            () => PortableArchiveValidation.ValidateStoredMediaLength(
                "media/item.epub",
                null,
                10),
            "integrity_failed");
        ThrowPortableError(
            () => PortableArchiveValidation.ValidateStoredMediaLength(
                "media/item.epub",
                9,
                10),
            "integrity_failed");
        ThrowPortableError(
            () => PortableArchiveValidation.ValidateStoredMediaCanReopen(
                "media/item.epub",
                opened: false),
            "integrity_failed");
        ThrowPortableError(
            () => PortableArchiveValidation.ValidateStoredMediaHash(
                "media/item.epub",
                9,
                10,
                ValidHash,
                ValidHash),
            "integrity_failed");
    }

    [Fact]
    public void Media_manifest_rejects_unknown_kind_book_and_duplicate_media()
    {
        var data = DataWithBook(hasBookFile: true);
        var book = data.Books[0];

        RejectMedia(data, [Media(book, bookId: Guid.NewGuid())], "media_unknown_book");
        RejectMedia(data, [Media(book, kind: "other")], "invalid_media_kind");
        RejectMedia(
            data,
            [Media(book), Media(book)],
            "duplicate_media");
    }

    [Fact]
    public void Media_manifest_rejects_duplicate_paths_and_unsafe_filenames()
    {
        var data = DataWithBook(hasBookFile: true);
        var book = data.Books[0];
        var firstBook = data.Books[0];
        var secondBook = Book(Guid.NewGuid(), firstBook.WorkId, hasBookFile: true);
        var withTwoBooks = data with { Books = [firstBook, secondBook] };

        RejectMedia(
            withTwoBooks,
            [Media(firstBook), Media(secondBook, path: Media(firstBook).Path)],
            "duplicate_path");
        RejectMedia(
            data,
            [Media(book, fileName: "../book.epub")],
            "invalid_media_filename");
        RejectMedia(
            data,
            [Media(book, fileName: "cover.epub")],
            "invalid_media_filename");
    }

    [Fact]
    public void Media_manifest_rejects_extension_path_size_hash_and_state_mismatches()
    {
        var data = DataWithBook(hasBookFile: true);
        var book = data.Books[0];

        RejectMedia(
            data,
            [Media(book, extension: ".exe")],
            "invalid_media_filename");
        RejectMedia(
            data,
            [Media(book, path: "media/other/book.epub")],
            "invalid_media_path");
        RejectMedia(
            data,
            [Media(book, length: -1)],
            "entry_too_large");
        RejectMedia(
            data,
            [Media(book, length: PortableArchiveLimits.MaxSingleEntryBytes + 1)],
            "entry_too_large");
        RejectMedia(
            data,
            [Media(book, sha256: "bad")],
            "invalid_checksum");
        RejectMedia(
            DataWithBook(hasBookFile: true),
            [],
            "missing_referenced_media");
        RejectMedia(
            DataWithBook(hasCover: true),
            [],
            "missing_referenced_media");
        var noMediaData = DataWithBook();
        RejectMedia(
            noMediaData,
            [Media(noMediaData.Books[0])],
            "missing_referenced_media");
    }

    [Fact]
    public void Portable_data_preserves_version_boundary_and_required_collection_checks()
    {
        PortableArchiveValidation.ValidatePortableData(EmptyData(version: 1));
        PortableArchiveValidation.ValidatePortableData(EmptyData(version: 2));
        PortableArchiveValidation.ValidatePortableData(EmptyData(version: 3));

        RejectData(EmptyData(version: 4), "unsupported_data_version");
        RejectData(
            EmptyData(version: 1) with { WritingNotes = [] },
            "unexpected_version_data");
        RejectData(
            EmptyData(version: 1) with { NoteImportBookLinks = [] },
            "unexpected_version_data");
        RejectData(
            EmptyData(version: 2) with { NoteImportBookLinks = [] },
            "unexpected_version_data");
        RejectData(
            EmptyData(version: 2) with { WritingNotes = null },
            "malformed_data");
        RejectData(
            EmptyData(version: 3) with { NoteImportBookLinks = null },
            "malformed_data");
        RejectData(
            EmptyData() with { Topics = null! },
            "malformed_data");
    }

    [Fact]
    public void Portable_data_rejects_empty_and_duplicate_entity_ids_for_every_entity_type()
    {
        var work = Work();
        var book = Book(Guid.NewGuid(), work.Id);
        var collection = new PortableCollection(Guid.NewGuid(), "Collection", null);
        var note = Note(Guid.NewGuid(), book.Id);
        var topic = new PortableTopic(Guid.NewGuid(), "Topic");
        var writing = Writing(Guid.NewGuid());
        var acquisition = Acquisition(Guid.NewGuid(), book.Id);
        var importLink = NoteImportBookLink(Guid.NewGuid(), book.Id);

        RejectData(DataWithBook() with { Works = [work, work] }, "duplicate_id");
        RejectData(DataWithBook() with { Books = [book, book] }, "duplicate_id");
        RejectData(EmptyData() with { Collections = [collection, collection] }, "duplicate_id");
        RejectData(DataWithBook() with { Notes = [note, note] }, "duplicate_id");
        RejectData(EmptyData() with { Topics = [topic, topic] }, "duplicate_id");
        RejectData(EmptyData() with { Writings = [writing, writing] }, "duplicate_id");
        RejectData(
            DataWithBook() with { BookAcquisitions = [acquisition, acquisition] },
            "duplicate_id");
        RejectData(
            DataWithBook() with { NoteImportBookLinks = [importLink, importLink] },
            "duplicate_id");
        RejectData(
            DataWithBook() with { Works = [work with { Id = Guid.Empty }] },
            "duplicate_id");
    }

    [Fact]
    public void Portable_data_rejects_invalid_book_identity_and_relationships()
    {
        var data = DataWithBook();
        var book = data.Books[0];

        RejectData(data with { Books = [book with { WorkId = Guid.NewGuid() }] }, "malformed_relationship");
        RejectData(data with { Books = [book with { Type = "other" }] }, "unsupported_book_type");
        RejectData(data with { Books = [book with { Status = "other" }] }, "invalid_book_status");

        var duplicateIsbn = data with
        {
            Books =
            [
                book with { Isbn = "978-0-306-40615-7" },
                Book(Guid.NewGuid(), book.WorkId) with { Isbn = "9780306406157" },
            ],
        };
        RejectData(duplicateIsbn, "duplicate_book_identity");

        var audioBooks = data with
        {
            Books =
            [
                book with { Type = "audiobook", Asin = "B000000001" },
                Book(Guid.NewGuid(), book.WorkId) with
                {
                    Type = "audiobook",
                    Asin = "B000000001",
                },
            ],
        };
        RejectData(audioBooks, "duplicate_book_identity");

        var collection = new PortableCollection(Guid.NewGuid(), "Collection", null);
        RejectData(
            data with
            {
                Collections = [collection],
                BookCollections = [
                    new PortableBookCollection(book.Id, Guid.NewGuid(), DateTime.UtcNow),
                ],
            },
            "malformed_relationship");
        RejectData(
            data with
            {
                Collections = [collection],
                BookCollections =
                [
                    new PortableBookCollection(book.Id, collection.Id, DateTime.UtcNow),
                    new PortableBookCollection(book.Id, collection.Id, DateTime.UtcNow),
                ],
            },
            "duplicate_relationship");
        RejectData(
            data with
            {
                Collections = [collection with { ParentId = Guid.NewGuid() }],
            },
            "malformed_relationship");

        var firstCollection = new PortableCollection(Guid.NewGuid(), "First", null);
        var secondCollection = new PortableCollection(Guid.NewGuid(), "Second", firstCollection.Id);
        RejectData(
            data with
            {
                Collections =
                [
                    firstCollection with { ParentId = secondCollection.Id },
                    secondCollection,
                ],
            },
            "malformed_relationship");
    }

    [Fact]
    public void Portable_data_rejects_note_topic_writing_and_acquisition_relationships()
    {
        var data = DataWithBook();
        var book = data.Books[0];
        var note = Note(Guid.NewGuid(), book.Id);
        var topic = new PortableTopic(Guid.NewGuid(), "Topic");

        RejectData(data with { Notes = [Note(note.Id, Guid.NewGuid())] }, "malformed_relationship");
        RejectData(
            data with { Topics = [topic, topic with { Id = Guid.NewGuid() }] },
            "duplicate_topic");
        RejectData(
            data with
            {
                Notes = [note],
                NoteTopics = [new PortableNoteTopic(note.Id, Guid.NewGuid())],
            },
            "malformed_relationship");
        RejectData(
            data with
            {
                Notes = [note],
                Topics = [topic],
                NoteTopics =
                [
                    new PortableNoteTopic(note.Id, topic.Id),
                    new PortableNoteTopic(note.Id, topic.Id),
                ],
            },
            "duplicate_relationship");

        var writing = Writing(Guid.NewGuid());
        RejectData(
            data with
            {
                Notes = [note],
                WritingNotes = [new PortableWritingNote(Guid.NewGuid(), note.Id, DateTime.UtcNow)],
            },
            "malformed_relationship");
        RejectData(
            data with
            {
                Notes = [note],
                Writings = [writing],
                WritingNotes =
                [
                    new PortableWritingNote(writing.Id, note.Id, DateTime.UtcNow),
                    new PortableWritingNote(writing.Id, note.Id, DateTime.UtcNow),
                ],
            },
            "duplicate_relationship");
        RejectData(
            data with { Writings = [writing with { Type = "other" }] },
            "invalid_writing_type");
        RejectData(
            data with { Writings = [writing with { ParentId = Guid.NewGuid() }] },
            "malformed_relationship");

        var firstWriting = Writing(Guid.NewGuid());
        var secondWriting = Writing(Guid.NewGuid()) with { ParentId = firstWriting.Id };
        RejectData(
            data with
            {
                Writings =
                [
                    firstWriting with { ParentId = secondWriting.Id },
                    secondWriting,
                ],
            },
            "malformed_relationship");

        var acquisition = Acquisition(Guid.NewGuid(), book.Id);
        RejectData(
            data with { BookAcquisitions = [Acquisition(Guid.NewGuid(), Guid.NewGuid())] },
            "malformed_relationship");
        RejectData(
            data with
            {
                BookAcquisitions =
                [
                    acquisition,
                    Acquisition(Guid.NewGuid(), book.Id),
                ],
            },
            "duplicate_relationship");
        RejectData(
            data with
            {
                BookAcquisitions =
                [
                    acquisition,
                    acquisition with
                    {
                        Id = Guid.NewGuid(),
                        BookId = Guid.NewGuid(),
                    },
                ],
            },
            "malformed_relationship");
        RejectData(
            data with
            {
                BookAcquisitions =
                [
                    acquisition,
                    acquisition with { Id = Guid.NewGuid() },
                ],
            },
            "duplicate_relationship");
        var secondBook = Book(Guid.NewGuid(), book.WorkId);
        RejectData(
            data with
            {
                Books = [book, secondBook],
                BookAcquisitions =
                [
                    acquisition,
                    acquisition with
                    {
                        Id = Guid.NewGuid(),
                        BookId = secondBook.Id,
                    },
                ],
            },
            "duplicate_acquisition");
    }

    [Fact]
    public void Portable_data_rejects_note_import_links_and_media_state_mismatches()
    {
        var data = DataWithBook();
        var book = data.Books[0];

        RejectData(
            data with
            {
                NoteImportBookLinks =
                [NoteImportBookLink(Guid.NewGuid(), Guid.NewGuid())],
            },
            "malformed_relationship");
        RejectData(
            data with
            {
                NoteImportBookLinks =
                [
                    NoteImportBookLink(Guid.NewGuid(), book.Id),
                    NoteImportBookLink(Guid.NewGuid(), book.Id),
                ],
            },
            "duplicate_relationship");
    }

    [Fact]
    public void Imported_relational_state_guards_preserve_integrity_failures()
    {
        var counts = EmptyCounts();
        PortableArchiveValidation.ValidateImportedCounts(counts, counts);
        ThrowPortableError(
            () => PortableArchiveValidation.ValidateImportedCounts(
                counts with { Books = 1 },
                counts),
            "integrity_failed");

        var workId = Guid.NewGuid();
        PortableArchiveValidation.RequireSameIds([workId], [workId], "work");
        ThrowPortableError(
            () => PortableArchiveValidation.RequireSameIds([Guid.NewGuid()], [], "work"),
            "integrity_failed");
        PortableArchiveValidation.ValidateImportedRelationshipSet([1, 2], [2, 1], "links differ");
        ThrowPortableError(
            () => PortableArchiveValidation.ValidateImportedRelationshipSet([1], [2], "links differ"),
            "integrity_failed");

        var expectedBook = Book(Guid.NewGuid(), Guid.NewGuid());
        PortableArchiveValidation.ValidateImportedBookType(
            expectedBook.Id,
            expectedBook.Type,
            expectedBook.Type);
        PortableArchiveValidation.ValidateImportedBookProgress(
            expectedBook,
            expectedBook.Progress.LastLocation,
            expectedBook.Progress.ProgressPercent,
            expectedBook.Progress.Rating,
            expectedBook.Progress.IsFavorite,
            expectedBook.Progress.PersonalReview);
        ThrowPortableError(
            () => PortableArchiveValidation.ValidateImportedBookType(
                expectedBook.Id,
                expectedBook.Type,
                "other"),
            "integrity_failed");
        ThrowPortableError(
            () => PortableArchiveValidation.ValidateImportedBookProgress(
                expectedBook,
                expectedBook.Progress.LastLocation,
                expectedBook.Progress.ProgressPercent,
                expectedBook.Progress.Rating + 1,
                expectedBook.Progress.IsFavorite,
                expectedBook.Progress.PersonalReview),
            "integrity_failed");

        var expectedWriting = Writing(Guid.NewGuid());
        PortableArchiveValidation.ValidateImportedWritingState(
            expectedWriting,
            expectedWriting.ParentId,
            expectedWriting.Name,
            expectedWriting.Content,
            expectedWriting.Type);
        ThrowPortableError(
            () => PortableArchiveValidation.ValidateImportedWritingState(
                expectedWriting,
                Guid.NewGuid(),
                expectedWriting.Name,
                expectedWriting.Content,
                expectedWriting.Type),
            "integrity_failed");
    }

    private static void RejectData(PortableLibraryData data, string code) =>
        ThrowPortableError(() => PortableArchiveValidation.ValidatePortableData(data), code);

    private static void RejectMedia(
        PortableLibraryData data,
        IReadOnlyList<PortableArchiveMediaEntry> media,
        string code) =>
        ThrowPortableError(
            () => PortableArchiveValidation.ValidateMediaManifest(
                Manifest(media: media),
                data),
            code);

    private static PortableArchiveException ThrowPortableError(Action action, string code)
    {
        var exception = action.Should().Throw<PortableArchiveException>().Which;
        exception.Code.Should().Be(code);
        return exception;
    }

    private static PortableArchiveManifest Manifest(
        int dataVersion = 3,
        IReadOnlyList<PortableArchiveMediaEntry>? media = null) =>
        new(
            PortableArchiveFormat.Name,
            PortableArchiveFormat.Version,
            dataVersion,
            DateTime.UnixEpoch,
            "test",
            EmptyCounts(),
            new PortableArchivePayload(PortableArchiveFormat.DataPath, 0, ValidHash),
            media ?? []);

    private static PortableArchiveCounts EmptyCounts() =>
        new(0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static PortableLibraryData EmptyData(int version = 3) =>
        new(
            version,
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            null,
            version >= 2 ? [] : null,
            version >= 3 ? [] : null);

    private static PortableLibraryData DataWithBook(
        bool hasBookFile = false,
        bool hasCover = false)
    {
        var work = Work();
        return EmptyData() with
        {
            Works = [work],
            Books = [Book(Guid.NewGuid(), work.Id, hasBookFile, hasCover)],
        };
    }

    private static PortableWork Work(Guid? id = null) =>
        new(id ?? Guid.NewGuid(), "Work", null, DateTime.UnixEpoch);

    private static PortableBook Book(
        Guid id,
        Guid workId,
        bool hasBookFile = false,
        bool hasCover = false) =>
        new(
            id,
            workId,
            "ebook",
            Enum.GetNames<BookStatus>()[0],
            null,
            "Book",
            null,
            new PortableBookMetadata(
                null, null, null, null, null, null, null, null, null, null, null, null),
            new PortableReadingProgress(null, 0, 0, false, null, null, null),
            DateTime.UnixEpoch,
            null,
            null,
            null,
            null,
            null,
            null,
            hasBookFile,
            hasCover);

    private static PortableArchiveMediaEntry Media(
        PortableBook book,
        Guid? bookId = null,
        string kind = PortableArchiveFormat.BookMediaKind,
        string? path = null,
        string? fileName = null,
        string? extension = null,
        long length = 0,
        string? sha256 = null)
    {
        extension ??= kind == PortableArchiveFormat.CoverMediaKind ? ".jpg" : ".epub";
        fileName ??= $"{kind}{extension}";
        return new PortableArchiveMediaEntry(
            bookId ?? book.Id,
            kind,
            path ?? PortableArchiveValidation.MediaPath(bookId ?? book.Id, kind, extension),
            fileName,
            "application/octet-stream",
            length,
            sha256 ?? ValidHash);
    }

    private static PortableNote Note(Guid id, Guid bookId) =>
        new(
            id,
            "Note",
            null,
            null,
            DateTime.UnixEpoch,
            bookId,
            null,
            "manual",
            "default",
            "none",
            null,
            false);

    private static PortableWriting Writing(Guid id) =>
        new(
            id,
            "Writing",
            Enum.GetNames<WritingType>()[0],
            null,
            null,
            DateTime.UnixEpoch,
            DateTime.UnixEpoch);

    private static PortableBookAcquisition Acquisition(Guid id, Guid bookId) =>
        new(
            id,
            bookId,
            "provider",
            "Provider",
            "external",
            "asset",
            null,
            null,
            null,
            null,
            DateTime.UnixEpoch);

    private static PortableNoteImportBookLink NoteImportBookLink(Guid id, Guid bookId) =>
        new(id, "source", "key", bookId, DateTime.UnixEpoch);
}

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Portability;
using Nostos.Product.BookText;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

public sealed class PortableArchiveServiceTests
{
    [Fact]
    public async Task Portable_import_schedules_supported_publications_for_index_rebuild()
    {
        await using var source = await LocalPortableTestLibrary.CreateAsync();
        var ids = await PortableArchiveTestSupport.PopulateRepresentativeAsync(
            source.Db,
            source.Storage);

        using var archive = new MemoryStream();
        await source.Portability().ExportAsync(archive);

        await using var destination = await LocalPortableTestLibrary.CreateAsync();
        var scheduler = new RecordingBookTextScheduler();
        var importer = new PortableArchiveService(
            destination.Db,
            destination.Storage,
            NullLogger<PortableArchiveService>.Instance,
            scheduler);

        archive.Position = 0;
        var imported = await importer.ImportAsync(archive);
        imported.IntegrityVerified.Should().BeTrue();

        scheduler.Scheduled.Should().BeEquivalentTo(
        [
            (ids.EpubBookId, "book.epub"),
            (ids.PdfBookId, "book.pdf"),
        ]);
    }

    [Fact]
    public async Task Portable_archive_round_trips_user_data_media_and_relationships()
    {
        await using var source = await LocalPortableTestLibrary.CreateAsync();
        var ids = await PortableArchiveTestSupport.PopulateRepresentativeAsync(
            source.Db,
            source.Storage);

        using var firstArchive = new MemoryStream();
        var exported = await source.Portability().ExportAsync(firstArchive);

        exported.FormatVersion.Should().Be(1);
        exported.Counts.Books.Should().Be(4);
        exported.MediaFiles.Should().Be(5);

        await using var destination = await LocalPortableTestLibrary.CreateAsync();
        firstArchive.Position = 0;
        var imported = await destination.Portability().ImportAsync(firstArchive);

        imported.IntegrityVerified.Should().BeTrue();
        imported.Counts.Should().Be(exported.Counts);
        imported.MediaFiles.Should().Be(exported.MediaFiles);

        destination.Db.ChangeTracker.Clear();

        (await destination.Db.Works.CountAsync()).Should().Be(3);
        (await destination.Db.Books.CountAsync()).Should().Be(4);
        (await destination.Db.BookCollections.CountAsync()).Should().Be(3);
        (await destination.Db.NoteTopics.CountAsync()).Should().Be(1);
        (await destination.Db.Writings.CountAsync()).Should().Be(2);
        (await destination.Db.WritingNotes.CountAsync()).Should().Be(1);
        (await destination.Db.BookAcquisitions.CountAsync()).Should().Be(1);

        var destinationKeptNote = await destination.Db.WritingNotes.AsNoTracking().SingleAsync();
        destinationKeptNote.WritingId.Should().Be(ids.WritingDocumentId);
        destinationKeptNote.NoteId.Should().Be(ids.NoteId);

        var epub = await destination.Db.Books
            .AsNoTracking()
            .OfType<EBookModel>()
            .SingleAsync(x => x.Id == ids.EpubBookId);
        epub.Progress.LastLocation.Should().Be("epubcfi(/6/4)");
        epub.Progress.ProgressPercent.Should().Be(42);
        epub.Progress.Rating.Should().Be(5);
        epub.Progress.IsFavorite.Should().BeTrue();
        epub.Progress.PersonalReview.Should().Be("Important review");
        epub.FileDetails.FileName.Should().Be("book.epub");
        epub.FileDetails.CoverFileName.Should().Be("cover.jpg");
        epub.FileDetails.ChaptersJson.Should().Be("[{\"generated\":true}]",
            "reader chapter metadata must remain available after import");
        epub.FileDetails.LocationsJson.Should().BeNull(
            "epub.js locations are a reconstructable cache");

        var audio = await destination.Db.Books
            .AsNoTracking()
            .OfType<AudioBookModel>()
            .SingleAsync(x => x.Id == ids.AudioBookId);
        audio.Duration.Should().Be("10:11:12");
        audio.Narrator.Should().Be("Narrator");
        audio.Progress.LastLocation.Should().Be("3721.5");

        var note = await destination.Db.Notes
            .AsNoTracking()
            .SingleAsync(x => x.Id == ids.NoteId);
        note.RawContent.Should().Be("raw thought");
        note.ProcessingMode.Should().Be("light_polish");
        note.SourceAnchorKind.Should().Be("epub_cfi");
        note.AnchorVerified.Should().BeTrue();

        var writing = await destination.Db.Writings
            .AsNoTracking()
            .SingleAsync(x => x.Id == ids.WritingDocumentId);
        writing.Content.Should().Be("<p>User-owned studio prose.</p>");
        writing.ParentId.Should().NotBeNull();

        var assistant = await destination.Db.AssistantSettings
            .AsNoTracking()
            .SingleAsync();
        assistant.CaptureProcessingMode.Should().Be("light_polish");

        (await destination.Db.AiProviderSettings.CountAsync()).Should().Be(0,
            "provider configuration and encrypted keys are deployment-specific");

        (await PortableArchiveTestSupport.ReadBookAsync(
            destination.Storage,
            ids.EpubBookId))
            .Should().Equal(Encoding.UTF8.GetBytes("EPUB-CONTENT-PORTABLE"));
        (await PortableArchiveTestSupport.ReadBookAsync(
            destination.Storage,
            ids.PdfBookId))
            .Should().Equal(Encoding.UTF8.GetBytes("PDF-CONTENT-PORTABLE"));
        (await PortableArchiveTestSupport.ReadBookAsync(
            destination.Storage,
            ids.AudioBookId))
            .Should().Equal(Encoding.UTF8.GetBytes("AUDIO-CONTENT-PORTABLE"));

        using var secondArchive = new MemoryStream();
        await destination.Portability().ExportAsync(secondArchive);

        await using var secondDestination = await LocalPortableTestLibrary.CreateAsync();
        secondArchive.Position = 0;
        var secondImport = await secondDestination.Portability().ImportAsync(secondArchive);

        secondImport.Counts.Should().Be(imported.Counts);
        secondImport.MediaFiles.Should().Be(5);
        (await secondDestination.Db.BookCollections.CountAsync()).Should().Be(3);
        (await secondDestination.Db.NoteTopics.CountAsync()).Should().Be(1);
        (await secondDestination.Db.Writings
            .AsNoTracking()
            .SingleAsync(x => x.Id == ids.WritingDocumentId))
            .Content.Should().Be("<p>User-owned studio prose.</p>");
    }

    [Fact]
    public async Task Export_to_non_seekable_stream_avoids_synchronous_io()
    {
        // Regression for #554: Kestrel's response stream is non-seekable and
        // rejects synchronous writes. ZipArchive finalization on a non-seekable
        // stream writes ZIP data descriptors synchronously, which previously
        // aborted the export mid-response.
        await using var source = await LocalPortableTestLibrary.CreateAsync();
        var ids = await PortableArchiveTestSupport.PopulateRepresentativeAsync(
            source.Db,
            source.Storage);

        var response = new SyncIoForbiddenStream();

        var exported = await source.Portability().ExportAsync(response);

        exported.FormatVersion.Should().Be(1);
        exported.Counts.Books.Should().Be(4);
        exported.MediaFiles.Should().Be(5);

        using var archiveBytes = new MemoryStream(response.WrittenBytes);
        using (var archive = new ZipArchive(
            archiveBytes,
            ZipArchiveMode.Read,
            leaveOpen: true))
        {
            archive.GetEntry("data/library.json").Should().NotBeNull();
            archive.GetEntry("manifest.json").Should().NotBeNull();

            // The Concepts -> Topics rename keeps the on-disk JSON keys so archives
            // exported before it still import.
            using var library = JsonDocument.Parse(
                archive.GetEntry("data/library.json")!.Open());
            library.RootElement.TryGetProperty("concepts", out _).Should().BeTrue();
            library.RootElement.TryGetProperty("noteConcepts", out _).Should().BeTrue();
            library.RootElement.TryGetProperty("topics", out _).Should().BeFalse();
        }

        await using var destination = await LocalPortableTestLibrary.CreateAsync();
        archiveBytes.Position = 0;
        var imported = await destination.Portability().ImportAsync(archiveBytes);

        imported.IntegrityVerified.Should().BeTrue();
        imported.Counts.Should().Be(exported.Counts);
        imported.MediaFiles.Should().Be(exported.MediaFiles);

        destination.Db.ChangeTracker.Clear();
        (await destination.Db.Books.CountAsync()).Should().Be(4);
        (await destination.Db.BookCollections.CountAsync()).Should().Be(3);
        (await destination.Db.WritingNotes.CountAsync()).Should().Be(1);

        (await PortableArchiveTestSupport.ReadBookAsync(
            destination.Storage,
            ids.EpubBookId))
            .Should().Equal(Encoding.UTF8.GetBytes("EPUB-CONTENT-PORTABLE"));
    }

    [Fact]
    public async Task Export_excludes_secrets_provider_paths_and_generated_caches()
    {
        await using var source = await LocalPortableTestLibrary.CreateAsync();
        await PortableArchiveTestSupport.PopulateRepresentativeAsync(
            source.Db,
            source.Storage);

        using var archive = new MemoryStream();
        await source.Portability().ExportAsync(archive);

        var entries = await ReadEntriesAsync(archive);
        var text = string.Join(
            "\n",
            entries
                .Where(x => x.Name is "manifest.json" or "data/library.json")
                .Select(x => Encoding.UTF8.GetString(x.Bytes)));

        text.Should().NotContain(PortableArchiveTestSupport.SecretMarker);
        text.Should().NotContain("private-model");
        text.Should().NotContain("source-machine");
        text.Should().NotContain("/srv/private");
        text.Should().NotContain("must-not-cross");
        text.Should().NotContain("locationsJson");

        entries.Should().NotContain(x =>
            x.Name.Contains("thumb", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Import_rejects_unsupported_version_before_mutating_destination()
    {
        using var archive = await ExportFixtureAsync();
        var entries = await ReadEntriesAsync(archive);
        MutateJsonEntry(entries, "manifest.json", root =>
        {
            root["formatVersion"] = 999;
        });
        using var hostile = await BuildArchiveAsync(entries);

        await using var destination = await LocalPortableTestLibrary.CreateAsync();
        hostile.Position = 0;

        var action = () => destination.Portability().ImportAsync(hostile);
        var exception = await action.Should().ThrowAsync<PortableArchiveException>();
        exception.Which.Code.Should().Be("unsupported_version");
        (await destination.Db.Books.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Import_rejects_unsupported_data_version_before_mutating_destination()
    {
        using var archive = await ExportFixtureAsync();
        var entries = await ReadEntriesAsync(archive);
        MutateJsonEntry(entries, "manifest.json", root =>
        {
            root["dataVersion"] = 999;
        });
        using var hostile = await BuildArchiveAsync(entries);

        await using var destination = await LocalPortableTestLibrary.CreateAsync();
        hostile.Position = 0;

        var action = () => destination.Portability().ImportAsync(hostile);
        var exception = await action.Should().ThrowAsync<PortableArchiveException>();
        exception.Which.Code.Should().Be("unsupported_data_version");
        (await destination.Db.Books.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Portable_archive_v1_without_writing_notes_imports_cleanly_with_empty_memberships()
    {
        using var archive = await ExportFixtureAsync();
        var entries = await ReadEntriesAsync(archive);

        // Turn this archive into a v1 archive: manifest dataVersion = 1, data.json version = 1, no writingNotes
        MutateJsonEntry(entries, "manifest.json", root =>
        {
            root["dataVersion"] = 1;
        });

        MutateJsonEntry(entries, "data/library.json", root =>
        {
            root["version"] = 1;
            root.Remove("writingNotes");
            root.Remove("noteImportBookLinks");
        });

        RehashDataDescriptor(entries);

        using var v1Archive = await BuildArchiveAsync(entries);
        await using var destination = await LocalPortableTestLibrary.CreateAsync();

        var imported = await destination.Portability().ImportAsync(v1Archive);
        imported.IntegrityVerified.Should().BeTrue();

        (await destination.Db.Writings.CountAsync()).Should().Be(2);
        (await destination.Db.Notes.CountAsync()).Should().Be(1);
        (await destination.Db.WritingNotes.CountAsync()).Should().Be(0,
            "v1 archive without writingNotes must import with empty memberships (backward-compatibility requirement)");
    }

    [Fact]
    public async Task Import_rejects_missing_manifest_and_missing_referenced_media()
    {
        using var archive = await ExportFixtureAsync();
        var entries = await ReadEntriesAsync(archive);

        using (var missingManifest = await BuildArchiveAsync(
            entries.Where(x => x.Name != "manifest.json")))
        {
            await using var destination = await LocalPortableTestLibrary.CreateAsync();
            missingManifest.Position = 0;
            var action = () => destination.Portability().ImportAsync(missingManifest);
            var exception = await action.Should().ThrowAsync<PortableArchiveException>();
            exception.Which.Code.Should().Be("missing_manifest");
            (await destination.Db.Books.CountAsync()).Should().Be(0);
        }

        var mediaName = entries
            .Select(x => x.Name)
            .First(x => x.StartsWith("media/books/", StringComparison.Ordinal));
        using var missingMedia = await BuildArchiveAsync(
            entries.Where(x => x.Name != mediaName));

        await using var secondDestination = await LocalPortableTestLibrary.CreateAsync();
        missingMedia.Position = 0;
        var secondAction = () => secondDestination.Portability().ImportAsync(missingMedia);
        var secondException = await secondAction.Should().ThrowAsync<PortableArchiveException>();
        secondException.Which.Code.Should().Be("missing_referenced_media");
        (await secondDestination.Db.Books.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Import_rejects_corrupt_manifest_before_mutating_destination()
    {
        using var archive = await ExportFixtureAsync();
        var entries = await ReadEntriesAsync(archive);
        var manifestIndex = entries.FindIndex(x => x.Name == "manifest.json");
        entries[manifestIndex] = new TestArchiveEntry(
            "manifest.json",
            Encoding.UTF8.GetBytes("{ definitely-not-valid-json"));

        using var corrupt = await BuildArchiveAsync(entries);
        await using var destination = await LocalPortableTestLibrary.CreateAsync();
        corrupt.Position = 0;

        var action = () => destination.Portability().ImportAsync(corrupt);
        var exception = await action.Should().ThrowAsync<PortableArchiveException>();

        exception.Which.Code.Should().Be("malformed_manifest");
        (await destination.Db.Books.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Import_rejects_duplicate_entity_ids_before_mutating_destination()
    {
        using var archive = await ExportFixtureAsync();
        var entries = await ReadEntriesAsync(archive);
        MutateJsonEntry(entries, "data/library.json", root =>
        {
            var books = root["books"]!.AsArray();
            var duplicateId = books[0]!["id"]!.GetValue<string>();
            books[1]!["id"] = duplicateId;
        });
        RehashDataDescriptor(entries);

        using var duplicateIds = await BuildArchiveAsync(entries);
        await using var destination = await LocalPortableTestLibrary.CreateAsync();
        duplicateIds.Position = 0;

        var action = () => destination.Portability().ImportAsync(duplicateIds);
        var exception = await action.Should().ThrowAsync<PortableArchiveException>();

        exception.Which.Code.Should().Be("duplicate_id");
        (await destination.Db.Books.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Import_rejects_malformed_relationship_duplicate_path_and_traversal()
    {
        using var archive = await ExportFixtureAsync();
        var entries = await ReadEntriesAsync(archive);

        var malformed = entries
            .Select(x => new TestArchiveEntry(x.Name, x.Bytes.ToArray()))
            .ToList();
        MutateJsonEntry(malformed, "data/library.json", root =>
        {
            var books = root["books"]!.AsArray();
            books[0]!["workId"] = Guid.NewGuid();
        });
        RehashDataDescriptor(malformed);
        using (var malformedArchive = await BuildArchiveAsync(malformed))
        {
            await using var destination = await LocalPortableTestLibrary.CreateAsync();
            malformedArchive.Position = 0;
            var action = () => destination.Portability().ImportAsync(malformedArchive);
            var exception = await action.Should().ThrowAsync<PortableArchiveException>();
            exception.Which.Code.Should().Be("malformed_relationship");
            (await destination.Db.Books.CountAsync()).Should().Be(0);
        }

        var media = entries.First(x =>
            x.Name.StartsWith("media/books/", StringComparison.Ordinal));
        var duplicate = entries
            .Select(x => new TestArchiveEntry(x.Name, x.Bytes.ToArray()))
            .ToList();
        duplicate.Add(new TestArchiveEntry(media.Name, media.Bytes.ToArray()));
        using (var duplicateArchive = await BuildArchiveAsync(duplicate))
        {
            await using var destination = await LocalPortableTestLibrary.CreateAsync();
            duplicateArchive.Position = 0;
            var action = () => destination.Portability().ImportAsync(duplicateArchive);
            var exception = await action.Should().ThrowAsync<PortableArchiveException>();
            exception.Which.Code.Should().Be("duplicate_path");
            (await destination.Db.Books.CountAsync()).Should().Be(0);
        }

        var traversal = entries
            .Select(x => new TestArchiveEntry(x.Name, x.Bytes.ToArray()))
            .ToList();
        traversal.Add(new TestArchiveEntry("../escape.txt", [1, 2, 3]));
        using var traversalArchive = await BuildArchiveAsync(traversal);
        await using var traversalDestination = await LocalPortableTestLibrary.CreateAsync();
        traversalArchive.Position = 0;
        var traversalAction = () =>
            traversalDestination.Portability().ImportAsync(traversalArchive);
        var traversalException =
            await traversalAction.Should().ThrowAsync<PortableArchiveException>();
        traversalException.Which.Code.Should().Be("unsafe_archive_path");
        (await traversalDestination.Db.Books.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("/absolute")]
    [InlineData("\\absolute")]
    [InlineData("C:\\absolute")]
    [InlineData("../escape")]
    [InlineData("./escape")]
    [InlineData("foo/../bar")]
    [InlineData("foo\\..\\bar")]
    [InlineData("foo//bar")]
    public async Task Import_rejects_hostile_entry_paths_before_manifest_lookup(string path)
    {
        using var archive = await BuildArchiveAsync(
            [new TestArchiveEntry(path, [1, 2, 3])]);
        await using var destination = await LocalPortableTestLibrary.CreateAsync();

        var action = () => destination.Portability().ImportAsync(archive);
        var exception = await action.Should().ThrowAsync<PortableArchiveException>();

        exception.Which.Code.Should().Be("unsafe_archive_path");
    }

    [Fact]
    public async Task Import_rejects_case_insensitive_duplicate_entry_paths()
    {
        using var archive = await BuildArchiveAsync(
        [
            new TestArchiveEntry("payload.bin", [1]),
            new TestArchiveEntry("PAYLOAD.BIN", [2]),
        ]);
        await using var destination = await LocalPortableTestLibrary.CreateAsync();

        var action = () => destination.Portability().ImportAsync(archive);
        var exception = await action.Should().ThrowAsync<PortableArchiveException>();

        exception.Which.Code.Should().Be("duplicate_path");
        exception.Which.Message.Should()
            .Be("Portable archive contains duplicate path 'PAYLOAD.BIN'.");
    }

    [Fact]
    public async Task Import_rejects_manifest_and_payload_data_version_disagreement()
    {
        using var archive = await ExportFixtureAsync();
        var entries = await ReadEntriesAsync(archive);
        MutateJsonEntry(entries, "manifest.json", root =>
        {
            root["dataVersion"] = 2;
        });
        using var mismatch = await BuildArchiveAsync(entries);
        await using var destination = await LocalPortableTestLibrary.CreateAsync();

        var action = () => destination.Portability().ImportAsync(mismatch);
        var exception = await action.Should().ThrowAsync<PortableArchiveException>();

        exception.Which.Code.Should().Be("data_version_mismatch");
        (await destination.Db.Books.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Import_rejects_duplicate_relationships_before_mutating_destination()
    {
        using var archive = await ExportFixtureAsync();
        var entries = await ReadEntriesAsync(archive);
        MutateJsonEntry(entries, "data/library.json", root =>
        {
            var writingNotes = root["writingNotes"]!.AsArray();
            writingNotes.Add(writingNotes[0]!.DeepClone());
        });
        RehashDataDescriptor(entries);
        using var duplicate = await BuildArchiveAsync(entries);
        await using var destination = await LocalPortableTestLibrary.CreateAsync();

        var action = () => destination.Portability().ImportAsync(duplicate);
        var exception = await action.Should().ThrowAsync<PortableArchiveException>();

        exception.Which.Code.Should().Be("duplicate_relationship");
        (await destination.Db.Books.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Import_requires_empty_destination()
    {
        using var archive = await ExportFixtureAsync();
        await using var destination = await LocalPortableTestLibrary.CreateAsync();
        destination.Db.Topics.Add(new TopicModel
        {
            Topic = "Existing user content",
        });
        await destination.Db.SaveChangesAsync();

        archive.Position = 0;
        var action = () => destination.Portability().ImportAsync(archive);
        var exception = await action.Should().ThrowAsync<PortableArchiveException>();

        exception.Which.Code.Should().Be("destination_not_empty");
        (await destination.Db.Books.CountAsync()).Should().Be(0);
        (await destination.Db.Topics.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Failed_media_write_rolls_back_relational_state_and_compensates_media()
    {
        using var archive = await ExportFixtureAsync();
        await using var destination = await LocalPortableTestLibrary.CreateAsync();
        var failingStorage = new FailAfterMediaWriteStorage(destination.Storage);
        var service = new PortableArchiveService(
            destination.Db,
            failingStorage,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PortableArchiveService>.Instance);

        archive.Position = 0;
        var action = () => service.ImportAsync(archive);
        var exception = await action.Should().ThrowAsync<PortableArchiveException>();

        exception.Which.Code.Should().Be("import_failed");
        destination.Db.ChangeTracker.Clear();
        (await destination.Db.Books.CountAsync()).Should().Be(0);
        (await destination.Db.Works.CountAsync()).Should().Be(0);
        (await destination.Db.Notes.CountAsync()).Should().Be(0);

        Directory.Exists(destination.Storage.StorageRoot).Should().BeTrue();
        Directory.EnumerateFiles(
                destination.Storage.StorageRoot,
                "*",
                SearchOption.AllDirectories)
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Import_rejects_suspicious_compression_before_mutating_destination()
    {
        using var bomb = new MemoryStream();
        using (var archive = new ZipArchive(
            bomb,
            ZipArchiveMode.Create,
            leaveOpen: true))
        {
            var entry = archive.CreateEntry(
                "payload.bin",
                CompressionLevel.Optimal);
            await using var output = entry.Open();
            var zeros = new byte[2 * 1024 * 1024];
            await output.WriteAsync(zeros);
        }

        bomb.Position = 0;
        await using var destination = await LocalPortableTestLibrary.CreateAsync();

        var action = () => destination.Portability().ImportAsync(bomb);
        var exception = await action.Should().ThrowAsync<PortableArchiveException>();

        exception.Which.Code.Should().Be("suspicious_compression");
        (await destination.Db.Books.CountAsync()).Should().Be(0);
        (await destination.Db.Notes.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Import_round_trips_v1_v2_v3_fixtures_with_media_hashes(
        int dataVersion)
    {
        using var exported = await ExportFixtureAsync();
        var entries = await ReadEntriesAsync(exported);
        MutateJsonEntry(entries, "manifest.json", root => root["dataVersion"] = dataVersion);
        MutateJsonEntry(entries, "data/library.json", root =>
        {
            root["version"] = dataVersion;
            if (dataVersion < 2)
            {
                root.Remove("writingNotes");
                root.Remove("noteImportBookLinks");
            }
            else if (dataVersion < 3)
            {
                root.Remove("noteImportBookLinks");
            }
        });
        RehashDataDescriptor(entries);

        var manifest = JsonNode.Parse(
            entries.Single(x => x.Name == "manifest.json").Bytes)!;
        var media = manifest["media"]!.AsArray();

        using var archive = await BuildArchiveAsync(entries);
        await using var destination = await LocalPortableTestLibrary.CreateAsync();

        var imported = await destination.Portability().ImportAsync(archive);

        imported.IntegrityVerified.Should().BeTrue();
        imported.FormatVersion.Should().Be(1);
        imported.MediaFiles.Should().Be(media.Count);

        destination.Db.ChangeTracker.Clear();
        (await destination.Db.Books.CountAsync()).Should().Be(4);
        (await destination.Db.Works.CountAsync()).Should().Be(3);
        (await destination.Db.Notes.CountAsync()).Should().Be(1);
        (await destination.Db.WritingNotes.CountAsync())
            .Should().Be(dataVersion >= 2 ? 1 : 0);

        foreach (var descriptor in media)
        {
            var bookId = Guid.Parse(descriptor!["bookId"]!.GetValue<string>());
            var kind = descriptor["kind"]!.GetValue<string>();
            var expectedLength = descriptor["length"]!.GetValue<long>();
            var expectedSha256 = descriptor["sha256"]!.GetValue<string>();

            await using var opened = kind == "book"
                ? await destination.Storage.OpenBookFileAsync(bookId)
                : await destination.Storage.OpenBookCoverAsync(bookId);
            opened.Should().NotBeNull();

            using var content = new MemoryStream();
            await opened!.Content.CopyToAsync(content);
            content.Length.Should().Be(expectedLength);
            Convert.ToHexString(SHA256.HashData(content.ToArray()))
                .ToLowerInvariant()
                .Should().Be(expectedSha256);
        }
    }

    [Fact]
    public async Task Import_scratch_holds_only_the_archive_and_local_staging_and_is_deleted()
    {
        var scratchRoot = Path.Combine(
            Path.GetTempPath(),
            $"nostos-s8-scratch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratchRoot);

        try
        {
            using var archive = await ExportFixtureAsync();
            await using var destination = await LocalPortableTestLibrary.CreateAsync();
            var inner = destination.Storage;
            var snapshots = new List<IReadOnlyList<string>>();
            var storage = new InterceptingStorage(
                inner,
                onBookFile: async (bookId, content, fileName, ct) =>
                {
                    snapshots.Add(SnapshotScratch(scratchRoot));
                    return await inner.SaveBookFileAsync(bookId, content, fileName, ct);
                });
            var service = new PortableArchiveService(
                destination.Db,
                storage,
                NullLogger<PortableArchiveService>.Instance)
            {
                ScratchRoot = scratchRoot,
            };

            var imported = await service.ImportAsync(archive);

            imported.IntegrityVerified.Should().BeTrue();
            imported.MediaFiles.Should().Be(5);
            snapshots.Should().NotBeEmpty();

            var separator = Path.DirectorySeparatorChar;
            foreach (var snapshot in snapshots)
            {
                snapshot.Should().Contain(path => path.EndsWith("archive.nostos", StringComparison.Ordinal));

                // The slice removes the whole-library extraction directory: media
                // may only exist as the archive spool plus the local staging copy.
                snapshot.Should().NotContain(path =>
                    path.Contains($"{separator}media-stage{separator}", StringComparison.Ordinal));
                snapshot.Count(path =>
                        path.Contains($"{separator}staging{separator}", StringComparison.Ordinal)
                        && path.Contains($"{separator}media{separator}", StringComparison.Ordinal)
                        && path.EndsWith(".bin", StringComparison.Ordinal))
                    .Should().Be(5);
                snapshot.Where(path =>
                        !path.EndsWith(".bin", StringComparison.Ordinal)
                        && !path.EndsWith(".json", StringComparison.Ordinal)
                        && !path.EndsWith("archive.nostos", StringComparison.Ordinal))
                    .Should().BeEmpty("no additional whole-media extraction copy may exist");
            }

            Directory.EnumerateFileSystemEntries(scratchRoot).Should().BeEmpty();
        }
        finally
        {
            try
            {
                if (Directory.Exists(scratchRoot))
                    Directory.Delete(scratchRoot, recursive: true);
            }
            catch
            {
                // Test cleanup only.
            }
        }
    }

    [Theory]
    [InlineData("invalid_archive", "invalid_zip")]
    [InlineData("missing_manifest", "missing_manifest")]
    [InlineData("data_checksum_mismatch", "data_checksum_mismatch")]
    [InlineData("media_checksum_mismatch", "media_checksum_mismatch")]
    [InlineData("destination_not_empty", "destination_not_empty")]
    [InlineData("asset_write_failure", "import_failed")]
    [InlineData("cancellation", "import_failed")]
    public async Task Import_failure_leaves_no_database_assets_or_scratch_state(
        string scenario,
        string expectedCode)
    {
        var scratchRoot = Path.Combine(
            Path.GetTempPath(),
            $"nostos-s8-scratch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratchRoot);

        try
        {
            var fixture = await ExportFixtureAsync();
            MemoryStream archive;
            switch (scenario)
            {
                case "invalid_archive":
                    archive = new MemoryStream(
                        Encoding.UTF8.GetBytes("this is not a portable archive"),
                        writable: false);
                    break;
                case "missing_manifest":
                {
                    var entries = await ReadEntriesAsync(fixture);
                    archive = await BuildArchiveAsync(
                        entries.Where(x => x.Name != "manifest.json"));
                    break;
                }
                case "data_checksum_mismatch":
                {
                    var entries = await ReadEntriesAsync(fixture);
                    MutateJsonEntry(entries, "data/library.json", root =>
                        root["works"]!.AsArray()[0]!["title"] = "Tampered after hashing");
                    var dataBytes = entries
                        .Single(x => x.Name == "data/library.json")
                        .Bytes;
                    MutateJsonEntry(entries, "manifest.json", root =>
                        root["data"]!["length"] = dataBytes.LongLength);
                    archive = await BuildArchiveAsync(entries);
                    break;
                }
                case "media_checksum_mismatch":
                {
                    var entries = await ReadEntriesAsync(fixture);
                    var index = entries.FindIndex(x =>
                        x.Name.StartsWith("media/books/", StringComparison.Ordinal));
                    var corrupted = new byte[entries[index].Bytes.Length];
                    new Random(2026).NextBytes(corrupted);
                    entries[index] = new TestArchiveEntry(
                        entries[index].Name,
                        corrupted);
                    archive = await BuildArchiveAsync(entries);
                    break;
                }
                default:
                    archive = fixture;
                    break;
            }

            using (archive)
            {
                await using var destination = await LocalPortableTestLibrary.CreateAsync();
                if (scenario == "destination_not_empty")
                {
                    destination.Db.Topics.Add(new TopicModel { Topic = "existing" });
                    await destination.Db.SaveChangesAsync();
                }

                var inner = destination.Storage;
                IBookAssetStorage storage = inner;
                CancellationTokenSource? cancellation = null;

                if (scenario is "asset_write_failure" or "cancellation")
                {
                    cancellation = scenario == "cancellation"
                        ? new CancellationTokenSource()
                        : null;
                    var saveCalls = 0;
                    storage = new InterceptingStorage(
                        inner,
                        onBookFile: async (bookId, content, fileName, ct) =>
                        {
                            var call = Interlocked.Increment(ref saveCalls);
                            if (scenario == "cancellation" && call >= 2)
                            {
                                cancellation!.Cancel();
                                return await inner.SaveBookFileAsync(
                                    bookId,
                                    content,
                                    fileName,
                                    cancellation.Token);
                            }

                            var saved = await inner.SaveBookFileAsync(
                                bookId,
                                content,
                                fileName,
                                ct);
                            if (scenario == "asset_write_failure")
                            {
                                throw new IOException(
                                    "Injected failure after durable media write.");
                            }

                            return saved;
                        });
                }

                using (cancellation)
                {
                    var service = new PortableArchiveService(
                        destination.Db,
                        storage,
                        NullLogger<PortableArchiveService>.Instance)
                    {
                        ScratchRoot = scratchRoot,
                    };

                    var action = () => service.ImportAsync(
                        archive,
                        cancellation?.Token ?? CancellationToken.None);
                    var exception = await action.Should().ThrowAsync<PortableArchiveException>();
                    exception.Which.Code.Should().Be(expectedCode);
                }

                destination.Db.ChangeTracker.Clear();
                (await destination.Db.Books.CountAsync()).Should().Be(0);
                (await destination.Db.Works.CountAsync()).Should().Be(0);
                (await destination.Db.Notes.CountAsync()).Should().Be(0);

                if (scenario == "destination_not_empty")
                {
                    (await destination.Db.Topics.CountAsync()).Should().Be(
                        1,
                        "existing destination content must be untouched");
                }

                Directory.EnumerateFiles(
                        destination.Storage.StorageRoot,
                        "*",
                        SearchOption.AllDirectories)
                    .Should().BeEmpty();
                Directory.EnumerateFileSystemEntries(scratchRoot).Should().BeEmpty();
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(scratchRoot))
                    Directory.Delete(scratchRoot, recursive: true);
            }
            catch
            {
                // Test cleanup only.
            }
        }
    }

    [Fact]
    public async Task Import_rechecks_empty_destination_inside_the_final_transaction()
    {
        using var archive = await ExportFixtureAsync();
        await using var destination = await LocalPortableTestLibrary.CreateAsync();

        // The reader clocks the prepared metadata immediately before committing
        // staging; add a competing row at that moment so the transaction's
        // empty-destination recheck (plan 9.14) is what rejects the import.
        var race = new CallbackTimeProvider(() =>
        {
            destination.Db.Topics.Add(new TopicModel { Topic = "concurrent writer" });
            destination.Db.SaveChanges();
        });
        var service = new PortableArchiveService(
            destination.Db,
            destination.Storage,
            NullLogger<PortableArchiveService>.Instance,
            timeProvider: race);

        var action = () => service.ImportAsync(archive);
        var exception = await action.Should().ThrowAsync<PortableArchiveException>();

        exception.Which.Code.Should().Be("destination_not_empty");
        (await destination.Db.Books.CountAsync()).Should().Be(0);
        (await destination.Db.Topics.CountAsync()).Should().Be(1);
        Directory.EnumerateFiles(
                destination.Storage.StorageRoot,
                "*",
                SearchOption.AllDirectories)
            .Should().BeEmpty();
    }

    private static IReadOnlyList<string> SnapshotScratch(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToArray()
            : [];

    private static async Task<MemoryStream> ExportFixtureAsync()
    {
        await using var source = await LocalPortableTestLibrary.CreateAsync();
        await PortableArchiveTestSupport.PopulateRepresentativeAsync(
            source.Db,
            source.Storage);

        var archive = new MemoryStream();
        await source.Portability().ExportAsync(archive);
        archive.Position = 0;
        return archive;
    }

    private static async Task<List<TestArchiveEntry>> ReadEntriesAsync(
        MemoryStream source)
    {
        source.Position = 0;
        var entries = new List<TestArchiveEntry>();

        using var archive = new ZipArchive(
            source,
            ZipArchiveMode.Read,
            leaveOpen: true);
        foreach (var entry in archive.Entries)
        {
            await using var input = entry.Open();
            using var output = new MemoryStream();
            await input.CopyToAsync(output);
            entries.Add(new TestArchiveEntry(entry.FullName, output.ToArray()));
        }

        source.Position = 0;
        return entries;
    }

    private static Task<MemoryStream> BuildArchiveAsync(
        IEnumerable<TestArchiveEntry> entries)
    {
        var output = new MemoryStream();
        using (var archive = new ZipArchive(
            output,
            ZipArchiveMode.Create,
            leaveOpen: true))
        {
            foreach (var item in entries)
            {
                var entry = archive.CreateEntry(
                    item.Name,
                    CompressionLevel.NoCompression);
                using var target = entry.Open();
                target.Write(item.Bytes);
            }
        }

        output.Position = 0;
        return Task.FromResult(output);
    }

    private static void MutateJsonEntry(
        List<TestArchiveEntry> entries,
        string name,
        Action<JsonObject> mutate)
    {
        var index = entries.FindIndex(x => x.Name == name);
        index.Should().BeGreaterThanOrEqualTo(0);
        var root = JsonNode.Parse(entries[index].Bytes)!.AsObject();
        mutate(root);
        entries[index] = new TestArchiveEntry(
            name,
            Encoding.UTF8.GetBytes(root.ToJsonString()));
    }

    private static void RehashDataDescriptor(
        List<TestArchiveEntry> entries)
    {
        var data = entries.Single(x => x.Name == "data/library.json").Bytes;
        var manifestIndex = entries.FindIndex(x => x.Name == "manifest.json");
        var manifest = JsonNode.Parse(entries[manifestIndex].Bytes)!.AsObject();
        var dataNode = manifest["data"]!.AsObject();
        dataNode["length"] = data.LongLength;
        dataNode["sha256"] = Convert.ToHexString(
            SHA256.HashData(data)).ToLowerInvariant();
        entries[manifestIndex] = new TestArchiveEntry(
            "manifest.json",
            Encoding.UTF8.GetBytes(manifest.ToJsonString()));
    }

    private sealed record TestArchiveEntry(string Name, byte[] Bytes);

    private sealed class FailAfterMediaWriteStorage(IBookAssetStorage inner)
        : IBookAssetStorage
    {
        public Task<string> SaveBookFileAsync(
            Guid bookId,
            Stream content,
            string fileName,
            CancellationToken ct = default) =>
            WriteThenFail(() => inner.SaveBookFileAsync(
                bookId,
                content,
                fileName,
                ct));

        public Task<string> AdoptBookFileAsync(
            Guid bookId,
            string sourcePath,
            string fileName,
            CancellationToken ct = default) =>
            inner.AdoptBookFileAsync(bookId, sourcePath, fileName, ct);

        public Task<StoredAssetInfo?> GetBookFileInfoAsync(
            Guid bookId,
            CancellationToken ct = default) =>
            inner.GetBookFileInfoAsync(bookId, ct);

        public Task<StoredAssetRead?> OpenBookFileAsync(
            Guid bookId,
            StorageByteRange? range = null,
            CancellationToken ct = default) =>
            inner.OpenBookFileAsync(bookId, range, ct);

        public Task<bool> DeleteBookFileAsync(
            Guid bookId,
            CancellationToken ct = default) =>
            inner.DeleteBookFileAsync(bookId, ct);

        public Task DeleteBookFilesAsync(
            Guid bookId,
            CancellationToken ct = default) =>
            inner.DeleteBookFilesAsync(bookId, ct);

        public Task<string> SaveBookCoverAsync(
            Guid bookId,
            Stream content,
            string fileName,
            CancellationToken ct = default) =>
            WriteThenFail(() => inner.SaveBookCoverAsync(
                bookId,
                content,
                fileName,
                ct));

        public Task<StoredAssetInfo?> GetBookCoverInfoAsync(
            Guid bookId,
            CancellationToken ct = default) =>
            inner.GetBookCoverInfoAsync(bookId, ct);

        public Task<StoredAssetRead?> OpenBookCoverAsync(
            Guid bookId,
            CancellationToken ct = default) =>
            inner.OpenBookCoverAsync(bookId, ct);

        public Task<StoredAssetInfo?> GetBookCoverThumbnailInfoAsync(
            Guid bookId,
            int width,
            CancellationToken ct = default) =>
            inner.GetBookCoverThumbnailInfoAsync(bookId, width, ct);

        public Task<StoredAssetRead?> OpenBookCoverThumbnailAsync(
            Guid bookId,
            int width,
            CancellationToken ct = default) =>
            inner.OpenBookCoverThumbnailAsync(bookId, width, ct);

        public Task<bool> DeleteCoverAsync(
            Guid bookId,
            CancellationToken ct = default) =>
            inner.DeleteCoverAsync(bookId, ct);

        private static async Task<string> WriteThenFail(Func<Task<string>> write)
        {
            await write();
            throw new IOException("Injected failure after durable media write.");
        }
    }

    private sealed class SyncIoForbiddenStream : Stream
    {
        private readonly MemoryStream _inner = new();

        public byte[] WrittenBytes => _inner.ToArray();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw SyncIoDisallowed();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw SyncIoDisallowed();

        public override void Write(ReadOnlySpan<byte> buffer) =>
            throw SyncIoDisallowed();

        public override void WriteByte(byte value) => throw SyncIoDisallowed();

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            _inner.WriteAsync(buffer, offset, count, cancellationToken);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _inner.WriteAsync(buffer, cancellationToken);

        private static InvalidOperationException SyncIoDisallowed() =>
            new(
                "Synchronous operations are disallowed. "
                + "Call WriteAsync or set AllowSynchronousIO to true instead.");
    }

    private sealed class InterceptingStorage(
        IBookAssetStorage inner,
        Func<Guid, Stream, string, CancellationToken, Task<string>>? onBookFile = null,
        Func<Guid, Stream, string, CancellationToken, Task<string>>? onCoverFile = null)
        : IBookAssetStorage
    {
        public Task<string> SaveBookFileAsync(
            Guid bookId,
            Stream content,
            string fileName,
            CancellationToken ct = default) =>
            onBookFile is null
                ? inner.SaveBookFileAsync(bookId, content, fileName, ct)
                : onBookFile(bookId, content, fileName, ct);

        public Task<string> AdoptBookFileAsync(
            Guid bookId,
            string sourcePath,
            string fileName,
            CancellationToken ct = default) =>
            inner.AdoptBookFileAsync(bookId, sourcePath, fileName, ct);

        public Task<StoredAssetInfo?> GetBookFileInfoAsync(
            Guid bookId,
            CancellationToken ct = default) =>
            inner.GetBookFileInfoAsync(bookId, ct);

        public Task<StoredAssetRead?> OpenBookFileAsync(
            Guid bookId,
            StorageByteRange? range = null,
            CancellationToken ct = default) =>
            inner.OpenBookFileAsync(bookId, range, ct);

        public Task<bool> DeleteBookFileAsync(
            Guid bookId,
            CancellationToken ct = default) =>
            inner.DeleteBookFileAsync(bookId, ct);

        public Task DeleteBookFilesAsync(
            Guid bookId,
            CancellationToken ct = default) =>
            inner.DeleteBookFilesAsync(bookId, ct);

        public Task<string> SaveBookCoverAsync(
            Guid bookId,
            Stream content,
            string fileName,
            CancellationToken ct = default) =>
            onCoverFile is null
                ? inner.SaveBookCoverAsync(bookId, content, fileName, ct)
                : onCoverFile(bookId, content, fileName, ct);

        public Task<StoredAssetInfo?> GetBookCoverInfoAsync(
            Guid bookId,
            CancellationToken ct = default) =>
            inner.GetBookCoverInfoAsync(bookId, ct);

        public Task<StoredAssetRead?> OpenBookCoverAsync(
            Guid bookId,
            CancellationToken ct = default) =>
            inner.OpenBookCoverAsync(bookId, ct);

        public Task<StoredAssetInfo?> GetBookCoverThumbnailInfoAsync(
            Guid bookId,
            int width,
            CancellationToken ct = default) =>
            inner.GetBookCoverThumbnailInfoAsync(bookId, width, ct);

        public Task<StoredAssetRead?> OpenBookCoverThumbnailAsync(
            Guid bookId,
            int width,
            CancellationToken ct = default) =>
            inner.OpenBookCoverThumbnailAsync(bookId, width, ct);

        public Task<bool> DeleteCoverAsync(
            Guid bookId,
            CancellationToken ct = default) =>
            inner.DeleteCoverAsync(bookId, ct);
    }

    private sealed class CallbackTimeProvider(Action onFirstUtcNow) : TimeProvider
    {
        private int _called;

        public override DateTimeOffset GetUtcNow()
        {
            if (Interlocked.Exchange(ref _called, 1) == 0)
                onFirstUtcNow();

            return DateTimeOffset.UtcNow;
        }
    }

    private sealed class RecordingBookTextScheduler : IBookTextIngestionScheduler
    {
        public List<(Guid BookId, string FileName)> Scheduled { get; } = [];

        public Task ScheduleAsync(
            Guid bookId,
            string sourceFileName,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Scheduled.Add((bookId, sourceFileName));
            return Task.CompletedTask;
        }
    }

}

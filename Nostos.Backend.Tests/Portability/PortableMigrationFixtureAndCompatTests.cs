using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Portability;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

public sealed class PortableMigrationFixtureAndCompatTests
{
    private const string ManifestPath = "manifest.json";
    private const string DataPath = "data/library.json";
    private const string PortableFormat = "nostos-portable";
    private const int SupportedFormatVersion = 1;
    private const int LegacyDataVersion = 1;
    private const int CurrentDataVersion = 2;

    [Fact]
    public async Task Generated_legacy_data_v1_fixture_is_redistributable_and_contains_no_writing_notes()
    {
        using var fixture = await CreateFixtureAsync(
            "portable-format1-data1.nostos",
            LegacyDataVersion);

        fixture.Name.Should().Be("portable-format1-data1.nostos");
        fixture.Bytes.Should().NotBeEmpty();

        using var archive = OpenArchive(fixture.Bytes);
        var manifest = await ReadJsonObjectAsync(archive, ManifestPath);
        var data = await ReadJsonObjectAsync(archive, DataPath);

        manifest["format"]!.GetValue<string>().Should().Be(PortableFormat);
        manifest["formatVersion"]!.GetValue<int>().Should().Be(SupportedFormatVersion);
        manifest["dataVersion"]!.GetValue<int>().Should().Be(LegacyDataVersion);

        data["version"]!.GetValue<int>().Should().Be(LegacyDataVersion);
        data.ContainsKey("writingNotes").Should().BeFalse(
            "DataVersion 1 predates WritingNotes membership relationships");

        AssertFixtureIsRedistributable(fixture.Bytes);
        AssertDataDescriptorMatchesPayload(manifest, data);
    }

    [Fact]
    public async Task Generated_legacy_data_v2_fixture_is_redistributable_and_contains_writing_notes()
    {
        using var fixture = await CreateFixtureAsync(
            "portable-format1-data2.nostos",
            CurrentDataVersion);

        fixture.Name.Should().Be("portable-format1-data2.nostos");
        fixture.Bytes.Should().NotBeEmpty();

        using var archive = OpenArchive(fixture.Bytes);
        var manifest = await ReadJsonObjectAsync(archive, ManifestPath);
        var data = await ReadJsonObjectAsync(archive, DataPath);

        manifest["format"]!.GetValue<string>().Should().Be(PortableFormat);
        manifest["formatVersion"]!.GetValue<int>().Should().Be(SupportedFormatVersion);
        manifest["dataVersion"]!.GetValue<int>().Should().Be(CurrentDataVersion);

        data["version"]!.GetValue<int>().Should().Be(CurrentDataVersion);
        data["writingNotes"].Should().BeOfType<JsonArray>();
        data["writingNotes"]!.AsArray().Should().ContainSingle();

        AssertFixtureIsRedistributable(fixture.Bytes);
        AssertDataDescriptorMatchesPayload(manifest, data);
    }

    [Fact]
    public async Task DataVersion_1_archive_imports_without_writing_notes_and_preserves_older_user_data()
    {
        using var fixture = await CreateFixtureAsync(
            "portable-format1-data1.nostos",
            LegacyDataVersion);

        await using var destination = await LocalPortableTestLibrary.CreateAsync();
        using var archive = new MemoryStream(fixture.Bytes, writable: false);

        var imported = await destination.Portability().ImportAsync(archive);

        imported.FormatVersion.Should().Be(SupportedFormatVersion);
        imported.IntegrityVerified.Should().BeTrue();

        destination.Db.ChangeTracker.Clear();

        (await destination.Db.Works.CountAsync()).Should().Be(3);
        (await destination.Db.Books.CountAsync()).Should().Be(4);
        (await destination.Db.Collections.CountAsync()).Should().Be(2);
        (await destination.Db.BookCollections.CountAsync()).Should().Be(3);
        (await destination.Db.Notes.CountAsync()).Should().Be(1);
        (await destination.Db.Topics.CountAsync()).Should().Be(1);
        (await destination.Db.NoteTopics.CountAsync()).Should().Be(1);
        (await destination.Db.Writings.CountAsync()).Should().Be(2);
        (await destination.Db.BookAcquisitions.CountAsync()).Should().Be(1);

        (await destination.Db.WritingNotes.CountAsync()).Should().Be(
            0,
            "DataVersion 1 did not contain WritingNotes and the missing field must default safely to an empty relationship set");

        var note = await destination.Db.Notes
            .AsNoTracking()
            .SingleAsync();

        note.Content.Should().Be("Processed thought");
        note.RawContent.Should().Be("raw thought");
        note.SelectedText.Should().Be("selected passage");
        note.CfiRange.Should().Be("epubcfi(/6/4!/4/2)");
        note.SourceAnchorKind.Should().Be("epub_cfi");
        note.SourceAnchorValue.Should().Be("epubcfi(/6/4!/4/2)");
        note.AnchorVerified.Should().BeTrue();

        var writing = await destination.Db.Writings
            .AsNoTracking()
            .SingleAsync(x => x.Type == WritingType.Document);

        writing.Name.Should().Be("Portable draft");
        writing.Content.Should().Be("<p>User-owned studio prose.</p>");
        writing.ParentId.Should().NotBeNull();

        var epub = await destination.Db.Books
            .AsNoTracking()
            .OfType<EBookModel>()
            .SingleAsync(x => x.Title == "Portable EPUB");

        epub.Progress.LastLocation.Should().Be("epubcfi(/6/4)");
        epub.Progress.ProgressPercent.Should().Be(42);
        epub.Progress.Rating.Should().Be(5);
        epub.Progress.IsFavorite.Should().BeTrue();
        epub.Progress.PersonalReview.Should().Be("Important review");
    }

    [Fact]
    public async Task DataVersion_2_archive_imports_with_writing_notes_preserved()
    {
        using var fixture = await CreateFixtureAsync(
            "portable-format1-data2.nostos",
            CurrentDataVersion);

        await using var destination = await LocalPortableTestLibrary.CreateAsync();
        using var archive = new MemoryStream(fixture.Bytes, writable: false);

        var imported = await destination.Portability().ImportAsync(archive);

        imported.FormatVersion.Should().Be(SupportedFormatVersion);
        imported.IntegrityVerified.Should().BeTrue();

        destination.Db.ChangeTracker.Clear();

        (await destination.Db.Writings.CountAsync()).Should().Be(2);
        (await destination.Db.Notes.CountAsync()).Should().Be(1);
        (await destination.Db.WritingNotes.CountAsync()).Should().Be(1);

        var relationship = await destination.Db.WritingNotes
            .AsNoTracking()
            .SingleAsync();

        var writing = await destination.Db.Writings
            .AsNoTracking()
            .SingleAsync(x => x.Id == relationship.WritingId);

        var note = await destination.Db.Notes
            .AsNoTracking()
            .SingleAsync(x => x.Id == relationship.NoteId);

        writing.Name.Should().Be("Portable draft");
        note.Content.Should().Be("Processed thought");
        relationship.AddedAt.Should().NotBe(default);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(999)]
    public async Task Unsupported_data_versions_are_rejected_with_typed_exception(
        int dataVersion)
    {
        using var fixture = await CreateFixtureAsync(
            "portable-format1-data2.nostos",
            CurrentDataVersion);

        var entries = ReadEntries(fixture.Bytes);

        MutateJsonEntry(entries, ManifestPath, root =>
        {
            root["dataVersion"] = dataVersion;
        });

        MutateJsonEntry(entries, DataPath, root =>
        {
            root["version"] = dataVersion;
        });

        RehashDataDescriptor(entries);

        using var invalidArchive = BuildArchive(entries);
        await using var destination = await LocalPortableTestLibrary.CreateAsync();

        var action = () => destination.Portability().ImportAsync(invalidArchive);

        var thrown = await action.Should().ThrowAsync<PortableArchiveException>();
        thrown.Which.Code.Should().Be("unsupported_data_version");

        (await destination.Db.Books.CountAsync()).Should().Be(0);
        (await destination.Db.Notes.CountAsync()).Should().Be(0);
        (await destination.Db.Writings.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Unsupported_format_version_is_rejected_with_typed_exception()
    {
        using var fixture = await CreateFixtureAsync(
            "portable-format1-data2.nostos",
            CurrentDataVersion);

        var entries = ReadEntries(fixture.Bytes);

        MutateJsonEntry(entries, ManifestPath, root =>
        {
            root["formatVersion"] = SupportedFormatVersion + 1;
        });

        using var invalidArchive = BuildArchive(entries);
        await using var destination = await LocalPortableTestLibrary.CreateAsync();

        var action = () => destination.Portability().ImportAsync(invalidArchive);

        var thrown = await action.Should().ThrowAsync<PortableArchiveException>();
        thrown.Which.Code.Should().Be("unsupported_version");

        (await destination.Db.Books.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Data_hash_mismatch_is_rejected_with_typed_exception_before_mutation()
    {
        using var fixture = await CreateFixtureAsync(
            "portable-format1-data2.nostos",
            CurrentDataVersion);

        var entries = ReadEntries(fixture.Bytes);

        MutateJsonEntry(entries, DataPath, root =>
        {
            root["works"]!.AsArray()[0]!["title"] = "Tampered after hashing";
        });

        // Update length so it tests checksum mismatch specifically.
        var manifestIndex = entries.FindIndex(x =>
            string.Equals(x.Name, ManifestPath, StringComparison.Ordinal));
        var dataBytes = entries.Single(x =>
            string.Equals(x.Name, DataPath, StringComparison.Ordinal)).Bytes;
        var manifestObj = JsonNode.Parse(entries[manifestIndex].Bytes)!.AsObject();
        manifestObj["data"]!["length"] = dataBytes.LongLength;
        entries[manifestIndex] = new TestArchiveEntry(
            ManifestPath,
            Encoding.UTF8.GetBytes(manifestObj.ToJsonString()));

        // Deliberately do not update the manifest data hash.
        using var corruptArchive = BuildArchive(entries);
        await using var destination = await LocalPortableTestLibrary.CreateAsync();

        var action = () => destination.Portability().ImportAsync(corruptArchive);

        var thrown = await action.Should().ThrowAsync<PortableArchiveException>();
        thrown.Which.Code.Should().Be("data_checksum_mismatch");

        (await destination.Db.Works.CountAsync()).Should().Be(0);
        (await destination.Db.Books.CountAsync()).Should().Be(0);
        (await destination.Db.Notes.CountAsync()).Should().Be(0);
        (await destination.Db.Writings.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Preflight_accepts_compatible_archive_for_empty_library()
    {
        using var fixture = await CreateFixtureAsync(
            "portable-format1-data2.nostos",
            CurrentDataVersion);

        await using var destination = await LocalPortableTestLibrary.CreateAsync();

        var result = await PreflightAsync(destination, fixture.Bytes);

        result.IsCompatible.Should().BeTrue();
        result.DestinationIsEmpty.Should().BeTrue();
        result.RequiresReplacementConfirmation.Should().BeFalse();
        result.IncompatibilityCode.Should().BeNull();

        result.Format.Should().Be(PortableFormat);
        result.FormatVersion.Should().Be(SupportedFormatVersion);
        result.DataVersion.Should().Be(CurrentDataVersion);

        result.Counts.Books.Should().Be(4);
        result.Counts.Notes.Should().Be(1);
        result.Counts.Writings.Should().Be(2);

        result.MediaFiles.Should().Be(5);
        result.MediaBytes.Should().BeGreaterThan(0);
        result.MaxSingleEntryBytes.Should().BeGreaterThan(0);

        result.DestinationRevision.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Preflight_marks_populated_library_as_requiring_explicit_replacement()
    {
        using var fixture = await CreateFixtureAsync(
            "portable-format1-data2.nostos",
            CurrentDataVersion);

        await using var destination = await LocalPortableTestLibrary.CreateAsync();

        destination.Db.Topics.Add(new TopicModel
        {
            Topic = "Existing destination content",
        });
        await destination.Db.SaveChangesAsync();

        var result = await PreflightAsync(destination, fixture.Bytes);

        result.IsCompatible.Should().BeTrue();
        result.DestinationIsEmpty.Should().BeFalse();
        result.RequiresReplacementConfirmation.Should().BeTrue();
        result.IncompatibilityCode.Should().BeNull();

        result.ExistingCounts.Topics.Should().Be(1);
        result.ExistingCounts.TotalUserOwnedRows.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Destination_revision_changes_when_destination_user_data_changes()
    {
        using var fixture = await CreateFixtureAsync(
            "portable-format1-data2.nostos",
            CurrentDataVersion);

        await using var destination = await LocalPortableTestLibrary.CreateAsync();

        var before = await PreflightAsync(destination, fixture.Bytes);

        destination.Db.Topics.Add(new TopicModel
        {
            Id = Guid.Parse("68b40886-92fc-40a5-b293-6a77dc78c411"),
            Topic = "Destination revision marker",
        });
        await destination.Db.SaveChangesAsync();

        var after = await PreflightAsync(destination, fixture.Bytes);

        before.DestinationRevision.Should().NotBeNullOrWhiteSpace();
        after.DestinationRevision.Should().NotBeNullOrWhiteSpace();
        after.DestinationRevision.Should().NotBe(before.DestinationRevision);

        before.DestinationIsEmpty.Should().BeTrue();
        after.DestinationIsEmpty.Should().BeFalse();
        after.RequiresReplacementConfirmation.Should().BeTrue();
    }

    [Theory]
    [InlineData(2, CurrentDataVersion, "unsupported_format_version")]
    [InlineData(SupportedFormatVersion, 0, "unsupported_data_version")]
    [InlineData(SupportedFormatVersion, 999, "unsupported_data_version")]
    public async Task Preflight_detects_incompatible_archive_versions_without_mutating_destination(
        int formatVersion,
        int dataVersion,
        string expectedCode)
    {
        using var fixture = await CreateFixtureAsync(
            "portable-format1-data2.nostos",
            CurrentDataVersion);

        var entries = ReadEntries(fixture.Bytes);

        MutateJsonEntry(entries, ManifestPath, root =>
        {
            root["formatVersion"] = formatVersion;
            root["dataVersion"] = dataVersion;
        });

        MutateJsonEntry(entries, DataPath, root =>
        {
            root["version"] = dataVersion;
        });

        RehashDataDescriptor(entries);

        using var incompatible = BuildArchive(entries);
        var bytes = incompatible.ToArray();

        await using var destination = await LocalPortableTestLibrary.CreateAsync();

        var result = await PreflightAsync(destination, bytes);

        result.IsCompatible.Should().BeFalse();
        result.IncompatibilityCode.Should().Be(expectedCode);
        result.DestinationIsEmpty.Should().BeTrue();
        result.RequiresReplacementConfirmation.Should().BeFalse();

        (await destination.Db.Books.CountAsync()).Should().Be(0);
        (await destination.Db.Notes.CountAsync()).Should().Be(0);
        (await destination.Db.Writings.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Fixture_generation_is_stable_at_the_contract_level_and_can_be_written_as_redistributable_files()
    {
        using var v1 = await CreateFixtureAsync(
            "portable-format1-data1.nostos",
            LegacyDataVersion);

        using var v2 = await CreateFixtureAsync(
            "portable-format1-data2.nostos",
            CurrentDataVersion);

        var root = Path.Combine(
            Path.GetTempPath(),
            $"nostos-portable-fixtures-{Guid.NewGuid():N}");

        Directory.CreateDirectory(root);

        try
        {
            var v1Path = Path.Combine(root, v1.Name);
            var v2Path = Path.Combine(root, v2.Name);

            await File.WriteAllBytesAsync(v1Path, v1.Bytes);
            await File.WriteAllBytesAsync(v2Path, v2.Bytes);

            File.Exists(v1Path).Should().BeTrue();
            File.Exists(v2Path).Should().BeTrue();

            var persistedV1 = await File.ReadAllBytesAsync(v1Path);
            var persistedV2 = await File.ReadAllBytesAsync(v2Path);

            AssertFixtureIsRedistributable(persistedV1);
            AssertFixtureIsRedistributable(persistedV2);

            await AssertManifestVersionAsync(
                persistedV1,
                SupportedFormatVersion,
                LegacyDataVersion);

            await AssertManifestVersionAsync(
                persistedV2,
                SupportedFormatVersion,
                CurrentDataVersion);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<SyntheticFixture> CreateFixtureAsync(
        string name,
        int dataVersion)
    {
        if (dataVersion is not (LegacyDataVersion or CurrentDataVersion))
            throw new ArgumentOutOfRangeException(nameof(dataVersion));

        await using var source = await LocalPortableTestLibrary.CreateAsync();
        await PortableArchiveTestSupport.PopulateRepresentativeAsync(
            source.Db,
            source.Storage);

        using var exported = new MemoryStream();
        await source.Portability().ExportAsync(exported);

        var entries = ReadEntries(exported.ToArray());

        if (dataVersion == LegacyDataVersion)
        {
            MutateJsonEntry(entries, ManifestPath, root =>
            {
                root["formatVersion"] = SupportedFormatVersion;
                root["dataVersion"] = LegacyDataVersion;
            });

            MutateJsonEntry(entries, DataPath, root =>
            {
                root["version"] = LegacyDataVersion;
                root.Remove("writingNotes");
            });

            RehashDataDescriptor(entries);
        }
        else
        {
            MutateJsonEntry(entries, ManifestPath, root =>
            {
                root["formatVersion"] = SupportedFormatVersion;
                root["dataVersion"] = CurrentDataVersion;
            });

            MutateJsonEntry(entries, DataPath, root =>
            {
                root["version"] = CurrentDataVersion;
            });

            RehashDataDescriptor(entries);
        }

        using var rebuilt = BuildArchive(entries);
        return new SyntheticFixture(name, rebuilt.ToArray());
    }

    private static async Task<PreflightProbeResult> PreflightAsync(
        LocalPortableTestLibrary destination,
        byte[] archiveBytes)
    {
        using var archive = OpenArchive(archiveBytes);
        var manifest = await ReadJsonObjectAsync(archive, ManifestPath);

        var format = manifest["format"]?.GetValue<string>();
        var formatVersion = manifest["formatVersion"]?.GetValue<int>() ?? 0;
        var dataVersion = manifest["dataVersion"]?.GetValue<int>() ?? 0;

        var compatibleFormat =
            string.Equals(format, PortableFormat, StringComparison.Ordinal)
            && formatVersion == SupportedFormatVersion;

        var compatibleDataVersion =
            dataVersion is LegacyDataVersion or CurrentDataVersion;

        var incompatibilityCode = !compatibleFormat
            ? "unsupported_format_version"
            : !compatibleDataVersion
                ? "unsupported_data_version"
                : null;

        var countsNode = manifest["counts"]?.AsObject()
            ?? throw new InvalidDataException("Portable manifest is missing counts.");

        var media = manifest["media"]?.AsArray()
            ?? throw new InvalidDataException("Portable manifest is missing media.");

        var counts = new FixtureArchiveCounts(
            Works: ReadInt(countsNode, "works"),
            Books: ReadInt(countsNode, "books"),
            Collections: ReadInt(countsNode, "collections"),
            BookCollections: ReadInt(countsNode, "bookCollections"),
            Notes: ReadInt(countsNode, "notes"),
            Topics: ReadInt(countsNode, "topics"),
            NoteTopics: ReadInt(countsNode, "noteTopics"),
            Writings: ReadInt(countsNode, "writings"),
            BookAcquisitions: ReadInt(countsNode, "bookAcquisitions"));

        var mediaLengths = media
            .Select(x => x?["length"]?.GetValue<long>() ?? 0)
            .ToArray();

        var existingCounts = await ReadDestinationCountsAsync(destination);

        return new PreflightProbeResult(
            IsCompatible: incompatibilityCode is null,
            IncompatibilityCode: incompatibilityCode,
            Format: format,
            FormatVersion: formatVersion,
            DataVersion: dataVersion,
            Counts: counts,
            MediaFiles: media.Count,
            MediaBytes: mediaLengths.Sum(),
            MaxSingleEntryBytes: mediaLengths.DefaultIfEmpty(0).Max(),
            DestinationIsEmpty: existingCounts.TotalUserOwnedRows == 0,
            RequiresReplacementConfirmation:
                incompatibilityCode is null
                && existingCounts.TotalUserOwnedRows != 0,
            DestinationRevision: await ComputeDestinationRevisionAsync(destination),
            ExistingCounts: existingCounts);
    }

    private static async Task<DestinationCounts> ReadDestinationCountsAsync(
        LocalPortableTestLibrary destination)
    {
        var db = destination.Db;

        return new DestinationCounts(
            Works: await db.Works.CountAsync(),
            Books: await db.Books.CountAsync(),
            Collections: await db.Collections.CountAsync(),
            BookCollections: await db.BookCollections.CountAsync(),
            Notes: await db.Notes.CountAsync(),
            Topics: await db.Topics.CountAsync(),
            NoteTopics: await db.NoteTopics.CountAsync(),
            Writings: await db.Writings.CountAsync(),
            WritingNotes: await db.WritingNotes.CountAsync(),
            BookAcquisitions: await db.BookAcquisitions.CountAsync());
    }

    private static async Task<string> ComputeDestinationRevisionAsync(
        LocalPortableTestLibrary destination)
    {
        var db = destination.Db;

        var parts = new List<string>();

        parts.AddRange(
            await db.Works
                .AsNoTracking()
                .OrderBy(x => x.Id)
                .Select(x => $"work:{x.Id:N}")
                .ToListAsync());

        parts.AddRange(
            await db.Books
                .AsNoTracking()
                .OrderBy(x => x.Id)
                .Select(x => $"book:{x.Id:N}")
                .ToListAsync());

        parts.AddRange(
            await db.Collections
                .AsNoTracking()
                .OrderBy(x => x.Id)
                .Select(x => $"collection:{x.Id:N}")
                .ToListAsync());

        parts.AddRange(
            await db.Notes
                .AsNoTracking()
                .OrderBy(x => x.Id)
                .Select(x => $"note:{x.Id:N}")
                .ToListAsync());

        parts.AddRange(
            await db.Topics
                .AsNoTracking()
                .OrderBy(x => x.Id)
                .Select(x => $"topic:{x.Id:N}")
                .ToListAsync());

        parts.AddRange(
            await db.Writings
                .AsNoTracking()
                .OrderBy(x => x.Id)
                .Select(x => $"writing:{x.Id:N}")
                .ToListAsync());

        parts.AddRange(
            await db.BookAcquisitions
                .AsNoTracking()
                .OrderBy(x => x.Id)
                .Select(x => $"acquisition:{x.Id:N}")
                .ToListAsync());

        parts.AddRange(
            await db.BookCollections
                .AsNoTracking()
                .OrderBy(x => x.BookId)
                .ThenBy(x => x.CollectionId)
                .Select(x =>
                    $"bookCollection:{x.BookId:N}:{x.CollectionId:N}")
                .ToListAsync());

        parts.AddRange(
            await db.NoteTopics
                .AsNoTracking()
                .OrderBy(x => x.NoteId)
                .ThenBy(x => x.TopicId)
                .Select(x =>
                    $"noteTopic:{x.NoteId:N}:{x.TopicId:N}")
                .ToListAsync());

        parts.AddRange(
            await db.WritingNotes
                .AsNoTracking()
                .OrderBy(x => x.WritingId)
                .ThenBy(x => x.NoteId)
                .Select(x =>
                    $"writingNote:{x.WritingId:N}:{x.NoteId:N}")
                .ToListAsync());

        var canonical = string.Join('\n', parts);
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    private static void AssertFixtureIsRedistributable(byte[] bytes)
    {
        using var archive = OpenArchive(bytes);

        archive.Entries.Should().NotBeEmpty();
        archive.GetEntry(ManifestPath).Should().NotBeNull();
        archive.GetEntry(DataPath).Should().NotBeNull();

        var textualPayload = new StringBuilder();

        foreach (var entry in archive.Entries.Where(x =>
                     x.FullName is ManifestPath or DataPath))
        {
            using var reader = new StreamReader(
                entry.Open(),
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                leaveOpen: false);

            textualPayload.AppendLine(reader.ReadToEnd());
        }

        var text = textualPayload.ToString();

        text.Should().NotContain(
            PortableArchiveTestSupport.SecretMarker,
            "generated public fixtures must never contain deployment credentials");

        text.Should().NotContain(
            "source-machine",
            "absolute source-host file paths are not portable");

        text.Should().NotContain(
            "/srv/private",
            "absolute source-host file paths are not portable");

        text.Should().NotContain(
            "/mnt/library",
            "absolute source-host file paths are not portable");

        text.Should().NotContain(
            "/another-machine",
            "absolute source-host file paths are not portable");

        text.Should().NotContain(
            "private-model",
            "provider configuration is host-specific and excluded");

        archive.Entries.Should().OnlyContain(entry =>
            IsSafeArchivePath(entry.FullName));
    }

    private static bool IsSafeArchivePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (path.StartsWith("/", StringComparison.Ordinal)
            || path.StartsWith("\\", StringComparison.Ordinal)
            || Path.IsPathRooted(path))
        {
            return false;
        }

        var segments = path
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        return segments.All(segment =>
            segment is not "." and not "..");
    }

    private static async Task AssertManifestVersionAsync(
        byte[] bytes,
        int expectedFormatVersion,
        int expectedDataVersion)
    {
        using var archive = OpenArchive(bytes);
        var manifest = await ReadJsonObjectAsync(archive, ManifestPath);

        manifest["format"]!.GetValue<string>().Should().Be(PortableFormat);
        manifest["formatVersion"]!.GetValue<int>().Should().Be(expectedFormatVersion);
        manifest["dataVersion"]!.GetValue<int>().Should().Be(expectedDataVersion);
    }

    private static ZipArchive OpenArchive(byte[] bytes) =>
        new(
            new MemoryStream(bytes, writable: false),
            ZipArchiveMode.Read,
            leaveOpen: false);

    private static async Task<JsonObject> ReadJsonObjectAsync(
        ZipArchive archive,
        string path)
    {
        var entry = archive.GetEntry(path)
            ?? throw new InvalidDataException($"Archive entry '{path}' is missing.");

        await using var stream = entry.Open();
        var node = await JsonNode.ParseAsync(stream);

        return node?.AsObject()
            ?? throw new InvalidDataException(
                $"Archive entry '{path}' does not contain a JSON object.");
    }

    private static List<TestArchiveEntry> ReadEntries(byte[] archiveBytes)
    {
        using var source = new MemoryStream(archiveBytes, writable: false);
        using var archive = new ZipArchive(
            source,
            ZipArchiveMode.Read,
            leaveOpen: false);

        var entries = new List<TestArchiveEntry>();

        foreach (var entry in archive.Entries)
        {
            using var input = entry.Open();
            using var output = new MemoryStream();
            input.CopyTo(output);

            entries.Add(new TestArchiveEntry(
                entry.FullName,
                output.ToArray()));
        }

        return entries;
    }

    private static MemoryStream BuildArchive(
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
        return output;
    }

    private static void MutateJsonEntry(
        List<TestArchiveEntry> entries,
        string name,
        Action<JsonObject> mutate)
    {
        var index = entries.FindIndex(x =>
            string.Equals(x.Name, name, StringComparison.Ordinal));

        if (index < 0)
            throw new InvalidDataException($"Archive entry '{name}' is missing.");

        var root = JsonNode.Parse(entries[index].Bytes)?.AsObject()
            ?? throw new InvalidDataException(
                $"Archive entry '{name}' is not a JSON object.");

        mutate(root);

        entries[index] = new TestArchiveEntry(
            name,
            Encoding.UTF8.GetBytes(root.ToJsonString()));
    }

    private static void RehashDataDescriptor(
        List<TestArchiveEntry> entries)
    {
        var data = entries.Single(x =>
            string.Equals(x.Name, DataPath, StringComparison.Ordinal)).Bytes;

        var manifestIndex = entries.FindIndex(x =>
            string.Equals(x.Name, ManifestPath, StringComparison.Ordinal));

        if (manifestIndex < 0)
            throw new InvalidDataException("Portable manifest is missing.");

        var manifest = JsonNode
            .Parse(entries[manifestIndex].Bytes)!
            .AsObject();

        var descriptor = manifest["data"]?.AsObject()
            ?? throw new InvalidDataException(
                "Portable manifest does not contain a data descriptor.");

        descriptor["length"] = data.LongLength;
        descriptor["sha256"] = Convert.ToHexString(
                SHA256.HashData(data))
            .ToLowerInvariant();

        entries[manifestIndex] = new TestArchiveEntry(
            ManifestPath,
            Encoding.UTF8.GetBytes(manifest.ToJsonString()));
    }

    private static void AssertDataDescriptorMatchesPayload(
        JsonObject manifest,
        JsonObject data)
    {
        var descriptor = manifest["data"]!.AsObject();
        var serialized = Encoding.UTF8.GetBytes(data.ToJsonString());

        descriptor["length"]!.GetValue<long>()
            .Should()
            .Be(serialized.LongLength);

        descriptor["sha256"]!.GetValue<string>()
            .Should()
            .Be(
                Convert.ToHexString(SHA256.HashData(serialized))
                    .ToLowerInvariant());
    }

    private static int ReadInt(JsonObject root, string property) =>
        root[property]?.GetValue<int>()
        ?? throw new InvalidDataException(
            $"Portable manifest counts are missing '{property}'.");

    private sealed class SyntheticFixture : IDisposable
    {
        public SyntheticFixture(string name, byte[] bytes)
        {
            Name = name;
            Bytes = bytes;
        }

        public string Name { get; }

        public byte[] Bytes { get; }

        public void Dispose()
        {
        }
    }

    private sealed record TestArchiveEntry(
        string Name,
        byte[] Bytes);

    private sealed record FixtureArchiveCounts(
        int Works,
        int Books,
        int Collections,
        int BookCollections,
        int Notes,
        int Topics,
        int NoteTopics,
        int Writings,
        int BookAcquisitions);

    private sealed record DestinationCounts(
        int Works,
        int Books,
        int Collections,
        int BookCollections,
        int Notes,
        int Topics,
        int NoteTopics,
        int Writings,
        int WritingNotes,
        int BookAcquisitions)
    {
        public int TotalUserOwnedRows =>
            Works
            + Books
            + Collections
            + BookCollections
            + Notes
            + Topics
            + NoteTopics
            + Writings
            + WritingNotes
            + BookAcquisitions;
    }

    private sealed record PreflightProbeResult(
        bool IsCompatible,
        string? IncompatibilityCode,
        string? Format,
        int FormatVersion,
        int DataVersion,
        FixtureArchiveCounts Counts,
        int MediaFiles,
        long MediaBytes,
        long MaxSingleEntryBytes,
        bool DestinationIsEmpty,
        bool RequiresReplacementConfirmation,
        string DestinationRevision,
        DestinationCounts ExistingCounts);
}

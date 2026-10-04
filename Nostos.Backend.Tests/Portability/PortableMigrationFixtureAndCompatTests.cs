using System.IO.Compression;
using System.Reflection;
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
    private const int IntermediateDataVersion = 2;
    private const int CurrentDataVersion = 3;

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
            IntermediateDataVersion);

        fixture.Name.Should().Be("portable-format1-data2.nostos");
        fixture.Bytes.Should().NotBeEmpty();

        using var archive = OpenArchive(fixture.Bytes);
        var manifest = await ReadJsonObjectAsync(archive, ManifestPath);
        var data = await ReadJsonObjectAsync(archive, DataPath);

        manifest["format"]!.GetValue<string>().Should().Be(PortableFormat);
        manifest["formatVersion"]!.GetValue<int>().Should().Be(SupportedFormatVersion);
        manifest["dataVersion"]!.GetValue<int>().Should().Be(IntermediateDataVersion);

        data["version"]!.GetValue<int>().Should().Be(IntermediateDataVersion);
        data["writingNotes"].Should().BeOfType<JsonArray>();
        data["writingNotes"]!.AsArray().Should().ContainSingle();
        data.ContainsKey("noteImportBookLinks").Should().BeFalse(
            "DataVersion 2 predates remembered e-reader book mappings");

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
            IntermediateDataVersion);

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
    public void Job_transitions_validate_legal_state_machine_and_reject_illegal()
    {
        var legalTransitions = new (MigrationJobState From, MigrationJobState To)[]
        {
            (MigrationJobState.Pending, MigrationJobState.Preparing),
            (MigrationJobState.Preparing, MigrationJobState.Transferring),
            (MigrationJobState.Transferring, MigrationJobState.Validating),
            (MigrationJobState.Validating, MigrationJobState.ReadyToActivate),
            (MigrationJobState.ReadyToActivate, MigrationJobState.Activating),
            (MigrationJobState.Activating, MigrationJobState.Completed),
        };

        foreach (var (from, to) in legalTransitions)
        {
            MigrationJobTransitions.CanTransition(from, to).Should().BeTrue(
                because: $"{from} -> {to} is a legal migration transition");

            var act = () => MigrationJobTransitions.ValidateTransition(from, to);
            act.Should().NotThrow();
        }

        var terminalStates = new[]
        {
            MigrationJobState.Completed,
            MigrationJobState.Failed,
            MigrationJobState.Cancelled,
            MigrationJobState.Expired,
        };

        var retryableStates = new[]
        {
            MigrationJobState.Failed,
            MigrationJobState.Cancelled,
            MigrationJobState.Expired,
        };

        foreach (var state in Enum.GetValues<MigrationJobState>())
        {
            MigrationJobTransitions.IsTerminal(state)
                .Should()
                .Be(terminalStates.Contains(state), because: $"{state} has a defined terminal-state contract");

            MigrationJobTransitions.IsRetryable(state)
                .Should()
                .Be(retryableStates.Contains(state), because: $"{state} has a defined retryability contract");
        }

        foreach (var terminal in terminalStates)
        {
            MigrationJobTransitions.AllowedTransitions[terminal].Should().BeEmpty();

            foreach (var target in Enum.GetValues<MigrationJobState>())
            {
                MigrationJobTransitions.CanTransition(terminal, target).Should().BeFalse();

                var act = () => MigrationJobTransitions.ValidateTransition(terminal, target);
                act.Should().Throw<InvalidOperationException>();
            }
        }

        foreach (var current in Enum.GetValues<MigrationJobState>())
        {
            foreach (var target in Enum.GetValues<MigrationJobState>())
            {
                var declaredLegal = MigrationJobTransitions.AllowedTransitions[current].Contains(target);

                MigrationJobTransitions.CanTransition(current, target)
                    .Should()
                    .Be(declaredLegal);

                if (!declaredLegal)
                {
                    var act = () => MigrationJobTransitions.ValidateTransition(current, target);
                    act.Should().Throw<InvalidOperationException>();
                }
            }
        }
    }

    [Fact]
    public void Preflight_evaluator_returns_expected_decisions_for_various_inputs()
    {
        MigrationPreflightRequest CompatibleRequest(
            string? clientDestinationRevision = "revision-1",
            long declaredArchiveBytes = 1_024,
            long declaredMediaBytes = 2_048,
            string? declaredFormatName = null,
            int declaredFormatVersion = PortableArchiveFormat.Version,
            int declaredDataVersion = PortableArchiveFormat.DataVersion) =>
            new(
                IncomingCounts: new MigrationArchiveCounts(
                    Works: 1,
                    Books: 1,
                    Notes: 1,
                    Writings: 1,
                    Topics: 1,
                    Collections: 1,
                    CollectionMemberships: 1,
                    Acquisitions: 1,
                    AssistantSettings: 1,
                    NoteImportBookLinks: 1),
                DeclaredArchiveBytes: declaredArchiveBytes,
                DeclaredMediaBytes: declaredMediaBytes,
                MaxSingleEntryBytes: 1_024,
                DeclaredFormatVersion: declaredFormatVersion,
                DeclaredDataVersion: declaredDataVersion,
                DeclaredFormatName: declaredFormatName,
                ClientDestinationRevision: clientDestinationRevision);

        MigrationPreflightResult Evaluate(
            MigrationPreflightRequest request,
            MigrationDestinationStatus destinationStatus = MigrationDestinationStatus.Empty,
            MigrationExistingCounts? existingCounts = null,
            long availableStorageBytes = long.MaxValue,
            string destinationRevision = "revision-1") =>
            MigrationPreflightEvaluator.Evaluate(
                new MigrationPreflightEvaluationInput(
                    Request: request,
                    DestinationStatus: destinationStatus,
                    ExistingCounts: existingCounts ?? new MigrationExistingCounts(),
                    AvailableStorageBytes: availableStorageBytes,
                    DestinationRevision: destinationRevision));

        Evaluate(CompatibleRequest())
            .Decision.Should().Be(MigrationPreflightDecision.AllowedEmpty);

        Evaluate(
                CompatibleRequest(),
                destinationStatus: MigrationDestinationStatus.Populated,
                existingCounts: new MigrationExistingCounts(Books: 1))
            .Decision.Should().Be(MigrationPreflightDecision.AllowedReplacementRequired);

        var populatedResult = Evaluate(
            CompatibleRequest(declaredArchiveBytes: 1_000_000, declaredMediaBytes: 2_000_000),
            destinationStatus: MigrationDestinationStatus.Populated,
            existingCounts: new MigrationExistingCounts(Books: 2, Notes: 10));

        populatedResult.EstimatedRecoveryBytes.Should().Be(100_000_000L + (12 * 1024L));
        populatedResult.RequiredStorageBytes.Should().Be(1_000_000L + 2_000_000L + 100_000_000L + (12 * 1024L));

        Evaluate(
                CompatibleRequest(declaredDataVersion: 999))
            .Decision.Should().Be(MigrationPreflightDecision.RejectedIncompatible);

        Evaluate(
                CompatibleRequest(
                    declaredArchiveBytes: 10_000,
                    declaredMediaBytes: 20_000),
                availableStorageBytes: 1)
            .Decision.Should().Be(MigrationPreflightDecision.RejectedInsufficientStorage);

        Evaluate(
                CompatibleRequest(clientDestinationRevision: "stale-revision"),
                destinationRevision: "current-revision")
            .Decision.Should().Be(MigrationPreflightDecision.RejectedDestinationConflict);

        Evaluate(
                CompatibleRequest(declaredFormatName: "nostos-operational-backup"))
            .Decision.Should().Be(MigrationPreflightDecision.RejectedOperationalBackupNotPortable);
    }

    [Fact]
    public async Task Round_trip_exports_and_imports_note_import_book_links_in_data_version_3()
    {
        await using var source = await LocalPortableTestLibrary.CreateAsync();

        await PortableArchiveTestSupport.PopulateRepresentativeAsync(source.Db, source.Storage);

        var book = await source.Db.Books
            .OrderBy(x => x.Id)
            .FirstAsync();

        var expected = new NoteImportBookLink
        {
            Id = Guid.NewGuid(),
            Source = "koreader",
            SourceKey = "device-book-42",
            BookId = book.Id,
            Book = book,
            CreatedAtUtc = new DateTime(2026, 10, 4, 4, 30, 0, DateTimeKind.Utc),
        };

        source.Db.NoteImportBookLinks.Add(expected);
        await source.Db.SaveChangesAsync();

        await using var archive = new MemoryStream();
        await source.Portability().ExportAsync(archive);

        archive.Position = 0;

        await using var destination = await LocalPortableTestLibrary.CreateAsync();
        await destination.Portability().ImportAsync(archive);

        var imported = await destination.Db.NoteImportBookLinks
            .AsNoTracking()
            .SingleAsync();

        imported.Id.Should().Be(expected.Id);
        imported.Source.Should().Be(expected.Source);
        imported.SourceKey.Should().Be(expected.SourceKey);
        imported.BookId.Should().Be(expected.BookId);
        imported.CreatedAtUtc.Should().Be(expected.CreatedAtUtc);

        await using var reExported = new MemoryStream();
        await destination.Portability().ExportAsync(reExported);

        reExported.Position = 0;

        await using var secondDestination = await LocalPortableTestLibrary.CreateAsync();
        await secondDestination.Portability().ImportAsync(reExported);

        var roundTripped = await secondDestination.Db.NoteImportBookLinks
            .AsNoTracking()
            .SingleAsync();

        roundTripped.Should().BeEquivalentTo(
            imported,
            options => options.Excluding(x => x.Book));
    }

    [Fact]
    public async Task Legacy_v1_and_v2_fixtures_strictly_omit_unsupported_fields()
    {
        using var v1 = await CreateFixtureAsync("v1.nostos", LegacyDataVersion);
        using var v2 = await CreateFixtureAsync("v2.nostos", IntermediateDataVersion);

        using (var archive = OpenArchive(v1.Bytes))
        {
            var library = await ReadJsonObjectAsync(archive, DataPath);
            library.ContainsKey("writingNotes").Should().BeFalse();
            library.ContainsKey("noteImportBookLinks").Should().BeFalse();
        }

        using (var archive = OpenArchive(v2.Bytes))
        {
            var library = await ReadJsonObjectAsync(archive, DataPath);
            library.ContainsKey("writingNotes").Should().BeTrue();
            library.ContainsKey("noteImportBookLinks").Should().BeFalse();
        }
    }

    [Fact]
    public async Task Import_rejects_archive_when_manifest_and_payload_data_version_mismatch()
    {
        using var fixture = await CreateFixtureAsync("compat.nostos", CurrentDataVersion);
        var entries = ReadEntries(fixture.Bytes);

        MutateJsonEntry(entries, DataPath, json => json["version"] = LegacyDataVersion);
        RehashDataDescriptor(entries);

        using var corruptArchive = BuildArchive(entries);
        await using var destination = await LocalPortableTestLibrary.CreateAsync();

        var act = async () => await destination.Portability().ImportAsync(corruptArchive);
        var exception = await act.Should().ThrowAsync<PortableArchiveException>();
        exception.Which.Code.Should().Be("data_version_mismatch");

        (await destination.Db.Works.CountAsync()).Should().Be(0);
        (await destination.Db.Books.CountAsync()).Should().Be(0);
        (await destination.Db.Notes.CountAsync()).Should().Be(0);
        (await destination.Db.Writings.CountAsync()).Should().Be(0);
        (await destination.Db.NoteImportBookLinks.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Import_rejects_payload_with_unexpected_version_data_for_declared_version()
    {
        using var v1 = await CreateFixtureAsync("v1.nostos", LegacyDataVersion);
        var v1Entries = ReadEntries(v1.Bytes);

        MutateJsonEntry(v1Entries, DataPath, json => json["writingNotes"] = new JsonArray());
        RehashDataDescriptor(v1Entries);

        using (var corruptV1 = BuildArchive(v1Entries))
        {
            await using var destination = await LocalPortableTestLibrary.CreateAsync();
            var act = async () => await destination.Portability().ImportAsync(corruptV1);
            var exception = await act.Should().ThrowAsync<PortableArchiveException>();
            exception.Which.Code.Should().Be("unexpected_version_data");

            (await destination.Db.Works.CountAsync()).Should().Be(0);
            (await destination.Db.Books.CountAsync()).Should().Be(0);
            (await destination.Db.WritingNotes.CountAsync()).Should().Be(0);
        }

        using var v2 = await CreateFixtureAsync("v2.nostos", IntermediateDataVersion);
        var v2Entries = ReadEntries(v2.Bytes);

        MutateJsonEntry(v2Entries, DataPath, json => json["noteImportBookLinks"] = new JsonArray
        {
            new JsonObject
            {
                ["id"] = Guid.NewGuid(),
                ["source"] = "koreader",
                ["sourceKey"] = "unexpected-v2-link",
                ["bookId"] = Guid.NewGuid(),
                ["createdAtUtc"] = DateTime.UtcNow,
            }
        });
        RehashDataDescriptor(v2Entries);

        using (var corruptV2 = BuildArchive(v2Entries))
        {
            await using var destination = await LocalPortableTestLibrary.CreateAsync();
            var act = async () => await destination.Portability().ImportAsync(corruptV2);
            var exception = await act.Should().ThrowAsync<PortableArchiveException>();
            exception.Which.Code.Should().Be("unexpected_version_data");

            (await destination.Db.Works.CountAsync()).Should().Be(0);
            (await destination.Db.Books.CountAsync()).Should().Be(0);
            (await destination.Db.NoteImportBookLinks.CountAsync()).Should().Be(0);
        }
    }

    [Fact]
    public void Completeness_inventory_covers_all_preflight_count_properties()
    {
        var actualIncomingCountProperties = typeof(MigrationArchiveCounts)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        actualIncomingCountProperties.Should().BeEquivalentTo(
            PortableCompletenessInventoryTests.ExpectedIncomingCountProperties);

        var actualDestinationCountProperties = typeof(MigrationExistingCounts)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        actualDestinationCountProperties.Should().BeEquivalentTo(
            PortableCompletenessInventoryTests.ExpectedDestinationCountProperties);

        new MigrationArchiveCounts(NoteTopics: 1).TotalRows.Should().Be(1);
        new MigrationExistingCounts(NoteTopics: 1).TotalRows.Should().Be(1);
    }

    [Fact]
    public void Migration_idempotency_result_distinguishes_create_replay_and_typed_conflict()
    {
        var job = new MigrationJob(
            Guid.NewGuid(),
            MigrationDirection.Import,
            MigrationJobState.Pending,
            MigrationRecoveryStatus.NotRequired,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        var created = MigrationIdempotencyResult<MigrationJob>.Created(job);
        created.Resource.Should().BeSameAs(job);
        created.WasReplay.Should().BeFalse();
        created.IsConflict.Should().BeFalse();
        created.Conflict.Should().BeNull();

        var replayed = MigrationIdempotencyResult<MigrationJob>.Replayed(job);
        replayed.Resource.Should().BeSameAs(job);
        replayed.WasReplay.Should().BeTrue();
        replayed.IsConflict.Should().BeFalse();

        var conflicted = MigrationIdempotencyResult<MigrationJob>.Conflicted(
            new MigrationIdempotencyConflict(
                MigrationIdempotencyConflictKind.KeyReusedWithDifferentPayload));
        conflicted.Resource.Should().BeNull();
        conflicted.WasReplay.Should().BeFalse();
        conflicted.IsConflict.Should().BeTrue();
        conflicted.Conflict!.Kind.Should().Be(
            MigrationIdempotencyConflictKind.KeyReusedWithDifferentPayload);

        var sessionKeyParameter = typeof(MigrationSessionRequest)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Single(parameter => parameter.Name == "IdempotencyKey");
        sessionKeyParameter.ParameterType.Should().Be(typeof(string));
        sessionKeyParameter.HasDefaultValue.Should().BeFalse();

        var jobCreateKeyParameter = typeof(IMigrationJobStore)
            .GetMethod(nameof(IMigrationJobStore.CreateAsync))!
            .GetParameters()
            .Single(parameter => parameter.Name == "idempotencyKey");
        jobCreateKeyParameter.ParameterType.Should().Be(typeof(string));
        jobCreateKeyParameter.IsOptional.Should().BeFalse();

        typeof(IMigrationJobStore)
            .GetMethod(nameof(IMigrationJobStore.CreateAsync))!
            .ReturnType.Should().Be(typeof(Task<MigrationIdempotencyResult<MigrationJob>>));

        typeof(IMigrationTransferService)
            .GetMethod(nameof(IMigrationTransferService.CreateSessionAsync))!
            .ReturnType.Should().Be(
                typeof(Task<MigrationIdempotencyResult<MigrationSessionStatus>>));
    }

    [Fact]
    public async Task Fixture_generation_is_stable_at_the_contract_level_and_can_be_written_as_redistributable_files()
    {
        using var v1 = await CreateFixtureAsync(
            "portable-format1-data1.nostos",
            LegacyDataVersion);

        using var v2 = await CreateFixtureAsync(
            "portable-format1-data2.nostos",
            IntermediateDataVersion);

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
                IntermediateDataVersion);
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
        if (dataVersion is not (LegacyDataVersion or IntermediateDataVersion or CurrentDataVersion))
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
                root.Remove("noteImportBookLinks");
            });

            RehashDataDescriptor(entries);
        }
        else if (dataVersion == IntermediateDataVersion)
        {
            MutateJsonEntry(entries, ManifestPath, root =>
            {
                root["formatVersion"] = SupportedFormatVersion;
                root["dataVersion"] = IntermediateDataVersion;
            });

            MutateJsonEntry(entries, DataPath, root =>
            {
                root["version"] = IntermediateDataVersion;
                root.Remove("noteImportBookLinks");
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
}

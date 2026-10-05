using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Slice 11 destination-revision completion: <see cref="LibraryRevision"/> is
/// the only writer of <see cref="LibraryState.StateVersion"/>. Every portable
/// create/update/delete advances it exactly once per transaction, host-only
/// operational writes never do, invalid state fails closed, and concurrent
/// writers (including a tracked save racing a bulk delete) never lose or
/// reorder an increment.
/// </summary>
public sealed class LibraryDestinationRevisionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"nostos-revision-{Guid.NewGuid():N}");

    public LibraryDestinationRevisionTests() => Directory.CreateDirectory(_root);

    private string DatabasePath => Path.Combine(_root, "revision.db");

    private NostosDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={DatabasePath};Pooling=False;Default Timeout=30")
            .Options;
        return new NostosDbContext(options);
    }

    private async Task<NostosDbContext> CreateInitializedContextAsync()
    {
        var db = CreateContext();
        await db.Database.EnsureCreatedAsync();
        if (!await db.LibraryStates.AnyAsync())
        {
            db.LibraryStates.Add(new LibraryState { StateVersion = "0" });
            await db.SaveChangesAsync();
        }

        db.ChangeTracker.Clear();
        return db;
    }

    private async Task<NostosDbContext> CreateRawContextAsync()
    {
        var db = CreateContext();
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private async Task<long> ReadRevisionAsync()
    {
        await using var db = CreateContext();
        var raw = await db.LibraryStates.AsNoTracking()
            .Where(s => s.Id == LibraryState.WellKnownId)
            .Select(s => s.StateVersion)
            .SingleAsync();
        return long.Parse(raw);
    }

    /// <summary>
    /// One row of every portable entity type in one save/transaction advances
    /// the revision exactly once (one bump for the whole graph, not one per row).
    /// </summary>
    [Fact]
    public async Task Creating_one_row_of_every_portable_type_advances_the_revision_once()
    {
        await using var db = await CreateInitializedContextAsync();
        var before = await ReadRevisionAsync();

        var work = new WorkModel { Title = "Work", NormalizedTitle = "WORK", NormalizedAuthor = "" };
        var book = new EBookModel
        {
            Work = work,
            Title = "Book",
            Metadata = { Publisher = "Press" },
            Progress = { ProgressPercent = 10 },
            FileDetails = { HasFile = true, FileName = "book.epub" },
        };
        var collection = new CollectionModel { Name = "Collection" };
        var note = new NoteModel { Book = book, Content = "note" };
        var topic = new TopicModel { Topic = "topic" };
        var writing = new WritingModel { Name = "Writing", Type = WritingType.Document };

        db.Works.Add(work);
        db.Books.Add(book);
        db.Collections.Add(collection);
        db.Notes.Add(note);
        db.Topics.Add(topic);
        db.Writings.Add(writing);
        db.BookCollections.Add(new BookCollectionModel { Book = book, Collection = collection, AddedAt = DateTime.UtcNow });
        db.NoteTopics.Add(new NoteTopicModel { Note = note, Topic = topic });
        db.WritingNotes.Add(new WritingNoteModel { Writing = writing, Note = note, AddedAt = DateTime.UtcNow });
        db.BookAcquisitions.Add(new BookAcquisitionModel
        {
            Book = book,
            ProviderId = "provider",
            ProviderDisplayName = "Provider",
            ExternalId = "1",
            AssetId = "asset",
            AcquiredAt = DateTime.UtcNow,
        });
        db.NoteImportBookLinks.Add(new NoteImportBookLink
        {
            Book = book,
            Source = "koreader",
            SourceKey = "device-book",
        });
        db.AssistantSettings.Add(new AssistantSettingsModel { CaptureProcessingMode = "verbatim" });

        await db.SaveChangesAsync();

        (await ReadRevisionAsync()).Should().Be(before + 1);

        // A second save with no portable changes does not advance it again.
        db.LibraryCommandReceipts.Add(new LibraryCommandReceipt
        {
            ClientId = "client",
            IdempotencyKey = "key",
            CommandKind = "noop",
            ResponseJson = "{}",
        });
        await db.SaveChangesAsync();
        (await ReadRevisionAsync()).Should().Be(before + 1);
    }

    [Theory]
    [InlineData(PortableMutation.Create)]
    [InlineData(PortableMutation.Update)]
    [InlineData(PortableMutation.Delete)]
    public async Task Root_portable_entity_types_advance_on_create_update_and_delete(PortableMutation mutation)
    {
        var rootTypes = new[]
        {
            typeof(PhysicalBookModel),
            typeof(EBookModel),
            typeof(AudioBookModel),
            typeof(WorkModel),
            typeof(CollectionModel),
            typeof(BookCollectionModel),
            typeof(NoteModel),
            typeof(TopicModel),
            typeof(NoteTopicModel),
            typeof(WritingModel),
            typeof(WritingNoteModel),
            typeof(BookAcquisitionModel),
            typeof(AssistantSettingsModel),
            typeof(NoteImportBookLink),
        };

        foreach (var type in rootTypes)
        {
            await using var db = await CreateInitializedContextAsync();
            var scenario = PortableEntityFactory.CreateRootScenario(type);

            var before = await ReadRevisionAsync();

            // Seed principals and the entity together so EF orders the graph.
            db.AddRange(scenario.Principals);
            db.Add(scenario.Entity);
            await db.SaveChangesAsync();

            if (mutation == PortableMutation.Create)
            {
                (await ReadRevisionAsync()).Should().Be(
                    before + 1,
                    $"{type.Name} create must advance the portable revision exactly once");
                continue;
            }

            db.ChangeTracker.Clear();
            before = await ReadRevisionAsync();

            if (mutation == PortableMutation.Update)
            {
                if (type == typeof(NoteTopicModel))
                {
                    // A key-only membership row has no updatable payload; its
                    // portable mutation is removal plus a replacement link.
                    var link = (NoteTopicModel)scenario.Entity;
                    db.Remove(await db.FindAsync(type, scenario.Key) ?? throw new InvalidOperationException());
                    db.Add(new NoteTopicModel { NoteId = link.NoteId, TopicId = link.TopicId });
                }
                else
                {
                    var tracked = await db.FindAsync(type, scenario.Key);
                    PortableEntityFactory.Mutate(tracked!);
                }
            }
            else
            {
                var tracked = await db.FindAsync(type, scenario.Key);
                db.Remove(tracked!);
            }

            await db.SaveChangesAsync();

            (await ReadRevisionAsync()).Should().Be(
                before + 1,
                $"{type.Name} {mutation} must advance the portable revision exactly once");
        }
    }

    [Theory]
    [InlineData(OwnedPortableField.Metadata)]
    [InlineData(OwnedPortableField.Progress)]
    [InlineData(OwnedPortableField.FileDetails)]
    public async Task Owned_portable_types_advance_even_when_only_the_owned_entry_changed(OwnedPortableField field)
    {
        await using var db = await CreateInitializedContextAsync();
        var work = new WorkModel { Title = "W", NormalizedTitle = "W", NormalizedAuthor = "" };
        var book = new EBookModel
        {
            Work = work,
            Title = "Book",
            Metadata = { Publisher = "Before" },
            Progress = { ProgressPercent = 1 },
            FileDetails = { FileName = "before.epub" },
        };
        db.Add(work);
        db.Add(book);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var before = await ReadRevisionAsync();
        var tracked = await db.Books.SingleAsync(b => b.Id == book.Id);
        switch (field)
        {
            case OwnedPortableField.Metadata:
                tracked.Metadata.Publisher = "After";
                break;
            case OwnedPortableField.Progress:
                tracked.Progress.ProgressPercent = 42;
                break;
            case OwnedPortableField.FileDetails:
                tracked.FileDetails.FileName = "after.epub";
                break;
        }

        db.ChangeTracker.Entries<BookModel>().Single().State.Should().Be(
            EntityState.Unchanged,
            "only the owned entry changed; the advance must come from the owned portable type");

        await db.SaveChangesAsync();

        (await ReadRevisionAsync()).Should().Be(
            before + 1,
            $"an update touching only {field} must still advance the revision");
    }

    [Fact]
    public async Task Host_only_writes_never_advance_the_revision()
    {
        await using var db = await CreateInitializedContextAsync();
        var before = await ReadRevisionAsync();

        db.BackupRecords.Add(new BackupRecord { Status = BackupStatus.Completed, SizeBytes = 1 });
        db.MigrationJobRecords.Add(new MigrationJobRecord
        {
            Id = Guid.NewGuid(),
            Direction = 0,
            State = 0,
            RecoveryStatus = 0,
            IdempotencyKey = "host-job",
            CreationPayloadHash = new string('a', 64),
            AttemptNumber = 1,
            ExpiresAtUtc = DateTime.UtcNow.AddDays(1),
        });
        db.LibraryCommandReceipts.Add(new LibraryCommandReceipt
        {
            ClientId = "client",
            IdempotencyKey = "host-receipt",
            CommandKind = "noop",
            ResponseJson = "{}",
        });
        db.NoteCommandReceipts.Add(new NoteCommandReceipt
        {
            ClientId = "client",
            IdempotencyKey = "host-note-receipt",
            Command = "noop",
            ResultJson = "{}",
        });
        db.AiProviderSettings.Add(new AiProviderSettingsModel { LlmModel = "model" });
        await db.SaveChangesAsync();

        (await ReadRevisionAsync()).Should().Be(before);
    }

    /// <summary>
    /// LibraryRevision is the only writer: no product or host source file may
    /// assign the revision in application memory (the manual NextVersion
    /// writers are gone). Migrations, the model declaration and the activation
    /// candidate builder's array-slot write are not property assignments.
    /// </summary>
    [Fact]
    public void No_product_code_assigns_StateVersion_outside_the_revision_helper()
    {
        var repoRoot = FindRepoRoot();
        var offenders = new List<string>();
        foreach (var project in new[] { "Nostos.Product", "Nostos.Backend" })
        {
            var projectRoot = Path.Combine(repoRoot, project);
            foreach (var file in Directory.EnumerateFiles(projectRoot, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || Path.GetFileName(file) == "LibraryRevision.cs")
                {
                    // LibraryRevision.cs is the one allowed writer.
                    continue;
                }

                var lines = File.ReadAllLines(file);
                for (var index = 0; index < lines.Length; index++)
                {
                    if (Regex.IsMatch(lines[index], @"\.StateVersion\s*=(?!=)"))
                    {
                        offenders.Add(
                            $"{Path.GetRelativePath(repoRoot, file)}:{index + 1}: {lines[index].Trim()}");
                    }
                }
            }
        }

        offenders.Should().BeEmpty(
            "LibraryRevision is the only writer of LibraryState.StateVersion; callers must use " +
            "LibraryRevision.AdvanceAndGetAsync and return/store its value" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public async Task Missing_library_state_row_is_created_and_the_first_advance_is_one()
    {
        await using var db = await CreateRawContextAsync();
        (await db.LibraryStates.CountAsync()).Should().Be(0);

        db.Works.Add(new WorkModel { Title = "W", NormalizedTitle = "W", NormalizedAuthor = "" });
        await db.SaveChangesAsync();

        await using var verify = CreateContext();
        var state = await verify.LibraryStates.AsNoTracking().SingleAsync();
        state.StateVersion.Should().Be("1", "a missing row starts at 0 and the mutation advances it");
        state.SingletonSlot.Should().Be(LibraryState.SingletonSentinel);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("")]
    [InlineData("-1")]
    public async Task Invalid_revision_fails_closed_and_does_not_commit_the_portable_write(string corrupted)
    {
        await using var db = await CreateInitializedContextAsync();
        await db.Database.ExecuteSqlRawAsync(
            $"UPDATE \"LibraryStates\" SET \"StateVersion\" = '{corrupted}'");

        db.Works.Add(new WorkModel { Title = "W", NormalizedTitle = "W", NormalizedAuthor = "" });

        var act = async () => await db.SaveChangesAsync();
        var exception = await act.Should().ThrowAsync<LibraryRevisionException>();
        exception.Which.Code.Should().Be(LibraryRevisionException.InvalidRevisionCode);

        await using var verify = CreateContext();
        (await verify.Works.CountAsync()).Should().Be(0, "the portable write must roll back with the failed advance");
        (await verify.LibraryStates.AsNoTracking().SingleAsync()).StateVersion.Should().Be(
            corrupted,
            "a corrupted revision is never silently normalised");
    }

    [Fact]
    public async Task Ambient_transaction_rollback_rolls_back_the_revision_advance()
    {
        await using var db = await CreateInitializedContextAsync();
        var before = await ReadRevisionAsync();

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            db.Works.Add(new WorkModel { Title = "W", NormalizedTitle = "W", NormalizedAuthor = "" });
            await db.SaveChangesAsync();
            await transaction.RollbackAsync();
        }

        (await ReadRevisionAsync()).Should().Be(before);
        await using var verify = CreateContext();
        (await verify.Works.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Multiple_portable_saves_in_one_transaction_advance_once()
    {
        await using var db = await CreateInitializedContextAsync();
        var before = await ReadRevisionAsync();

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            db.Works.Add(new WorkModel { Title = "W1", NormalizedTitle = "W1", NormalizedAuthor = "" });
            await db.SaveChangesAsync();
            db.Works.Add(new WorkModel { Title = "W2", NormalizedTitle = "W2", NormalizedAuthor = "" });
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        (await ReadRevisionAsync()).Should().Be(
            before + 1,
            "one increment per transaction, however many portable saves it contains");
    }

    [Fact]
    public async Task Separate_saves_in_one_context_advance_once_each()
    {
        await using var db = await CreateInitializedContextAsync();
        var before = await ReadRevisionAsync();

        db.Works.Add(new WorkModel { Title = "W1", NormalizedTitle = "W1", NormalizedAuthor = "" });
        await db.SaveChangesAsync();
        db.Works.Add(new WorkModel { Title = "W2", NormalizedTitle = "W2", NormalizedAuthor = "" });
        await db.SaveChangesAsync();

        (await ReadRevisionAsync()).Should().Be(before + 2, "a reused context bumps once per portable save");
    }

    [Fact]
    public async Task Sync_save_with_accept_all_changes_false_still_advances_each_portable_save()
    {
        await using var db = await CreateInitializedContextAsync();
        var work = new WorkModel { Title = "Before", NormalizedTitle = "BEFORE", NormalizedAuthor = "" };
        db.Works.Add(work);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var before = await ReadRevisionAsync();

        var tracked = await db.Works.SingleAsync(w => w.Id == work.Id);
        tracked.Title = "First";
        db.SaveChanges(acceptAllChangesOnSuccess: false);
        (await ReadRevisionAsync()).Should().Be(before + 1);
        db.ChangeTracker.Entries<WorkModel>().Single().State.Should().Be(
            EntityState.Modified,
            "acceptAllChangesOnSuccess:false leaves the entry staged");

        tracked.Title = "Second";
        db.SaveChanges(acceptAllChangesOnSuccess: false);
        (await ReadRevisionAsync()).Should().Be(
            before + 2,
            "a still-staged portable entry must not suppress the next advance");
    }

    [Fact]
    public async Task Async_save_with_accept_all_changes_false_still_advances_each_portable_save()
    {
        await using var db = await CreateInitializedContextAsync();
        var work = new WorkModel { Title = "Before", NormalizedTitle = "BEFORE", NormalizedAuthor = "" };
        db.Works.Add(work);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var before = await ReadRevisionAsync();

        var tracked = await db.Works.SingleAsync(w => w.Id == work.Id);
        tracked.Title = "First";
        await db.SaveChangesAsync(acceptAllChangesOnSuccess: false);
        (await ReadRevisionAsync()).Should().Be(before + 1);

        tracked.Title = "Second";
        await db.SaveChangesAsync(acceptAllChangesOnSuccess: false);
        (await ReadRevisionAsync()).Should().Be(before + 2);
    }

    [Fact]
    public async Task Explicit_helper_call_then_portable_save_advances_once_per_transaction()
    {
        await using var db = await CreateInitializedContextAsync();
        var before = await ReadRevisionAsync();

        await using var transaction = await db.Database.BeginTransactionAsync();
        var version = await LibraryRevision.AdvanceAndGetAsync(db);
        version.Should().Be((before + 1).ToString());
        db.Works.Add(new WorkModel { Title = "W", NormalizedTitle = "W", NormalizedAuthor = "" });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        (await ReadRevisionAsync()).Should().Be(before + 1, "the save must not advance a second time");
    }

    [Fact]
    public async Task Concurrent_saves_advance_the_revision_once_each()
    {
        await using (var seed = await CreateInitializedContextAsync())
        {
            _ = seed;
        }

        var before = await ReadRevisionAsync();
        const int writers = 6;
        await Task.WhenAll(Enumerable.Range(0, writers).Select(index => Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 60; attempt++)
            {
                try
                {
                    await using var db = CreateContext();
                    db.Works.Add(new WorkModel
                    {
                        Title = $"W{index}-{attempt}",
                        NormalizedTitle = $"W{index}-{attempt}",
                        NormalizedAuthor = "",
                    });
                    await db.SaveChangesAsync();
                    return;
                }
                catch (Exception exception) when (exception is DbUpdateException or SqliteException)
                {
                    await Task.Delay(10 * (attempt + 1));
                }
            }

            throw new Xunit.Sdk.XunitException($"writer {index} never committed");
        })));

        await using var check = CreateContext();
        (await check.Works.AsNoTracking().CountAsync()).Should().Be(writers);
        (await ReadRevisionAsync()).Should().Be(
            before + writers,
            "every committed portable save must contribute exactly one distinct revision step");
    }

    /// <summary>
    /// The standardized lock order: a tracked save and a bulk delete run
    /// repeatedly; both must commit and the revision must increase by exactly
    /// two per iteration. On PostgreSQL the inverse order deadlocks when the
    /// two target the same row; this is the SQLite counterpart of the PG-lane
    /// test.
    /// </summary>
    [Fact]
    public async Task Tracked_save_racing_a_bulk_delete_commits_both_increments_repeatedly()
    {
        await using (var init = await CreateInitializedContextAsync())
        {
            _ = init;
        }

        for (var iteration = 0; iteration < 10; iteration++)
        {
            Guid trackedId;
            Guid deletedId;
            await using (var setup = CreateContext())
            {
                var tracked = new TopicModel { Topic = $"race-tracked-{iteration}" };
                var deleted = new TopicModel { Topic = $"race-deleted-{iteration}" };
                setup.Topics.AddRange(tracked, deleted);
                await setup.SaveChangesAsync();
                trackedId = tracked.Id;
                deletedId = deleted.Id;
            }

            var before = await ReadRevisionAsync();
            var barrier = new Barrier(2);

            async Task TrackedSaveAsync()
            {
                await using var db = CreateContext();
                barrier.SignalAndWait();
                await using var transaction = await db.Database.BeginTransactionAsync();
                await LibraryRevision.AdvanceAndGetAsync(db);
                var tracked = await db.Topics.SingleAsync(t => t.Id == trackedId);
                tracked.Topic = $"changed-{iteration}";
                await db.SaveChangesAsync();
                await transaction.CommitAsync();
            }

            async Task BulkDeleteAsync()
            {
                await using var db = CreateContext();
                barrier.SignalAndWait();
                await using var transaction = await db.Database.BeginTransactionAsync();
                await LibraryRevision.AdvanceAndGetAsync(db);
                await db.Topics.Where(t => t.Id == deletedId).ExecuteDeleteAsync();
                await transaction.CommitAsync();
            }

            await Task.WhenAll(
                Task.Run(TrackedSaveAsync),
                Task.Run(BulkDeleteAsync)).WaitAsync(TimeSpan.FromSeconds(30));

            (await ReadRevisionAsync()).Should().Be(
                before + 2,
                $"iteration {iteration}: both portable transactions committed one advance each");
        }
    }

    public enum PortableMutation
    {
        Create,
        Update,
        Delete,
    }

    public enum OwnedPortableField
    {
        Metadata,
        Progress,
        FileDetails,
    }

    /// <summary>Builds a minimal valid row (plus required principals) for each root portable type.</summary>
    internal static class PortableEntityFactory
    {
        internal sealed record RootScenario(object Entity, object?[] Key, object[] Principals);

        internal static RootScenario CreateRootScenario(Type type)
        {
            var suffix = Guid.NewGuid().ToString("N");
            var work = new WorkModel { Id = Guid.NewGuid(), Title = $"W-{suffix}", NormalizedTitle = $"W-{suffix}", NormalizedAuthor = "" };
            var collection = new CollectionModel { Id = Guid.NewGuid(), Name = $"C-{suffix}" };
            var topic = new TopicModel { Id = Guid.NewGuid(), Topic = $"T-{suffix}" };
            var writing = new WritingModel { Id = Guid.NewGuid(), Name = $"Writing-{suffix}", Type = WritingType.Document };
            BookModel book = type switch
            {
                _ when type == typeof(PhysicalBookModel) => new PhysicalBookModel { Id = Guid.NewGuid(), Title = $"Book-{suffix}" },
                _ when type == typeof(EBookModel) => new EBookModel { Id = Guid.NewGuid(), Title = $"Book-{suffix}" },
                _ when type == typeof(AudioBookModel) => new AudioBookModel { Id = Guid.NewGuid(), Title = $"Book-{suffix}" },
                _ => new EBookModel { Id = Guid.NewGuid(), Title = $"Book-{suffix}" },
            };
            book.WorkId = work.Id;
            var note = new NoteModel { Id = Guid.NewGuid(), BookId = book.Id, Content = $"note-{suffix}" };

            object entity = type switch
            {
                _ when type == typeof(PhysicalBookModel) => book,
                _ when type == typeof(EBookModel) => book,
                _ when type == typeof(AudioBookModel) => book,
                _ when type == typeof(WorkModel) => work,
                _ when type == typeof(CollectionModel) => collection,
                _ when type == typeof(BookCollectionModel) => new BookCollectionModel { BookId = book.Id, CollectionId = collection.Id, AddedAt = DateTime.UtcNow },
                _ when type == typeof(NoteModel) => note,
                _ when type == typeof(TopicModel) => topic,
                _ when type == typeof(NoteTopicModel) => new NoteTopicModel { NoteId = note.Id, TopicId = topic.Id },
                _ when type == typeof(WritingModel) => writing,
                _ when type == typeof(WritingNoteModel) => new WritingNoteModel { WritingId = writing.Id, NoteId = note.Id, AddedAt = DateTime.UtcNow },
                _ when type == typeof(BookAcquisitionModel) => new BookAcquisitionModel
                {
                    Id = Guid.NewGuid(),
                    BookId = book.Id,
                    ProviderId = "p",
                    ProviderDisplayName = "P",
                    ExternalId = "1",
                    AssetId = "a",
                    AcquiredAt = DateTime.UtcNow,
                },
                _ when type == typeof(AssistantSettingsModel) => new AssistantSettingsModel { Id = AssistantSettingsModel.SingletonId },
                _ when type == typeof(NoteImportBookLink) => new NoteImportBookLink
                {
                    Id = Guid.NewGuid(),
                    BookId = book.Id,
                    Source = "s",
                    SourceKey = "k",
                },
                _ => throw new InvalidOperationException($"No factory for {type.Name}"),
            };

            // Principals are every supporting row except the entity itself, so
            // the entity is added exactly once by the test body.
            var principals = new List<object> { work, collection, topic, writing, book, note }
                .Where(candidate => !ReferenceEquals(candidate, entity))
                .ToArray();

            return new RootScenario(entity, KeyOf(entity), principals);
        }

        internal static object?[] KeyOf(object entity) => entity switch
        {
            PhysicalBookModel book => new object?[] { book.Id },
            EBookModel book => new object?[] { book.Id },
            AudioBookModel book => new object?[] { book.Id },
            WorkModel work => new object?[] { work.Id },
            CollectionModel collection => new object?[] { collection.Id },
            BookCollectionModel membership => new object?[] { membership.BookId, membership.CollectionId },
            NoteModel note => new object?[] { note.Id },
            TopicModel topic => new object?[] { topic.Id },
            NoteTopicModel link => new object?[] { link.NoteId, link.TopicId },
            WritingModel writing => new object?[] { writing.Id },
            WritingNoteModel link => new object?[] { link.WritingId, link.NoteId },
            BookAcquisitionModel acquisition => new object?[] { acquisition.Id },
            AssistantSettingsModel settings => new object?[] { settings.Id },
            NoteImportBookLink link => new object?[] { link.Id },
            _ => throw new InvalidOperationException($"No key for {entity.GetType().Name}"),
        };

        internal static void Mutate(object entity)
        {
            switch (entity)
            {
                case PhysicalBookModel book: book.Title = "Changed"; break;
                case EBookModel book: book.Title = "Changed"; break;
                case AudioBookModel book: book.Title = "Changed"; break;
                case WorkModel work: work.Title = "Changed"; break;
                case CollectionModel collection: collection.Name = "Changed"; break;
                case BookCollectionModel membership: membership.AddedAt = membership.AddedAt.AddMinutes(1); break;
                case NoteModel note: note.Content = "Changed"; break;
                case TopicModel topic: topic.Topic = "Changed-" + Guid.NewGuid().ToString("N"); break;
                case WritingModel writing: writing.Name = "Changed"; break;
                case WritingNoteModel link: link.AddedAt = link.AddedAt.AddMinutes(1); break;
                case BookAcquisitionModel acquisition: acquisition.ProviderDisplayName = "Changed"; break;
                case AssistantSettingsModel settings: settings.CaptureProcessingMode = "clarify"; break;
                case NoteImportBookLink link: link.SourceKey = "changed"; break;
                default: throw new InvalidOperationException($"No mutation for {entity.GetType().Name}");
            }
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Nostos.Product");
            if (Directory.Exists(candidate))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

using System.Data.Common;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Portability;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

public sealed class PortableExportSnapshotTests
{
    private static readonly byte[] BookContent =
        Encoding.UTF8.GetBytes("EPUB-CONTENT-PORTABLE");

    private static readonly byte[] CoverContent =
        Encoding.UTF8.GetBytes("COVER-CONTENT-PORTABLE");

    [Fact]
    public async Task Export_data_reflects_snapshot_and_ignores_relational_writes_during_media_pinning()
    {
        await using var source = await LocalPortableTestLibrary.CreateAsync();
        var bookId = await PopulateMinimalLibraryAsync(source);
        var wrote = false;

        var storage = new ExportProbeStorage(source.Storage)
        {
            OnFirstOpenAsync = async ct =>
            {
                wrote = true;
                await WriteLateBookAsync(source, ct);
            },
        };

        await AssertExportExcludesLateBookAsync(source, storage, bookId);
        wrote.Should().BeTrue("the concurrent write must run during the pin pass");
    }

    [Fact]
    public async Task Export_data_reflects_snapshot_and_ignores_relational_writes_during_archive_copy_pass()
    {
        await using var source = await LocalPortableTestLibrary.CreateAsync();
        var bookId = await PopulateMinimalLibraryAsync(source);
        var wrote = false;

        var storage = new ExportProbeStorage(source.Storage)
        {
            TargetBookId = bookId,
            OnCopyPassOpenAsync = async ct =>
            {
                wrote = true;
                await WriteLateBookAsync(source, ct);
            },
        };

        await AssertExportExcludesLateBookAsync(source, storage, bookId);
        wrote.Should().BeTrue(
            "the concurrent write must run after pinning, during the archive copy pass");
    }

    [Fact]
    public async Task Export_timestamp_is_captured_inside_the_relational_snapshot_boundary()
    {
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var interceptor = new TransactionStartClockAdvancer(clock);

        await using var source = await LocalPortableTestLibrary.CreateAsync(
            options => options.AddInterceptors(interceptor));
        await PopulateMinimalLibraryAsync(source);

        var beforeExport = clock.GetUtcNow();

        using var archive = new MemoryStream();
        await source.Portability(clock).ExportAsync(archive);

        // The transaction-start interceptor advances the clock exactly once, so
        // a timestamp read before BeginTransactionAsync would be beforeExport.
        clock.GetUtcNow().Should().Be(beforeExport.AddMinutes(1));

        var entries = await ReadArchiveAsync(archive);
        var manifest = JsonSerializer.Deserialize<PortableArchiveManifest>(
            entries["manifest.json"],
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            })!;

        manifest.ExportedAtUtc.Should().Be(clock.GetUtcNow().UtcDateTime);
    }

    private static async Task AssertExportExcludesLateBookAsync(
        LocalPortableTestLibrary source,
        ExportProbeStorage storage,
        Guid bookId)
    {
        var service = new PortableArchiveService(
            source.Db,
            storage,
            NullLogger<PortableArchiveService>.Instance);

        using var archive = new MemoryStream();
        var exported = await service.ExportAsync(archive);

        exported.Counts.Books.Should().Be(1);
        exported.Counts.Works.Should().Be(1);

        var entries = await ReadArchiveAsync(archive);
        var library = JsonDocument.Parse(entries["data/library.json"]);
        library.RootElement.GetProperty("books").GetArrayLength().Should().Be(1);
        library.RootElement.GetProperty("works").GetArrayLength().Should().Be(1);
        library.RootElement.GetProperty("books")[0]
            .GetProperty("id").GetGuid().Should().Be(bookId);
        Encoding.UTF8.GetString(entries["data/library.json"])
            .Should().NotContain("Late Book");

        var manifest = JsonDocument.Parse(entries["manifest.json"]);
        manifest.RootElement.GetProperty("counts")
            .GetProperty("books").GetInt32().Should().Be(1);

        // The exported snapshot must stay internally consistent: a full import
        // into an empty library has to verify.
        await using var destination = await LocalPortableTestLibrary.CreateAsync();
        archive.Position = 0;
        var imported = await destination.Portability().ImportAsync(archive);
        imported.IntegrityVerified.Should().BeTrue();
        imported.Counts.Books.Should().Be(1);
        imported.Counts.Works.Should().Be(1);
    }

    private static async Task WriteLateBookAsync(
        LocalPortableTestLibrary library,
        CancellationToken ct)
    {
        var lateWork = new WorkModel
        {
            Id = Guid.NewGuid(),
            Title = "Late Work",
            NormalizedTitle = "LATE WORK",
            NormalizedAuthor = string.Empty,
            CreatedAt = DateTime.UtcNow,
        };
        var lateBook = new PhysicalBookModel
        {
            Id = Guid.NewGuid(),
            WorkId = lateWork.Id,
            Work = lateWork,
            Title = "Late Book",
            CreatedAt = DateTime.UtcNow,
        };

        library.Db.Works.Add(lateWork);
        library.Db.Books.Add(lateBook);
        await library.Db.SaveChangesAsync(ct);
    }

    [Fact]
    public async Task Export_fails_when_media_content_changes_with_same_length_after_pinning()
    {
        await using var source = await LocalPortableTestLibrary.CreateAsync();
        var bookId = await PopulateMinimalLibraryAsync(source);

        var mutated = BookContent.ToArray();
        mutated[0] ^= 0xFF;
        mutated.LongLength.Should().Be(BookContent.LongLength);

        var storage = new ExportProbeStorage(source.Storage)
        {
            TargetBookId = bookId,
            CopyPassContent = mutated,
        };
        var service = new PortableArchiveService(
            source.Db,
            storage,
            NullLogger<PortableArchiveService>.Instance);

        using var archive = new MemoryStream();
        var action = () => service.ExportAsync(archive);
        var exception = await action.Should().ThrowAsync<PortableArchiveException>();

        exception.Which.Code.Should().Be("source_media_changed");
    }

    [Fact]
    public async Task Export_fails_when_media_length_changes_after_pinning()
    {
        await using var source = await LocalPortableTestLibrary.CreateAsync();
        var bookId = await PopulateMinimalLibraryAsync(source);

        var storage = new ExportProbeStorage(source.Storage)
        {
            TargetBookId = bookId,
            ReportLongerAtCopyStart = true,
        };
        var service = new PortableArchiveService(
            source.Db,
            storage,
            NullLogger<PortableArchiveService>.Instance);

        using var archive = new MemoryStream();
        var action = () => service.ExportAsync(archive);
        var exception = await action.Should().ThrowAsync<PortableArchiveException>();

        exception.Which.Code.Should().Be("source_media_changed");
    }

    [Fact]
    public async Task Export_fails_when_media_metadata_changes_after_copy()
    {
        await using var source = await LocalPortableTestLibrary.CreateAsync();
        var bookId = await PopulateMinimalLibraryAsync(source);

        var storage = new ExportProbeStorage(source.Storage)
        {
            TargetBookId = bookId,
            ReportChangedMetadataAfterCopy = true,
        };
        var service = new PortableArchiveService(
            source.Db,
            storage,
            NullLogger<PortableArchiveService>.Instance);

        using var archive = new MemoryStream();
        var action = () => service.ExportAsync(archive);
        var exception = await action.Should().ThrowAsync<PortableArchiveException>();

        exception.Which.Code.Should().Be("source_media_changed");
    }

    [Fact]
    public async Task Export_fails_with_source_media_missing_when_referenced_media_is_absent()
    {
        await using var source = await LocalPortableTestLibrary.CreateAsync();
        var bookId = await PopulateMinimalLibraryAsync(source);

        await source.Storage.DeleteBookFileAsync(bookId);

        using var archive = new MemoryStream();
        var action = () => source.Portability().ExportAsync(archive);
        var exception = await action.Should().ThrowAsync<PortableArchiveException>();

        exception.Which.Code.Should().Be("source_media_missing");
    }

    [Fact]
    public async Task Export_manifest_hashes_match_pinned_media_and_archive_bytes()
    {
        await using var source = await LocalPortableTestLibrary.CreateAsync();
        await PopulateMinimalLibraryAsync(source);

        using var archive = new MemoryStream();
        var exported = await source.Portability().ExportAsync(archive);
        exported.MediaFiles.Should().Be(2);

        var entries = await ReadArchiveAsync(archive);
        var manifest = JsonSerializer.Deserialize<PortableArchiveManifest>(
            entries["manifest.json"],
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            })!;

        manifest.Media.Should().HaveCount(2);
        foreach (var media in manifest.Media)
        {
            var bytes = entries[media.Path];
            bytes.LongLength.Should().Be(media.Length);
            Sha256Hex(bytes).Should().Be(media.Sha256);
        }

        var book = manifest.Media.Single(
            media => media.Kind == PortableArchiveFormat.BookMediaKind);
        var cover = manifest.Media.Single(
            media => media.Kind == PortableArchiveFormat.CoverMediaKind);

        book.Sha256.Should().Be(Sha256Hex(BookContent));
        book.Length.Should().Be(BookContent.LongLength);
        entries[book.Path].Should().Equal(BookContent);

        cover.Sha256.Should().Be(Sha256Hex(CoverContent));
        cover.Length.Should().Be(CoverContent.LongLength);
        entries[cover.Path].Should().Equal(CoverContent);
    }

    [Fact]
    public async Task Export_reads_media_only_after_the_relational_transaction_completes()
    {
        await using var source = await LocalPortableTestLibrary.CreateAsync();
        await PopulateMinimalLibraryAsync(source);

        var storage = new ExportProbeStorage(source.Storage)
        {
            Db = source.Db,
            ProbeTransactionOnOpen = true,
        };
        var service = new PortableArchiveService(
            source.Db,
            storage,
            NullLogger<PortableArchiveService>.Instance);

        using var archive = new MemoryStream();
        await service.ExportAsync(archive);

        storage.TransactionOpenDuringOpen.Should().NotBeEmpty();
        storage.TransactionOpenDuringOpen.Should().OnlyContain(open => !open);
    }

    [Fact]
    public async Task Export_runs_every_relational_read_inside_one_transaction()
    {
        var interceptor = new RelationalCommandInterceptor();
        await using var source = await LocalPortableTestLibrary.CreateAsync(
            options => options.AddInterceptors(interceptor));
        await PopulateMinimalLibraryAsync(source);
        interceptor.Commands.Clear();

        using var archive = new MemoryStream();
        await source.Portability().ExportAsync(archive);

        var reads = interceptor.Commands
            .Where(command => command.CommandText
                .TrimStart()
                .StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            .ToList();

        reads.Should().NotBeEmpty(
            "the export materializes the portable relational snapshot with queries");
        reads.Should().OnlyContain(command => command.InTransaction);
        reads.Select(command => command.Transaction)
            .Distinct()
            .Should().HaveCount(1, "one read transaction owns every export query");
    }

    private static async Task<Guid> PopulateMinimalLibraryAsync(
        LocalPortableTestLibrary library)
    {
        var work = new WorkModel
        {
            Id = Guid.NewGuid(),
            Title = "Snapshot Work",
            NormalizedTitle = "SNAPSHOT WORK",
            NormalizedAuthor = string.Empty,
            CreatedAt = DateTime.UtcNow.AddDays(-1),
        };
        var book = new EBookModel
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            Work = work,
            Title = "Snapshot Book",
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            FileDetails = new FileInfoDetails
            {
                HasFile = true,
                FileName = "source.epub",
                CoverFileName = "cover.jpg",
            },
        };

        library.Db.Works.Add(work);
        library.Db.Books.Add(book);
        await library.Db.SaveChangesAsync();

        await using (var content = new MemoryStream(BookContent))
            await library.Storage.SaveBookFileAsync(book.Id, content, "source.epub");
        await using (var cover = new MemoryStream(CoverContent))
            await library.Storage.SaveBookCoverAsync(book.Id, cover, "cover.jpg");

        return book.Id;
    }

    private static async Task<Dictionary<string, byte[]>> ReadArchiveAsync(
        MemoryStream archive)
    {
        archive.Position = 0;
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        using (var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true))
        {
            foreach (var entry in zip.Entries)
            {
                await using var input = entry.Open();
                using var output = new MemoryStream();
                await input.CopyToAsync(output);
                entries[entry.FullName] = output.ToArray();
            }
        }

        archive.Position = 0;
        return entries;
    }

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal sealed class ExportProbeStorage(IBookAssetStorage inner)
        : DelegatingBookAssetStorage(inner)
    {
        private readonly Dictionary<(Guid BookId, string Kind), int> _infoCalls = new();
        private readonly Dictionary<(Guid BookId, string Kind), int> _openCalls = new();
        private bool _firstOpenInvoked;

        public Guid? TargetBookId { get; init; }

        public string TargetKind { get; init; } =
            PortableArchiveFormat.BookMediaKind;

        public byte[]? CopyPassContent { get; init; }

        public bool ReportLongerAtCopyStart { get; init; }

        public bool ReportChangedMetadataAfterCopy { get; init; }

        public NostosDbContext? Db { get; init; }

        public bool ProbeTransactionOnOpen { get; init; }

        public Func<CancellationToken, Task>? OnFirstOpenAsync { get; init; }

        public Func<CancellationToken, Task>? OnCopyPassOpenAsync { get; init; }

        public List<bool> TransactionOpenDuringOpen { get; } = [];

        public override Task<StoredAssetInfo?> GetBookFileInfoAsync(
            Guid bookId,
            CancellationToken ct = default) =>
            NextInfoAsync(bookId, PortableArchiveFormat.BookMediaKind, ct);

        public override Task<StoredAssetRead?> OpenBookFileAsync(
            Guid bookId,
            StorageByteRange? range = null,
            CancellationToken ct = default) =>
            NextReadAsync(bookId, PortableArchiveFormat.BookMediaKind, ct, range);

        public override Task<StoredAssetInfo?> GetBookCoverInfoAsync(
            Guid bookId,
            CancellationToken ct = default) =>
            NextInfoAsync(bookId, PortableArchiveFormat.CoverMediaKind, ct);

        public override Task<StoredAssetRead?> OpenBookCoverAsync(
            Guid bookId,
            CancellationToken ct = default) =>
            NextReadAsync(bookId, PortableArchiveFormat.CoverMediaKind, ct, range: null);

        private async Task<StoredAssetInfo?> NextInfoAsync(
            Guid bookId,
            string kind,
            CancellationToken ct)
        {
            var info = kind == PortableArchiveFormat.BookMediaKind
                ? await Inner.GetBookFileInfoAsync(bookId, ct)
                : await Inner.GetBookCoverInfoAsync(bookId, ct);

            if (info is null || TargetBookId != bookId || TargetKind != kind)
                return info;

            var calls = Increment(_infoCalls, (bookId, kind));

            // Pin pass: calls 1 (pre) and 2 (post). Copy pass: 3 (pre) and
            // 4 (post-copy observation).
            if (ReportLongerAtCopyStart && calls == 3)
                return info with { Length = info.Length + 1 };

            if (ReportChangedMetadataAfterCopy && calls == 4)
            {
                return info with
                {
                    EntityTag = "\"changed-after-copy\"",
                    LastModified = info.LastModified.AddMinutes(1),
                };
            }

            return info;
        }

        private async Task<StoredAssetRead?> NextReadAsync(
            Guid bookId,
            string kind,
            CancellationToken ct,
            StorageByteRange? range)
        {
            if (OnFirstOpenAsync is { } onFirstOpen && !_firstOpenInvoked)
            {
                _firstOpenInvoked = true;
                await onFirstOpen(ct);
            }

            if (Db is not null && ProbeTransactionOnOpen)
            {
                TransactionOpenDuringOpen.Add(
                    Db.Database.CurrentTransaction is not null);
            }

            if (TargetBookId == bookId && TargetKind == kind)
            {
                var calls = Increment(_openCalls, (bookId, kind));

                // Pin pass open: call 1. Copy pass open: call 2, after every
                // media item has already been pinned.
                if (calls == 2 && OnCopyPassOpenAsync is { } onCopyPassOpen)
                    await onCopyPassOpen(ct);

                if (CopyPassContent is not null && calls == 2)
                {
                    return new StoredAssetRead(
                        await InfoForContentAsync(bookId, kind, ct),
                        new MemoryStream(CopyPassContent, writable: false));
                }
            }

            return kind == PortableArchiveFormat.BookMediaKind
                ? await Inner.OpenBookFileAsync(bookId, range, ct)
                : await Inner.OpenBookCoverAsync(bookId, ct);
        }

        private async Task<StoredAssetInfo> InfoForContentAsync(
            Guid bookId,
            string kind,
            CancellationToken ct)
        {
            var info = kind == PortableArchiveFormat.BookMediaKind
                ? await Inner.GetBookFileInfoAsync(bookId, ct)
                : await Inner.GetBookCoverInfoAsync(bookId, ct);

            return info
                ?? throw new InvalidOperationException(
                    $"Storage test double has no info for {bookId}/{kind}.");
        }

        private static int Increment(
            Dictionary<(Guid BookId, string Kind), int> counters,
            (Guid BookId, string Kind) key)
        {
            var next = counters.GetValueOrDefault(key) + 1;
            counters[key] = next;
            return next;
        }
    }

    private sealed class TransactionStartClockAdvancer(ManualTimeProvider clock)
        : DbTransactionInterceptor
    {
        public override DbTransaction TransactionStarted(
            DbConnection connection,
            TransactionEndEventData eventData,
            DbTransaction result)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            return base.TransactionStarted(connection, eventData, result);
        }

        public override ValueTask<DbTransaction> TransactionStartedAsync(
            DbConnection connection,
            TransactionEndEventData eventData,
            DbTransaction result,
            CancellationToken cancellationToken = default)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            return base.TransactionStartedAsync(
                connection,
                eventData,
                result,
                cancellationToken);
        }
    }

    private sealed class RelationalCommandInterceptor : DbCommandInterceptor
    {
        public List<RecordedCommand> Commands { get; } = [];

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Record(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.ReaderExecutingAsync(
                command,
                eventData,
                result,
                cancellationToken);
        }

        private void Record(DbCommand command) =>
            Commands.Add(new RecordedCommand(command.CommandText, command.Transaction));

        internal sealed record RecordedCommand(
            string CommandText,
            DbTransaction? Transaction)
        {
            public bool InTransaction => Transaction is not null;
        }
    }
}

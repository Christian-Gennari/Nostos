using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Tests.Portability;
using Nostos.Backend.Tests.Support;
using Nostos.Product.BookText;
using Nostos.Product.Storage;
using Nostos.Shared.Dtos;
using Nostos.Shared.Enums;
using Xunit;

namespace Nostos.Backend.Tests.BookText;

public sealed class BookTextPipelineComponentTests
{
    [Theory]
    [InlineData("book.pdf", true, BookTextSourceFormat.Pdf)]
    [InlineData("BOOK.PDF", true, BookTextSourceFormat.Pdf)]
    [InlineData("novel.epub", true, BookTextSourceFormat.Epub)]
    [InlineData("NOVEL.EPUB", true, BookTextSourceFormat.Epub)]
    [InlineData("audio.mp3", false, default(BookTextSourceFormat))]
    [InlineData("", false, default(BookTextSourceFormat))]
    [InlineData(null, false, default(BookTextSourceFormat))]
    public void Format_resolver_handles_supported_and_unsupported_filenames(
        string? fileName,
        bool expectedMatched,
        BookTextSourceFormat expectedFormat)
    {
        var matched = BookTextFormatResolver.TryResolve(fileName, out var format);

        matched.Should().Be(expectedMatched);
        format.Should().Be(expectedFormat);
    }

    [Fact]
    public void Normalization_collapses_whitespace_and_preserves_clean_line_breaks()
    {
        BookTextNormalization.Normalize(null).Should().BeEmpty();
        BookTextNormalization.Normalize("   \t\r\n  ").Should().BeEmpty();
        BookTextNormalization.Normalize("  Hello \t  world  \r\n   next\tline  \r final  ")
            .Should().Be("Hello world\nnext line\nfinal");
    }

    [Fact]
    public void Identity_produces_deterministic_chunk_ids()
    {
        var revA = new BookTextSourceRevision(
            Guid.Parse("00000000-0000-0000-0000-000000000101"),
            new string('a', 64),
            BookTextArtifactSchema.CurrentExtractorVersion,
            BookTextSourceFormat.Epub);

        var revB = revA with { SourceSha256 = new string('b', 64) };

        var id0A = BookTextIdentity.ChunkId(revA, 0);
        var id0AAgain = BookTextIdentity.ChunkId(revA, 0);
        var id1A = BookTextIdentity.ChunkId(revA, 1);
        var id0B = BookTextIdentity.ChunkId(revB, 0);

        id0A.Should().Be(id0AAgain);
        id0A.Should().NotBe(id1A);
        id0A.Should().NotBe(id0B);
    }

    [Fact]
    public async Task NoOp_implementations_complete_without_side_effects()
    {
        var bookId = Guid.NewGuid();
        var revision = new BookTextSourceRevision(
            bookId,
            new string('c', 64),
            BookTextArtifactSchema.CurrentExtractorVersion,
            BookTextSourceFormat.Pdf);

        var index = new NoOpBookTextIndex();
        await index.EnsureSchemaAsync();
        await index.ScheduleAsync(bookId, "book.pdf", BookTextSourceFormat.Pdf);
        (await index.TryClaimNextAsync(TimeSpan.FromMinutes(15))).Should().BeNull();
        (await index.ReplaceReadyAsync(revision, [], 0, 1)).Should().BeFalse();
        (await index.MarkFailedAsync(bookId, "err", "msg", false, 1)).Should().BeFalse();
        await index.DeleteBookAsync(bookId);
        (await index.GetStateAsync(bookId)).Should().BeNull();
        (await index.SearchAsync("query", [bookId], 10)).Should().BeEmpty();
        (await index.GetNeighborsAsync(bookId, revision.SourceSha256, revision.ExtractorVersion, 0, 1)).Should().BeEmpty();
        (await index.GetStatesAsync([bookId])).Should().BeEmpty();
        (await index.GetNeighborsManyAsync([new BookTextNeighborQuery(bookId, revision.SourceSha256, revision.ExtractorVersion, 0)], 1)).Should().BeEmpty();

        var storage = new NoOpBookTextArtifactStorage();
        await storage.WriteAsync(revision, (_, _) => Task.CompletedTask);
        await storage.DeleteBookArtifactsAsync(bookId);
        await storage.PruneBookArtifactsAsync(bookId, revision);

        var scheduler = new NoOpBookTextIngestionScheduler();
        await scheduler.ScheduleAsync(bookId, "book.pdf");
    }

    [Fact]
    public async Task Lifecycle_deletes_index_and_artifacts()
    {
        var bookId = Guid.NewGuid();
        var index = new FakeBookTextIndex();
        var artifacts = new FakeArtifactStorage();
        var lifecycle = new BookTextLifecycle(index, artifacts);

        await lifecycle.DeleteAsync(bookId);

        index.DeletedBooks.Should().Equal(bookId);
        artifacts.DeletedBooks.Should().Equal(bookId);
    }

    [Fact]
    public async Task Scheduler_ignores_unsupported_formats_and_swallows_non_cancellation_errors()
    {
        var bookId = Guid.NewGuid();
        var index = new FakeBookTextIndex { ThrowOnSchedule = new InvalidOperationException("transient") };
        var artifacts = new FakeArtifactStorage();
        var scheduler = new BookTextIngestionScheduler(
            index,
            artifacts,
            NullLogger<BookTextIngestionScheduler>.Instance);

        await scheduler.ScheduleAsync(bookId, "cover.jpg");
        index.ScheduledBooks.Should().BeEmpty();

        // Non-cancellation error should be logged and swallowed so uploads stay valid.
        await scheduler.ScheduleAsync(bookId, "book.epub");
        artifacts.DeletedBooks.Should().BeEmpty();

        // Explicit cancellation must propagate.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        index.ThrowOnSchedule = new OperationCanceledException(cts.Token);
        await FluentActions.Awaiting(() => scheduler.ScheduleAsync(bookId, "book.epub", cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Ingestion_engine_processes_writes_artifacts_and_prunes_older_revisions()
    {
        var bookId = Guid.NewGuid();
        var payload = Encoding.UTF8.GetBytes("Valid synthetic publication payload for ingestion testing.");
        var storage = new FakeBookStorage("sample.epub", payload);
        var artifacts = new FakeArtifactStorage();
        var index = new FakeBookTextIndex { ReplaceReadyResult = true };
        var extractor = new StubExtractor(
            BookTextSourceFormat.Epub,
            new BookTextExtractedDocument(
                BookTextSourceFormat.Epub,
                [
                    new BookTextArtifactBlock(
                        0,
                        "This is a sufficiently long extracted paragraph for indexing and artifact persistence.",
                        ["Chapter One"],
                        [new BookTextSourceSegment(0, 84, new EpubBookTextSourceLocator(0, "ch1.xhtml", null, 0, 84))]),
                ]));
        var engine = new BookTextIngestionEngine(
            storage,
            artifacts,
            index,
            [extractor],
            new BookTextOptions(),
            NullLogger<BookTextIngestionEngine>.Instance);

        await engine.ProcessAsync(new BookTextIngestionWork(bookId, "sample.epub", BookTextSourceFormat.Epub, 1));

        artifacts.WrittenRevisions.Should().ContainSingle(r => r.BookId == bookId);
        artifacts.PrunedRevisions.Should().ContainSingle(r => r.BookId == bookId);
        artifacts.DeletedBooks.Should().BeEmpty();
        index.ReplacedChunks.Should().NotBeEmpty();
        index.Failures.Should().BeEmpty();
    }

    [Fact]
    public async Task Ingestion_engine_stages_the_source_on_the_host_scratch_root_and_deletes_it()
    {
        var bookId = Guid.NewGuid();
        var storage = new FakeBookStorage(
            "sample.epub",
            Encoding.UTF8.GetBytes("Valid synthetic publication payload for ingestion testing."));
        var artifacts = new FakeArtifactStorage();
        var index = new FakeBookTextIndex { ReplaceReadyResult = true };
        var extractor = new StubExtractor(
            BookTextSourceFormat.Epub,
            new BookTextExtractedDocument(
                BookTextSourceFormat.Epub,
                [
                    new BookTextArtifactBlock(
                        0,
                        "This is a sufficiently long extracted paragraph for indexing and artifact persistence.",
                        ["Chapter One"],
                        [new BookTextSourceSegment(0, 84, new EpubBookTextSourceLocator(0, "ch1.xhtml", null, 0, 84))]),
                ]));
        var scratch = new RecordingProductScratchStorage(
            Path.Combine(Path.GetTempPath(), $"nostos-engine-scratch-{Guid.NewGuid():N}"));

        try
        {
            var engine = new BookTextIngestionEngine(
                storage,
                artifacts,
                index,
                [extractor],
                new BookTextOptions(),
                NullLogger<BookTextIngestionEngine>.Instance,
                scratch);

            await engine.ProcessAsync(new BookTextIngestionWork(bookId, "sample.epub", BookTextSourceFormat.Epub, 1));

            // The source is copied out of storage once, on the host's scratch
            // root, keeping the publication's extension for the extractor.
            scratch.Files.Should().ContainSingle()
                .Which.Should().StartWith(scratch.Root).And.EndWith(".epub");
            scratch.Leftovers().Should().BeEmpty("the staged source is deleted once it is indexed");
        }
        finally
        {
            if (Directory.Exists(scratch.Root))
                Directory.Delete(scratch.Root, recursive: true);
        }
    }

    [Fact]
    public async Task Ingestion_engine_deletes_its_staged_source_when_extraction_is_unsupported()
    {
        var bookId = Guid.NewGuid();
        var storage = new FakeBookStorage(
            "sample.epub",
            Encoding.UTF8.GetBytes("source bytes"));
        var artifacts = new FakeArtifactStorage();
        var index = new FakeBookTextIndex();
        var extractor = new StubExtractor(
            BookTextSourceFormat.Epub,
            new BookTextExtractedDocument(
                BookTextSourceFormat.Epub,
                [new BookTextArtifactBlock(0, "short", [], [])]));
        var scratch = new RecordingProductScratchStorage(
            Path.Combine(Path.GetTempPath(), $"nostos-engine-scratch-{Guid.NewGuid():N}"));

        try
        {
            var engine = new BookTextIngestionEngine(
                storage,
                artifacts,
                index,
                [extractor],
                new BookTextOptions(),
                NullLogger<BookTextIngestionEngine>.Instance,
                scratch);

            await engine.ProcessAsync(new BookTextIngestionWork(bookId, "sample.epub", BookTextSourceFormat.Epub, 1));

            index.Failures.Should().ContainSingle()
                .Which.Should().Be((bookId, "book_text_no_extractable_text", true, 1));
            scratch.Files.Should().ContainSingle();
            scratch.Leftovers().Should().BeEmpty("a rejected publication still deletes its staged source");
        }
        finally
        {
            if (Directory.Exists(scratch.Root))
                Directory.Delete(scratch.Root, recursive: true);
        }
    }

    [Fact]
    public async Task Ingestion_engine_cleans_up_artifacts_when_superseded_by_book_deletion()
    {
        var bookId = Guid.NewGuid();
        var storage = new FakeBookStorage("sample.epub", Encoding.UTF8.GetBytes("source bytes"));
        var artifacts = new FakeArtifactStorage();
        var index = new FakeBookTextIndex
        {
            ReplaceReadyResult = false,
            StateByBook = null,
        };
        var extractor = new StubExtractor(
            BookTextSourceFormat.Epub,
            new BookTextExtractedDocument(
                BookTextSourceFormat.Epub,
                [
                    new BookTextArtifactBlock(
                        0,
                        "This is a sufficiently long extracted paragraph that gets superseded before commit.",
                        [],
                        []),
                ]));
        var engine = new BookTextIngestionEngine(
            storage,
            artifacts,
            index,
            [extractor],
            new BookTextOptions(),
            NullLogger<BookTextIngestionEngine>.Instance);

        await engine.ProcessAsync(new BookTextIngestionWork(bookId, "sample.epub", BookTextSourceFormat.Epub, 1));

        artifacts.DeletedBooks.Should().Equal(bookId);
        artifacts.PrunedRevisions.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true, 100, BookTextSourceFormat.Epub, "short", "book_text_no_extractable_text", true)]
    [InlineData(true, 100, BookTextSourceFormat.Pdf, "short", "book_text_pdf_no_extractable_text", true)]
    [InlineData(false, 100, BookTextSourceFormat.Epub, "unused", "book_text_source_missing", true)]
    [InlineData(true, 0, BookTextSourceFormat.Epub, "unused", "book_text_source_empty", true)]
    [InlineData(true, 999999999, BookTextSourceFormat.Epub, "unused", "book_text_source_too_large", true)]
    [InlineData(true, 100, BookTextSourceFormat.Audio, "unused", "book_text_format_unsupported", true)]
    public async Task Ingestion_engine_marks_unsupported_sources_appropriately(
        bool hasFile,
        long declaredLength,
        BookTextSourceFormat format,
        string extractedText,
        string expectedCode,
        bool expectedUnsupported)
    {
        var bookId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("dummy content");
        var storage = new FakeBookStorage(
            hasFile ? "book.bin" : null,
            hasFile ? bytes : null,
            declaredLength);
        var artifacts = new FakeArtifactStorage();
        var index = new FakeBookTextIndex();
        var extractor = new StubExtractor(
            format == BookTextSourceFormat.Audio ? BookTextSourceFormat.Epub : format,
            new BookTextExtractedDocument(
                format,
                [new BookTextArtifactBlock(0, extractedText, [], [])]));
        var options = new BookTextOptions { MaxSourceBytes = 1024 };
        var engine = new BookTextIngestionEngine(
            storage,
            artifacts,
            index,
            [extractor],
            options,
            NullLogger<BookTextIngestionEngine>.Instance);

        await engine.ProcessAsync(new BookTextIngestionWork(bookId, "book.bin", format, 2));

        index.Failures.Should().ContainSingle()
            .Which.Should().Be((bookId, expectedCode, expectedUnsupported, 2));
    }

    [Fact]
    public async Task Search_service_expands_neighbors_respects_caps_and_falls_back_on_miss()
    {
        var bookId = Guid.NewGuid();
        var sha = new string('d', 64);
        var version = BookTextArtifactSchema.CurrentExtractorVersion;
        var library = new FakeLibraryService([new LibraryBookScopeEntry(bookId, "Test Book", "Author", "test.epub", true)]);
        var index = new FakeBookTextIndex();

        var hitChunk = new BookTextIndexedChunk(
            BookTextIdentity.ChunkId(new BookTextSourceRevision(bookId, sha, version, BookTextSourceFormat.Epub), 1),
            bookId, sha, version, BookTextSourceFormat.Epub, 1,
            "Primary match about lighthouse", ["Ch1"], []);
        var neighborChunk = new BookTextIndexedChunk(
            BookTextIdentity.ChunkId(new BookTextSourceRevision(bookId, sha, version, BookTextSourceFormat.Epub), 2),
            bookId, sha, version, BookTextSourceFormat.Epub, 2,
            "Neighboring context paragraph", ["Ch1"], []);

        index.StateByBook = new BookTextIngestionState(
            bookId, BookTextIngestionStatus.Ready, "test.epub", BookTextSourceFormat.Epub,
            sha, version, null, null, 1, 2, 100, DateTime.UtcNow);
        index.SearchResponder = (query, _, _) =>
            query == "lighthouse"
                ? [new BookTextSearchHit(hitChunk, 1.0)]
                : query == "harbour"
                    ? [new BookTextSearchHit(neighborChunk, 0.7)]
                    : [];
        index.NeighborsResponder = (_, _) => [hitChunk, neighborChunk];

        var service = new BookTextSearchService(index, library, new BookTextOptions
        {
            MaxPassages = 4,
            NeighborRadius = 1,
            MaxPassageChars = 200,
            MaxTotalPassageChars = 1000,
        });

        var blank = await service.SearchAsync(new BookTextSearchRequest("   "));
        blank.EvidenceAvailable.Should().BeFalse();
        blank.Passages.Should().BeEmpty();

        var exact = await service.SearchAsync(new BookTextSearchRequest("lighthouse", BookIds: [bookId]));
        exact.EvidenceAvailable.Should().BeTrue();
        exact.Passages.Select(p => p.Ordinal).Should().Equal(1, 2);

        // Relaxed fallback: full phrase misses, individual token "harbour" matches.
        var relaxed = await service.SearchAsync(new BookTextSearchRequest("missing harbour", BookIds: [bookId], NeighborRadius: 0));
        relaxed.EvidenceAvailable.Should().BeTrue();
        relaxed.Passages.Select(p => p.Ordinal).Should().Equal(2);
    }

    private sealed class StubExtractor(BookTextSourceFormat format, BookTextExtractedDocument document) : IBookTextExtractor
    {
        public BookTextSourceFormat Format => format;
        public Task<BookTextExtractedDocument> ExtractAsync(Stream source, CancellationToken ct = default) =>
            Task.FromResult(document);
    }

    private sealed class FakeBookStorage(string? fileName, byte[]? bytes, long? overrideLength = null) : DelegatingBookAssetStorage
    {
        public override Task<StoredAssetInfo?> GetBookFileInfoAsync(Guid bookId, CancellationToken ct = default) =>
            Task.FromResult(fileName is null
                ? null
                : new StoredAssetInfo(
                    fileName,
                    "application/octet-stream",
                    overrideLength ?? bytes?.LongLength ?? 0,
                    "etag",
                    DateTimeOffset.UnixEpoch));

        public override Task<StoredAssetRead?> OpenBookFileAsync(Guid bookId, StorageByteRange? range = null, CancellationToken ct = default) =>
            Task.FromResult(fileName is null || bytes is null
                ? null
                : new StoredAssetRead(
                    new StoredAssetInfo(fileName, "application/octet-stream", bytes.Length, "etag", DateTimeOffset.UnixEpoch),
                    new MemoryStream(bytes)));
    }

    private sealed class FakeArtifactStorage : IBookDerivedArtifactStorage
    {
        public List<BookTextSourceRevision> WrittenRevisions { get; } = [];
        public List<Guid> DeletedBooks { get; } = [];
        public List<BookTextSourceRevision> PrunedRevisions { get; } = [];

        public async Task WriteAsync(BookTextSourceRevision revision, Func<Stream, CancellationToken, Task> writer, CancellationToken ct = default)
        {
            WrittenRevisions.Add(revision);
            using var ms = new MemoryStream();
            await writer(ms, ct);
        }

        public Task DeleteBookArtifactsAsync(Guid bookId, CancellationToken ct = default)
        {
            DeletedBooks.Add(bookId);
            return Task.CompletedTask;
        }

        public Task PruneBookArtifactsAsync(Guid bookId, BookTextSourceRevision keep, CancellationToken ct = default)
        {
            PrunedRevisions.Add(keep);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeBookTextIndex : IBookTextIndex
    {
        public Exception? ThrowOnSchedule { get; set; }
        public bool ReplaceReadyResult { get; set; } = true;
        public BookTextIngestionState? StateByBook { get; set; }
        public Func<string, IReadOnlyList<Guid>, int, IReadOnlyList<BookTextSearchHit>>? SearchResponder { get; set; }
        public Func<IReadOnlyList<BookTextNeighborQuery>, int, IReadOnlyList<BookTextIndexedChunk>>? NeighborsResponder { get; set; }

        public List<Guid> ScheduledBooks { get; } = [];
        public List<Guid> DeletedBooks { get; } = [];
        public List<BookTextIndexedChunk> ReplacedChunks { get; } = [];
        public List<(Guid BookId, string Code, bool Unsupported, int Attempt)> Failures { get; } = [];

        public Task EnsureSchemaAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task ScheduleAsync(Guid bookId, string sourceFileName, BookTextSourceFormat format, CancellationToken ct = default)
        {
            if (ThrowOnSchedule is not null) throw ThrowOnSchedule;
            ScheduledBooks.Add(bookId);
            return Task.CompletedTask;
        }

        public Task<BookTextIngestionWork?> TryClaimNextAsync(TimeSpan staleAfter, CancellationToken ct = default) =>
            Task.FromResult<BookTextIngestionWork?>(null);

        public Task<bool> ReplaceReadyAsync(BookTextSourceRevision revision, IReadOnlyList<BookTextIndexedChunk> chunks, long characterCount, int expectedAttempt, CancellationToken ct = default)
        {
            ReplacedChunks.AddRange(chunks);
            return Task.FromResult(ReplaceReadyResult);
        }

        public Task<bool> MarkFailedAsync(Guid bookId, string errorCode, string errorMessage, bool unsupported, int expectedAttempt, CancellationToken ct = default)
        {
            Failures.Add((bookId, errorCode, unsupported, expectedAttempt));
            return Task.FromResult(true);
        }

        public Task DeleteBookAsync(Guid bookId, CancellationToken ct = default)
        {
            DeletedBooks.Add(bookId);
            return Task.CompletedTask;
        }

        public Task<BookTextIngestionState?> GetStateAsync(Guid bookId, CancellationToken ct = default) =>
            Task.FromResult(StateByBook is { } state && state.BookId == bookId ? state : null);

        public Task<IReadOnlyList<BookTextSearchHit>> SearchAsync(string query, IReadOnlyList<Guid> bookIds, int maxCandidates, CancellationToken ct = default) =>
            Task.FromResult(SearchResponder?.Invoke(query, bookIds, maxCandidates) ?? []);

        public Task<IReadOnlyList<BookTextIndexedChunk>> GetNeighborsAsync(Guid bookId, string sourceSha256, string extractorVersion, int ordinal, int radius, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BookTextIndexedChunk>>([]);

        public Task<IReadOnlyList<BookTextIndexedChunk>> GetNeighborsManyAsync(IReadOnlyList<BookTextNeighborQuery> queries, int radius, CancellationToken ct = default) =>
            Task.FromResult(NeighborsResponder?.Invoke(queries, radius) ?? []);
    }

    private sealed class FakeLibraryService(IReadOnlyList<LibraryBookScopeEntry> entries) : ILibraryService
    {
        public Task<IReadOnlyList<LibraryBookScopeEntry>> ListBookScopeAsync(
            IReadOnlyList<Guid>? bookIds,
            Guid? collectionId,
            int limit,
            CancellationToken ct = default) =>
            Task.FromResult(entries);

        public Task<LibraryCommandResultDto> ListBooksAsync(BookFilter filter, BookSort sort, string? search, int page, int pageSize, Guid? collectionId, bool? groupByWork = false, string? format = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryCommandResultDto> GetStatusCountsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryCommandResultDto> GetBookAsync(Guid bookId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryResolveResult> ResolveBookAsync(LibraryResolveBookRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryCommandResultDto> ListCollectionsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryCommandResultDto> ListCollectionCountsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryCommandResultDto> GetCollectionAsync(Guid collectionId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryCommandResultDto> CreateOrMatchBookAsync(LibraryCreateBookRequest request, bool strictConfirmation, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryCommandResultDto> AttachAcquiredAssetAsync(LibraryAttachAcquiredAssetRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryCommandResultDto> UpdateBookAsync(LibraryUpdateBookRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryCommandResultDto> LinkWorkAsync(LibraryLinkWorkRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryCommandResultDto> UnlinkWorkAsync(LibraryUnlinkWorkRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryCommandResultDto> UpdateProgressAsync(Guid bookId, string location, int percentage, double? playbackRate = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryCommandResultDto> ResetProgressAsync(Guid bookId, bool allEditions = false, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryCommandResultDto> DeleteBookAsync(Guid bookId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryCommandResultDto> SetBookStatusAsync(Guid bookId, BookStatus status, string? statusMessage = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryCommandResultDto> CreateCollectionAsync(LibraryCreateCollectionRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryCommandResultDto> RenameCollectionAsync(LibraryRenameCollectionRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryCommandResultDto> MoveCollectionAsync(LibraryMoveCollectionRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryCommandResultDto> UpdateCollectionAsync(string clientId, string idempotencyKey, Guid collectionId, string name, Guid? parentId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryCommandResultDto> DeleteCollectionAsync(LibraryDeleteCollectionRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryCommandResultDto> ApplyBookCollectionChangesAsync(Guid bookId, IReadOnlyList<Guid> addCollectionIds, IReadOnlyList<Guid> removeCollectionIds, CancellationToken ct = default) => throw new NotSupportedException();
    }
}

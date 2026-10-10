using System.Reflection;
using System.Text;
using FluentAssertions;
using Nostos.Backend.Services;
using Nostos.Product.Alignment;
using Nostos.Product.BookText;
using Xunit;

namespace Nostos.Backend.Tests.Alignment;

public sealed class AlignmentDocumentCacheTests
{
    private static readonly Guid BookId = Guid.NewGuid();

    [Fact]
    public async Task Same_revision_reuses_extraction()
    {
        var storage = CreateStorage(
            ("revision-a", Encoding.UTF8.GetBytes("epub-a")));

        var extractor = new FakeExtractor("Some extracted text.");

        var cache = new AlignmentDocumentCache(
            new AlignmentOptions());

        var first = await cache.GetAsync(
            BookId, storage, extractor);

        var second = await cache.GetAsync(
            BookId, storage, extractor);

        ReferenceEquals(first, second).Should().BeTrue();

        extractor.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Replaced_revision_does_not_reuse_old_document()
    {
        var state = new StorageState
        {
            Revision = "revision-a",
            Content = Encoding.UTF8.GetBytes("first"),
        };

        var storage = CreateStorage(state);
        var extractor = new FakeExtractor("Extracted text.");

        var cache = new AlignmentDocumentCache(
            new AlignmentOptions());

        var first = await cache.GetAsync(
            BookId, storage, extractor);

        state.Revision = "revision-b";
        state.Content = Encoding.UTF8.GetBytes("other");

        var second = await cache.GetAsync(
            BookId, storage, extractor);

        ReferenceEquals(first, second).Should().BeFalse();

        extractor.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task Revision_is_taken_from_opened_content()
    {
        var state = new StorageState
        {
            Revision = "revision-a",
            Content = Encoding.UTF8.GetBytes("first"),
        };

        var storage = CreateStorage(state);
        var extractor = new FakeExtractor("Extracted text.");

        var cache = new AlignmentDocumentCache(
            new AlignmentOptions());

        await cache.GetAsync(BookId, storage, extractor);

        state.Revision = "revision-b";
        state.Content = Encoding.UTF8.GetBytes("other");

        await cache.GetAsync(BookId, storage, extractor);

        state.InfoCalls.Should().Be(0);
        state.OpenCalls.Should().Be(2);
        extractor.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task Document_larger_than_character_budget_is_not_cached()
    {
        var storage = CreateStorage(
            ("revision-a", Encoding.UTF8.GetBytes("epub")));

        var extractor = new FakeExtractor(
            new string('x', 101));

        var cache = new AlignmentDocumentCache(
            new AlignmentOptions
            {
                MaxCachedCharacters = 100,
            });

        await cache.GetAsync(BookId, storage, extractor);
        await cache.GetAsync(BookId, storage, extractor);

        extractor.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task Character_budget_evicts_oldest_document()
    {
        var state = new StorageState
        {
            Revision = "revision-a",
            Content = Encoding.UTF8.GetBytes("first"),
        };

        var storage = CreateStorage(state);
        var extractor = new FakeExtractor(
            new string('x', 60));

        var cache = new AlignmentDocumentCache(
            new AlignmentOptions
            {
                MaxCachedCharacters = 100,
            });

        await cache.GetAsync(BookId, storage, extractor);

        state.Revision = "revision-b";
        state.Content = Encoding.UTF8.GetBytes("other");

        await cache.GetAsync(BookId, storage, extractor);

        state.Revision = "revision-a";
        state.Content = Encoding.UTF8.GetBytes("first");

        await cache.GetAsync(BookId, storage, extractor);

        extractor.CallCount.Should().Be(3);
    }

    [Fact]
    public async Task Oversized_declared_epub_is_refused_before_copy()
    {
        var state = new StorageState
        {
            Revision = "revision-a",
            Content = new byte[101],
        };

        var storage = CreateStorage(state);
        var extractor = new FakeExtractor("Text");

        var cache = new AlignmentDocumentCache(
            new AlignmentOptions
            {
                MaxEpubBytes = 100,
            });

        Func<Task> action = () =>
            cache.GetAsync(BookId, storage, extractor);

        var error = await action.Should()
            .ThrowAsync<AlignmentException>();

        error.Which.Code.Should()
            .Be("alignment_target_too_large");

        extractor.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Provider_sending_more_than_declared_length_is_refused()
    {
        var state = new StorageState
        {
            Revision = "revision-a",
            Content = new byte[101],
            DeclaredLength = 50,
        };

        var storage = CreateStorage(state);
        var extractor = new FakeExtractor("Text");

        var cache = new AlignmentDocumentCache(
            new AlignmentOptions
            {
                MaxEpubBytes = 100,
            });

        Func<Task> action = () =>
            cache.GetAsync(BookId, storage, extractor);

        var error = await action.Should()
            .ThrowAsync<AlignmentException>();

        error.Which.Code.Should()
            .Be("alignment_target_too_large");

        extractor.CallCount.Should().Be(0);
    }

    private static IBookAssetStorage CreateStorage(
        (string Revision, byte[] Content) initial)
    {
        return CreateStorage(new StorageState
        {
            Revision = initial.Revision,
            Content = initial.Content,
        });
    }

    private static IBookAssetStorage CreateStorage(
        StorageState state)
    {
        var storage =
            DispatchProxy.Create<IBookAssetStorage, StorageProxy>();

        ((StorageProxy)(object)storage).State = state;

        return storage;
    }

    private sealed class StorageState
    {
        public string Revision { get; set; } = "";
        public byte[] Content { get; set; } = [];

        public long? DeclaredLength { get; set; }

        public int InfoCalls { get; set; }
        public int OpenCalls { get; set; }
    }

    public class StorageProxy : DispatchProxy
    {
        public object State { get; set; } = null!;

        protected override object? Invoke(
            MethodInfo? targetMethod,
            object?[]? args)
        {
            var state = (StorageState)State;

            if (targetMethod?.Name == "GetBookFileInfoAsync")
            {
                state.InfoCalls++;

                return Task.FromResult<StoredAssetInfo?>(
                    CreateInfo(state));
            }

            if (targetMethod?.Name == "OpenBookFileAsync")
            {
                state.OpenCalls++;

                StoredAssetRead asset = new(
                    CreateInfo(state),
                    new MemoryStream(
                        state.Content.ToArray(),
                        writable: false));

                return Task.FromResult<StoredAssetRead?>(asset);
            }

            throw new NotSupportedException(
                $"Unexpected storage call: {targetMethod?.Name}");
        }

        private static StoredAssetInfo CreateInfo(
            StorageState state) =>
            new(
                "book.epub",
                "application/epub+zip",
                state.DeclaredLength ?? state.Content.Length,
                state.Revision,
                DateTimeOffset.UnixEpoch);
    }

    private sealed class FakeExtractor(
        string text) : IBookTextExtractor
    {
        public int CallCount { get; private set; }

        public BookTextSourceFormat Format =>
            BookTextSourceFormat.Epub;

        public Task<BookTextExtractedDocument> ExtractAsync(
            Stream source,
            CancellationToken ct = default)
        {
            CallCount++;

            var document = new BookTextExtractedDocument(
                BookTextSourceFormat.Epub,
                [
                    new BookTextArtifactBlock(
                        0,
                        text,
                        [],
                        [
                            new BookTextSourceSegment(
                                0,
                                text.Length,
                                new EpubBookTextSourceLocator(
                                    0,
                                    "chapter.xhtml",
                                    null,
                                    0,
                                    text.Length)),
                        ]),
                ]);

            return Task.FromResult(document);
        }
    }
}

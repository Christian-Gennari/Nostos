using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Providers;
using Nostos.Backend.Providers.Acquisition;
using Nostos.Backend.Providers.Contracts;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Nostos.Shared.Enums;
using Xunit;

namespace Nostos.Backend.Tests.Providers;

public sealed class AcquisitionServiceTests
{
    private static ProviderAcquisitionPlan CreateEbookPlan(
        string providerId = "fake-provider",
        string externalId = "item-123",
        string assetId = "epub-main",
        string title = "The Republic",
        string author = "Plato",
        string? description = null,
        string? language = null,
        string? partUrl = "https://example.com/item-123.epub",
        string partExtension = ".epub",
        long partBytes = 1024,
        string rights = "Public Domain in the USA",
        string? coverUrl = null)
    {
        return new ProviderAcquisitionPlan(
            ProviderId: providerId,
            ExternalId: externalId,
            Asset: new ProviderAsset(
                Id: assetId,
                Kind: ProviderMediaKind.Ebook,
                Label: "EPUB format",
                SourceFormat: "epub",
                SizeBytes: partBytes,
                IsPreferred: true),
            Metadata: new ProviderMetadata(
                Title: title,
                Author: author,
                Description: description,
                Language: language),
            Parts: new[]
            {
                new ProviderDownloadPart(new Uri(partUrl!), partExtension, partBytes)
            },
            Output: new ProviderOutput(".epub", "application/epub+zip", "EPUB"),
            Cover: coverUrl is null
                ? null
                : new ProviderCover(new Uri(coverUrl), "image/jpeg", ".jpg"),
            Source: new ProviderSourceInfo(
                ItemUrl: $"https://example.com/items/{externalId}",
                RightsStatement: rights));
    }

    [Fact]
    public async Task HappyPath_SinglePart_NoAssembler_AcquiresAndRecordsProvenance()
    {
        using var h = AcquisitionHarness.Create();

        var fakeProvider = new FakeContentProvider("gutenberg", "Project Gutenberg");
        var plan = CreateEbookPlan(
            providerId: "gutenberg",
            externalId: "1497",
            assetId: "epub3",
            title: "The Republic",
            author: "Plato",
            rights: "Public Domain");
        fakeProvider.PlanResult = plan;

        var registry = new ProviderRegistry(new[] { fakeProvider });
        var service = h.CreateService(registry);

        var request = new AcquisitionRequest(
            ProviderId: "gutenberg",
            ExternalId: "1497",
            AssetId: "epub3");

        var result = await service.AcquireAsync(request, null, CancellationToken.None);

        result.Outcome.Should().Be(AcquisitionOutcome.Acquired);
        result.Succeeded.Should().BeTrue();
        result.BookId.Should().NotBeNull();
        result.Book.Should().NotBeNull();
        result.Book!.Title.Should().Be("The Republic");
        result.Book.Author.Should().Be("Plato");
        result.Book.HasFile.Should().BeTrue();
        result.Book.Status.Should().Be(BookStatus.Ready);
        result.Book.StatusMessage.Should().BeNull();

        // Stored file exists in storage
        var storedFileName = h.Storage.GetBookFileName(result.BookId!.Value);
        storedFileName.Should().NotBeNull();
        File.Exists(storedFileName!).Should().BeTrue();

        // BookAcquisition row exists with the right values
        await using var db = await h.ContextFactory.CreateDbContextAsync();
        var acqRow = await db.BookAcquisitions.SingleOrDefaultAsync(a => a.BookId == result.BookId.Value);
        acqRow.Should().NotBeNull();
        acqRow!.ProviderId.Should().Be("gutenberg");
        acqRow.ExternalId.Should().Be("1497");
        acqRow.AssetId.Should().Be("epub3");
        acqRow.RightsStatement.Should().Be("Public Domain");
    }

    [Fact]
    public async Task AlreadyAcquired_RunTwice_SecondReturnsAlreadyAcquired_AndDownloadsOnlyOnce()
    {
        using var h = AcquisitionHarness.Create();

        var fakeProvider = new FakeContentProvider("gutenberg", "Project Gutenberg");
        var plan = CreateEbookPlan(providerId: "gutenberg", externalId: "100", assetId: "asset-1", title: "Moby Dick");
        fakeProvider.PlanResult = plan;

        var registry = new ProviderRegistry(new[] { fakeProvider });
        var service = h.CreateService(registry);

        // First run
        var request1 = new AcquisitionRequest("gutenberg", "100", "asset-1");
        var result1 = await service.AcquireAsync(request1, null, CancellationToken.None);
        result1.Outcome.Should().Be(AcquisitionOutcome.Acquired);
        h.Downloader.DownloadCallCount.Should().Be(1);

        // Second run with explicit AssetId (hits step 2 fast path)
        var request2 = new AcquisitionRequest("gutenberg", "100", "asset-1");
        var result2 = await service.AcquireAsync(request2, null, CancellationToken.None);
        result2.Outcome.Should().Be(AcquisitionOutcome.AlreadyAcquired);
        result2.BookId.Should().Be(result1.BookId);
        h.Downloader.DownloadCallCount.Should().Be(1, "should not download again on second run");

        // Third run without explicit AssetId (hits step 4 authoritative check once plan resolves)
        var request3 = new AcquisitionRequest("gutenberg", "100", null);
        var result3 = await service.AcquireAsync(request3, null, CancellationToken.None);
        result3.Outcome.Should().Be(AcquisitionOutcome.AlreadyAcquired);
        result3.BookId.Should().Be(result1.BookId);
        h.Downloader.DownloadCallCount.Should().Be(1, "should still not download again");
    }

    [Fact]
    public async Task AlreadyInLibrary_PreExistingBookWithFile_ReturnsAlreadyInLibrary_AndDoesNotDownload()
    {
        using var h = AcquisitionHarness.Create();

        // 1. Pre-create a book with title "War and Peace", author "Leo Tolstoy", type "ebook", HasFile = true
        var createResult = await h.Library.CreateOrMatchBookAsync(
            new LibraryCreateBookRequest("client", $"create-{Guid.NewGuid():N}", "ebook", "War and Peace", Author: "Leo Tolstoy"),
            strictConfirmation: false);
        var bookId = ((LibraryCreateOrMatchResultDto)createResult.Data!).BookId!.Value;

        // Put a fake file into storage and attach it
        var tempFile = Path.Combine(h.WorkingRootDir, "initial.epub");
        await File.WriteAllBytesAsync(tempFile, new byte[] { 1, 2, 3 });
        await h.Storage.AdoptBookFileAsync(bookId, tempFile, "book.epub");

        await h.Library.AttachAcquiredAssetAsync(new LibraryAttachAcquiredAssetRequest(
            ClientId: "client",
            IdempotencyKey: $"attach-{Guid.NewGuid():N}",
            BookId: bookId,
            FileName: "book.epub",
            ProviderId: "manual",
            ProviderDisplayName: "Manual",
            ExternalId: "initial-id",
            AssetId: "initial-asset"));

        var originalFileContent = await File.ReadAllBytesAsync(h.Storage.GetBookFileName(bookId)!);

        // 2. Set up fake provider with matching metadata
        var fakeProvider = new FakeContentProvider("gutenberg", "Project Gutenberg");
        fakeProvider.PlanResult = CreateEbookPlan(
            providerId: "gutenberg",
            externalId: "2600",
            assetId: "epub-main",
            title: "War and Peace",
            author: "Leo Tolstoy");

        var registry = new ProviderRegistry(new[] { fakeProvider });
        var service = h.CreateService(registry);

        // 3. Acquire
        var result = await service.AcquireAsync(new AcquisitionRequest("gutenberg", "2600"), null, CancellationToken.None);

        result.Outcome.Should().Be(AcquisitionOutcome.AlreadyInLibrary);
        result.BookId.Should().Be(bookId);
        h.Downloader.DownloadCallCount.Should().Be(0, "must not download at all if already in library");

        // Existing file must not be overwritten
        var currentFileContent = await File.ReadAllBytesAsync(h.Storage.GetBookFileName(bookId)!);
        currentFileContent.Should().Equal(originalFileContent);
    }

    [Fact]
    public async Task DownloadFailure_DownloaderThrowsProviderDownloadException_FailsAndCleansUp()
    {
        using var h = AcquisitionHarness.Create();

        var fakeProvider = new FakeContentProvider("gutenberg", "Project Gutenberg");
        fakeProvider.PlanResult = CreateEbookPlan(
            providerId: "gutenberg",
            externalId: "500",
            assetId: "epub-main",
            title: "Failing Download Book");

        h.Downloader.ExceptionToThrowOnDownload = new ProviderDownloadException(
            ProviderDownloadException.NotFound, "Remote server returned 404.");

        var registry = new ProviderRegistry(new[] { fakeProvider });
        var service = h.CreateService(registry);

        var result = await service.AcquireAsync(new AcquisitionRequest("gutenberg", "500"), null, CancellationToken.None);

        result.Outcome.Should().Be(AcquisitionOutcome.Failed);
        result.ErrorCode.Should().Be(ProviderDownloadException.NotFound);

        // Book row remains marked as Failed
        await using var db = await h.ContextFactory.CreateDbContextAsync();
        var book = await db.Books.SingleOrDefaultAsync(b => b.Title == "Failing Download Book");
        book.Should().NotBeNull("book row must be kept on download failure");
        book!.Status.Should().Be(BookStatus.Failed);
        book.StatusMessage.Should().Be("Remote server returned 404.");

        // No files left in storage
        Directory.GetFiles(h.BooksRootDir, "*", SearchOption.AllDirectories).Should().BeEmpty();

        // Staging directory is cleaned up
        Directory.GetDirectories(h.WorkingRootDir).Should().BeEmpty();
    }

    [Fact]
    public async Task PdfDownloadFailure_LeavesNoLocalFileOrAcquisitionProvenance()
    {
        using var h = AcquisitionHarness.Create();

        var provider = new FakeContentProvider("wikisource", "Wikisource");
        var basePlan = CreateEbookPlan(
            providerId: "wikisource",
            externalId: "Pride and Prejudice",
            assetId: "pdf",
            title: "Pride and Prejudice",
            partUrl: "https://example.com/pride.pdf",
            partExtension: ".pdf");

        provider.PlanResult = basePlan with
        {
            Asset = basePlan.Asset with
            {
                Id = "pdf",
                Label = "PDF",
                SourceFormat = "application/pdf",
            },
            Output = new ProviderOutput(".pdf", "application/pdf", "PDF"),
        };

        h.Downloader.ExceptionToThrowOnDownload = new ProviderDownloadException(
            ProviderDownloadException.NotFound,
            "PDF export returned 404.");

        var service = h.CreateService(new ProviderRegistry(new[] { provider }));

        var result = await service.AcquireAsync(
            new AcquisitionRequest("wikisource", "Pride and Prejudice", AssetId: "pdf"),
            null,
            CancellationToken.None);

        result.Outcome.Should().Be(AcquisitionOutcome.Failed);

        await using var db = await h.ContextFactory.CreateDbContextAsync();
        var book = await db.Books.SingleAsync(b => b.Title == "Pride and Prejudice");
        book.Status.Should().Be(BookStatus.Failed);
        db.BookAcquisitions.Should().BeEmpty(
            "a failed PDF export is not an acquired local edition");

        Directory.GetFiles(h.BooksRootDir, "*", SearchOption.AllDirectories).Should().BeEmpty();
        Directory.GetDirectories(h.WorkingRootDir).Should().BeEmpty();
    }

    private sealed class FailingStorageDecorator(IBookAssetStorage inner) : IBookAssetStorage
    {
        public Task<string> SaveBookFileAsync(
            Guid bookId,
            Stream content,
            string fileName,
            CancellationToken ct = default) =>
            inner.SaveBookFileAsync(bookId, content, fileName, ct);

        public Task<string> AdoptBookFileAsync(
            Guid bookId,
            string sourcePath,
            string fileName,
            CancellationToken ct = default) =>
            throw new IOException("Simulated storage disk failure during adopt.");

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
            inner.SaveBookCoverAsync(bookId, content, fileName, ct);

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

    [Fact]
    public async Task StorageFailure_AdoptBookFileAsyncThrows_RollsBackCreatedBook()
    {
        using var h = AcquisitionHarness.Create();

        var fakeProvider = new FakeContentProvider("gutenberg", "Project Gutenberg");
        fakeProvider.PlanResult = CreateEbookPlan(
            providerId: "gutenberg",
            externalId: "501",
            assetId: "epub-main",
            title: "Storage Fail Book");

        var registry = new ProviderRegistry(new[] { fakeProvider });
        var failingStorage = new FailingStorageDecorator(h.Storage);
        var service = h.CreateService(registry, storageOverride: failingStorage);

        var result = await service.AcquireAsync(new AcquisitionRequest("gutenberg", "501"), null, CancellationToken.None);

        result.Outcome.Should().Be(AcquisitionOutcome.Failed);
        result.ErrorCode.Should().Be("storage_failed");

        // The book created before adoption must remain with Failed status
        await using var db = await h.ContextFactory.CreateDbContextAsync();
        var book = await db.Books.SingleOrDefaultAsync(b => b.Title == "Storage Fail Book");
        book.Should().NotBeNull("book row must be kept on storage failure");
        book!.Status.Should().Be(BookStatus.Failed);
        book.StatusMessage.Should().Be("The file could not be stored, so nothing was imported.");

        // Staging directory cleaned up
        Directory.GetDirectories(h.WorkingRootDir).Should().BeEmpty();
    }

    [Fact]
    public async Task AssemblyFailure_AssemblerThrows_FailsAndCleansUp()
    {
        using var h = AcquisitionHarness.Create();

        var assemblingProvider = new FakeAssemblingContentProvider("assembler", "Assembling Source");
        assemblingProvider.PlanResult = new ProviderAcquisitionPlan(
            ProviderId: "assembler",
            ExternalId: "multi-1",
            Asset: new ProviderAsset("audio-asset", ProviderMediaKind.Audiobook, "Full Audiobook", "mp3-multi"),
            Metadata: new ProviderMetadata(Title: "Audiobook To Assemble"),
            Parts: new[]
            {
                new ProviderDownloadPart(new Uri("https://example.com/part1.mp3"), ".mp3", 500),
                new ProviderDownloadPart(new Uri("https://example.com/part2.mp3"), ".mp3", 500),
            },
            Output: new ProviderOutput(".m4b", "audio/mp4", "M4B"));

        assemblingProvider.ExceptionToThrowOnAssemble = new InvalidOperationException("Transcode / assemble crashed");

        var registry = new ProviderRegistry(new[] { assemblingProvider });
        var service = h.CreateService(registry);

        var result = await service.AcquireAsync(new AcquisitionRequest("assembler", "multi-1"), null, CancellationToken.None);

        result.Outcome.Should().Be(AcquisitionOutcome.Failed);
        result.ErrorCode.Should().Be("acquisition_failed");

        // Book row remains marked as Failed
        await using var db = await h.ContextFactory.CreateDbContextAsync();
        var book = await db.Books.SingleOrDefaultAsync(b => b.Title == "Audiobook To Assemble");
        book.Should().NotBeNull("book row must be kept on assembly failure");
        book!.Status.Should().Be(BookStatus.Failed);
        book.StatusMessage.Should().Be("The import failed unexpectedly.");

        // Staging root contains no directories afterwards
        Directory.GetDirectories(h.WorkingRootDir).Should().BeEmpty();
    }

    [Fact]
    public async Task Validation_EmptyParts_ReturnsProviderNoDownloadableAssets()
    {
        using var h = AcquisitionHarness.Create();

        var fakeProvider = new FakeContentProvider("gutenberg", "Project Gutenberg");
        fakeProvider.PlanResult = new ProviderAcquisitionPlan(
            ProviderId: "gutenberg",
            ExternalId: "empty-parts",
            Asset: new ProviderAsset("asset-1", ProviderMediaKind.Ebook, "Asset", "epub"),
            Metadata: new ProviderMetadata("Book Title"),
            Parts: Array.Empty<ProviderDownloadPart>(),
            Output: new ProviderOutput(".epub", "application/epub+zip", "EPUB"));

        var registry = new ProviderRegistry(new[] { fakeProvider });
        var service = h.CreateService(registry);

        var result = await service.AcquireAsync(new AcquisitionRequest("gutenberg", "empty-parts"), null, CancellationToken.None);

        result.Outcome.Should().Be(AcquisitionOutcome.Failed);
        result.ErrorCode.Should().Be(AcquisitionException.NoAssets);
        result.ErrorCode.Should().Be("provider_no_downloadable_assets");

        // Staging directory cleaned up
        Directory.GetDirectories(h.WorkingRootDir).Should().BeEmpty();
    }

    [Fact]
    public async Task Validation_TooManyParts_ReturnsDownloadTooManyParts()
    {
        using var h = AcquisitionHarness.Create();

        // Provider policy allows at most 2 parts
        var fakeProvider = new FakeContentProvider("gutenberg", "Project Gutenberg", maxParts: 2);
        fakeProvider.PlanResult = new ProviderAcquisitionPlan(
            ProviderId: "gutenberg",
            ExternalId: "too-many-parts",
            Asset: new ProviderAsset("asset-1", ProviderMediaKind.Ebook, "Asset", "epub"),
            Metadata: new ProviderMetadata("Book Title"),
            Parts: new[]
            {
                new ProviderDownloadPart(new Uri("https://example.com/1.epub"), ".epub", 100),
                new ProviderDownloadPart(new Uri("https://example.com/2.epub"), ".epub", 100),
                new ProviderDownloadPart(new Uri("https://example.com/3.epub"), ".epub", 100),
            },
            Output: new ProviderOutput(".epub", "application/epub+zip", "EPUB"));

        var registry = new ProviderRegistry(new[] { fakeProvider });
        var service = h.CreateService(registry);

        var result = await service.AcquireAsync(new AcquisitionRequest("gutenberg", "too-many-parts"), null, CancellationToken.None);

        result.Outcome.Should().Be(AcquisitionOutcome.Failed);
        result.ErrorCode.Should().Be(AcquisitionException.TooManyParts);
        result.ErrorCode.Should().Be("download_too_many_parts");

        // Staging directory cleaned up
        Directory.GetDirectories(h.WorkingRootDir).Should().BeEmpty();
    }

    [Fact]
    public async Task Cleanup_OnAnyFailingAcquisition_StagingRootContainsNoDirectoriesAfterwards()
    {
        using var h = AcquisitionHarness.Create();

        var fakeProvider = new FakeContentProvider("gutenberg", "Project Gutenberg");
        fakeProvider.PlanResult = CreateEbookPlan(
            providerId: "gutenberg",
            externalId: "fail-cleanup",
            assetId: "epub-main",
            title: "Cleanup Test Book");

        h.Downloader.ExceptionToThrowOnDownload = new ProviderDownloadException(
            ProviderDownloadException.Failed, "Network connection dropped.");

        var registry = new ProviderRegistry(new[] { fakeProvider });
        var service = h.CreateService(registry);

        var result = await service.AcquireAsync(new AcquisitionRequest("gutenberg", "fail-cleanup"), null, CancellationToken.None);
        result.Outcome.Should().Be(AcquisitionOutcome.Failed);

        // Staging root must be completely empty
        Directory.GetDirectories(h.WorkingRootDir).Should().BeEmpty();
    }

    [Fact]
    public async Task UnknownProvider_FailsWithProviderUnknown_AndTouchesNothing()
    {
        using var h = AcquisitionHarness.Create();

        var registry = new ProviderRegistry(Array.Empty<IContentProvider>());
        var service = h.CreateService(registry);

        var result = await service.AcquireAsync(
            new AcquisitionRequest("nobody", "1"), null, CancellationToken.None);

        result.Outcome.Should().Be(AcquisitionOutcome.Failed);
        result.ErrorCode.Should().Be(AcquisitionException.ProviderUnknown);
        h.Downloader.DownloadCallCount.Should().Be(0);
        Directory.GetDirectories(h.WorkingRootDir).Should().BeEmpty();
    }

    [Fact]
    public async Task ProviderWithoutAcquisitionCapability_FailsWithProviderCannotAcquire()
    {
        using var h = AcquisitionHarness.Create();

        var registry = new ProviderRegistry(new[] { new SearchOnlyProvider() });
        var service = h.CreateService(registry);

        var result = await service.AcquireAsync(
            new AcquisitionRequest("search-only", "1"), null, CancellationToken.None);

        result.Outcome.Should().Be(AcquisitionOutcome.Failed);
        result.ErrorCode.Should().Be(AcquisitionException.ProviderCannotAcquire);
        h.Downloader.DownloadCallCount.Should().Be(0);
    }

    /// <summary>
    /// Identity matching is deliberately type-blind, so acquiring an ebook can
    /// "match" a physical row on the shelf that shares title and author.
    /// Attaching an EPUB to it would be a modelling error, so the pipeline must
    /// create a proper ebook edition in the same work instead — and leave the
    /// physical book exactly as it was.
    /// </summary>
    [Fact]
    public async Task CrossTypeMatch_CreatesAnEditionOfTheSameWork_InsteadOfAttachingToThePhysicalBook()
    {
        using var h = AcquisitionHarness.Create();

        var physical = await h.Library.CreateOrMatchBookAsync(
            new LibraryCreateBookRequest(
                "client", $"create-{Guid.NewGuid():N}", "physical", "The Republic", Author: "Plato"),
            strictConfirmation: false);
        var physicalId = ((LibraryCreateOrMatchResultDto)physical.Data!).BookId!.Value;

        var fakeProvider = new FakeContentProvider("gutenberg", "Project Gutenberg");
        fakeProvider.PlanResult = CreateEbookPlan(
            providerId: "gutenberg",
            externalId: "1497",
            assetId: "epub3-images",
            title: "The Republic",
            author: "Plato");

        var service = h.CreateService(new ProviderRegistry(new[] { fakeProvider }));

        var result = await service.AcquireAsync(
            new AcquisitionRequest("gutenberg", "1497", "epub3-images"), null, CancellationToken.None);

        result.Outcome.Should().Be(AcquisitionOutcome.Acquired);
        result.BookId.Should().NotBe(physicalId, "an ebook must not be attached to a physical book row");
        result.Book!.Type.Should().Be("ebook");

        // The physical book is untouched: no file, no provenance.
        await using var db = await h.ContextFactory.CreateDbContextAsync();
        var physicalBook = await db.Books.SingleAsync(b => b.Id == physicalId);
        physicalBook.FileDetails.HasFile.Should().BeFalse();
        (await db.BookAcquisitions.CountAsync(a => a.BookId == physicalId)).Should().Be(0);

        // ...and the new edition is grouped with it, so the library still shows
        // one work rather than two.
        var acquired = await h.Library.GetBookAsync(result.BookId!.Value);
        var acquiredDto = (BookDto)acquired.Data!;
        acquiredDto.WorkId.Should().Be(physicalBook.WorkId);
    }

    // --- User-supplied metadata overrides ---------------------------------
    //
    // An import started from a prefilled form carries the user's edits. They
    // replace the created book's fields; they never take part in identity
    // matching, which stays on what the source says.

    [Fact]
    public async Task Overrides_ReplaceTheSourceMetadataOnTheCreatedBook()
    {
        using var h = AcquisitionHarness.Create();

        var fakeProvider = new FakeContentProvider("gutenberg", "Project Gutenberg");
        fakeProvider.PlanResult = CreateEbookPlan(
            providerId: "gutenberg",
            externalId: "1497",
            assetId: "epub3",
            title: "The Republic",
            author: "Plato",
            description: "The source's blurb.",
            language: "en");

        var service = h.CreateService(new ProviderRegistry(new[] { fakeProvider }));

        var request = new AcquisitionRequest(
            ProviderId: "gutenberg",
            ExternalId: "1497",
            AssetId: "epub3",
            MetadataOverrides: new ProviderMetadataOverrides(
                Title: "Politeia",
                Author: "Plato (trans. Bloom)",
                Description: "My own note about this edition.",
                Language: "grc"));

        var result = await service.AcquireAsync(request, null, CancellationToken.None);

        result.Outcome.Should().Be(AcquisitionOutcome.Acquired);
        result.Book!.Title.Should().Be("Politeia");
        result.Book.Author.Should().Be("Plato (trans. Bloom)");
        result.Book.Description.Should().Be("My own note about this edition.");
        result.Book.Language.Should().Be("grc");
    }

    [Fact]
    public async Task Overrides_LeaveUntouchedFieldsFollowingTheSource()
    {
        using var h = AcquisitionHarness.Create();

        var fakeProvider = new FakeContentProvider("gutenberg", "Project Gutenberg");
        fakeProvider.PlanResult = CreateEbookPlan(
            providerId: "gutenberg",
            externalId: "1497",
            assetId: "epub3",
            title: "The Republic",
            author: "Plato",
            description: "The source's blurb.",
            language: "en");

        var service = h.CreateService(new ProviderRegistry(new[] { fakeProvider }));

        // Only the author was touched. A client that echoed the whole form back
        // would pin the description to whatever it happened to show, so a later
        // provider-side correction could never reach the library.
        var request = new AcquisitionRequest(
            ProviderId: "gutenberg",
            ExternalId: "1497",
            AssetId: "epub3",
            MetadataOverrides: new ProviderMetadataOverrides(Author: "Plato of Athens"));

        var result = await service.AcquireAsync(request, null, CancellationToken.None);

        result.Book!.Author.Should().Be("Plato of Athens");
        result.Book.Title.Should().Be("The Republic");
        result.Book.Description.Should().Be("The source's blurb.");
        result.Book.Language.Should().Be("en");
    }

    [Fact]
    public async Task Overrides_AnEmptyStringClearsTheField()
    {
        using var h = AcquisitionHarness.Create();

        var fakeProvider = new FakeContentProvider("gutenberg", "Project Gutenberg");
        fakeProvider.PlanResult = CreateEbookPlan(
            providerId: "gutenberg",
            externalId: "1497",
            assetId: "epub3",
            title: "The Republic",
            author: "Plato",
            description: "A blurb the user does not want.");

        var service = h.CreateService(new ProviderRegistry(new[] { fakeProvider }));

        // The user emptied the box. Putting the source's text back would be worse
        // than dropping it, so an empty string means cleared, not "unchanged".
        var request = new AcquisitionRequest(
            ProviderId: "gutenberg",
            ExternalId: "1497",
            AssetId: "epub3",
            MetadataOverrides: new ProviderMetadataOverrides(Description: "   "));

        var result = await service.AcquireAsync(request, null, CancellationToken.None);

        result.Book!.Description.Should().BeNull();
    }

    [Fact]
    public async Task Overrides_A_ClearedTitleFallsBackToTheSource()
    {
        using var h = AcquisitionHarness.Create();

        var fakeProvider = new FakeContentProvider("gutenberg", "Project Gutenberg");
        fakeProvider.PlanResult = CreateEbookPlan(
            providerId: "gutenberg",
            externalId: "1497",
            assetId: "epub3",
            title: "The Republic",
            author: "Plato");

        var service = h.CreateService(new ProviderRegistry(new[] { fakeProvider }));

        var request = new AcquisitionRequest(
            ProviderId: "gutenberg",
            ExternalId: "1497",
            AssetId: "epub3",
            MetadataOverrides: new ProviderMetadataOverrides(Title: "  "));

        var result = await service.AcquireAsync(request, null, CancellationToken.None);

        // A book with no title is not a book: the source's title stands, and the
        // import still succeeds rather than failing validation.
        result.Outcome.Should().Be(AcquisitionOutcome.Acquired);
        result.Book!.Title.Should().Be("The Republic");
    }

    [Fact]
    public async Task Overrides_DoNotChangeIdentity_AnExistingLocalCopyIsStillRecognised()
    {
        using var h = AcquisitionHarness.Create();

        var fakeProvider = new FakeContentProvider("gutenberg", "Project Gutenberg");
        fakeProvider.PlanResult = CreateEbookPlan(
            providerId: "gutenberg",
            externalId: "1497",
            assetId: "epub3",
            title: "The Republic",
            author: "Plato");

        var service = h.CreateService(new ProviderRegistry(new[] { fakeProvider }));
        var first = await service.AcquireAsync(
            new AcquisitionRequest(ProviderId: "gutenberg", ExternalId: "1497", AssetId: "epub3"),
            null,
            CancellationToken.None);
        first.Outcome.Should().Be(AcquisitionOutcome.Acquired);

        // A different item of the same work, and the user renames it on the way
        // in. Identity has to stay on the SOURCE's title, or the rename would
        // hide the copy already on disk and import a duplicate.
        fakeProvider.PlanResult = CreateEbookPlan(
            providerId: "gutenberg",
            externalId: "1497-alt",
            assetId: "epub-alt",
            title: "The Republic",
            author: "Plato");

        var second = await service.AcquireAsync(
            new AcquisitionRequest(
                ProviderId: "gutenberg",
                ExternalId: "1497-alt",
                AssetId: "epub-alt",
                MetadataOverrides: new ProviderMetadataOverrides(Title: "Republic, The (my copy)")),
            null,
            CancellationToken.None);

        second.Outcome.Should().Be(AcquisitionOutcome.AlreadyInLibrary);
        second.BookId.Should().Be(first.BookId);

        await using var db = await h.ContextFactory.CreateDbContextAsync();
        (await db.Books.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Cancellation_DeletesRowCreatedByAcquisition()
    {
        using var h = AcquisitionHarness.Create();

        var fakeProvider = new FakeContentProvider("gutenberg", "Project Gutenberg");
        fakeProvider.PlanResult = CreateEbookPlan(
            providerId: "gutenberg",
            externalId: "cancel-test",
            assetId: "epub3",
            title: "Cancel Test Book");

        using var cts = new CancellationTokenSource();
        // Downloader triggers cancellation
        h.Downloader.OnDownloadAsync = _ => { cts.Cancel(); return Task.CompletedTask; };

        var service = h.CreateService(new ProviderRegistry(new[] { fakeProvider }));
        var request = new AcquisitionRequest("gutenberg", "cancel-test");

        var act = () => service.AcquireAsync(request, null, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        await using var db = await h.ContextFactory.CreateDbContextAsync();
        var book = await db.Books.SingleOrDefaultAsync(b => b.Title == "Cancel Test Book");
        book.Should().BeNull("cancelled acquisition must delete the row created by us");
    }

    [Fact]
    public async Task AcquireAsync_SetsMilestones_DownloadingThenTranscodingThenReady()
    {
        using var h = AcquisitionHarness.Create();

        var assemblingProvider = new FakeAssemblingContentProvider("assembler", "Assembling Source");
        assemblingProvider.PlanResult = new ProviderAcquisitionPlan(
            ProviderId: "assembler",
            ExternalId: "milestone-1",
            Asset: new ProviderAsset("audio-asset", ProviderMediaKind.Audiobook, "Full Audiobook", "mp3-multi"),
            Metadata: new ProviderMetadata(Title: "Milestone Audiobook"),
            Parts: new[]
            {
                new ProviderDownloadPart(new Uri("https://example.com/part1.mp3"), ".mp3", 500),
            },
            Output: new ProviderOutput(".m4b", "audio/mp4", "M4B"));

        var recordedStatuses = new List<BookStatus>();

        h.Downloader.OnDownloadAsync = async _ =>
        {
            await using var db = await h.ContextFactory.CreateDbContextAsync();
            var b = await db.Books.SingleAsync(x => x.Title == "Milestone Audiobook");
            recordedStatuses.Add(b.Status);
        };

        var registry = new ProviderRegistry(new[] { assemblingProvider });
        var service = h.CreateService(registry);

        var result = await service.AcquireAsync(new AcquisitionRequest("assembler", "milestone-1"), null, CancellationToken.None);
        result.Outcome.Should().Be(AcquisitionOutcome.Acquired);

        await using var finalDb = await h.ContextFactory.CreateDbContextAsync();
        var finalBook = await finalDb.Books.SingleAsync(x => x.Title == "Milestone Audiobook");

        recordedStatuses.Should().Contain(BookStatus.Downloading);
        finalBook.Status.Should().Be(BookStatus.Ready);
        finalBook.StatusMessage.Should().BeNull();
    }

    [Fact]
    public async Task StaleSnapshotAsset_StillOffered_DownloadsOnlyTheLivePlanUrl()
    {
        using var h = AcquisitionHarness.Create();

        // A non-live discovery backend (for example a Cloud catalog snapshot)
        // listed this item and asset at sync time, including metadata a live
        // search would not carry.
        var staleCoverUrl = new Uri("https://example.com/stale-cover.jpg");
        var snapshotItem = new ProviderItem(
            ProviderId: "gutenberg",
            ExternalId: "1497",
            MediaKind: ProviderMediaKind.Ebook,
            Metadata: new ProviderMetadata("The Republic"),
            Assets: [new ProviderAsset("epub3", ProviderMediaKind.Ebook, "EPUB3", "epub")],
            Cover: new ProviderCover(staleCoverUrl, "image/jpeg", ".jpg"));

        var liveUrl = new Uri("https://fake.org/live/1497.epub");
        var liveCoverUrl = new Uri("https://fake.org/live/1497-cover.jpg");
        var fakeProvider = new FakeContentProvider("gutenberg", "Project Gutenberg");
        fakeProvider.PlanResult = CreateEbookPlan(
            providerId: snapshotItem.ProviderId,
            externalId: snapshotItem.ExternalId,
            assetId: "epub3",
            partUrl: liveUrl.ToString(),
            coverUrl: liveCoverUrl.ToString());

        var service = h.CreateService(new ProviderRegistry([fakeProvider]));

        var result = await service.AcquireAsync(
            new AcquisitionRequest(
                snapshotItem.ProviderId,
                snapshotItem.ExternalId,
                snapshotItem.Assets[0].Id,
                IncludeCover: true),
            null,
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        fakeProvider.PlanCallCount.Should().Be(1, "the live provider plan is the only source of download locations");
        h.Downloader.RequestedUrls.Should().BeEquivalentTo(
            [liveUrl, liveCoverUrl],
            "every fetched location comes from the live plan");
        h.Downloader.RequestedUrls.Should().NotContain(
            staleCoverUrl,
            "discovery metadata is never fetched or trusted as an asset");
    }

    [Fact]
    public async Task StaleSnapshotAsset_NoLongerOffered_FailsCleanly_WithoutDownloading()
    {
        using var h = AcquisitionHarness.Create();

        var snapshotItem = new ProviderItem(
            ProviderId: "gutenberg",
            ExternalId: "1497",
            MediaKind: ProviderMediaKind.Ebook,
            Metadata: new ProviderMetadata("The Republic"),
            Assets: [new ProviderAsset("epub3", ProviderMediaKind.Ebook, "EPUB3", "epub")]);

        var fakeProvider = new FakeContentProvider("gutenberg", "Project Gutenberg");
        fakeProvider.ExceptionToThrowOnPlan = ProviderException.AssetUnavailableFor(
            snapshotItem.ProviderId,
            snapshotItem.ExternalId,
            snapshotItem.Assets[0].Id);

        var service = h.CreateService(new ProviderRegistry([fakeProvider]));

        var result = await service.AcquireAsync(
            new AcquisitionRequest(
                snapshotItem.ProviderId,
                snapshotItem.ExternalId,
                snapshotItem.Assets[0].Id),
            null,
            CancellationToken.None);

        result.Outcome.Should().Be(AcquisitionOutcome.Failed);
        result.ErrorCode.Should().Be(ProviderException.AssetUnavailable);
        fakeProvider.PlanCallCount.Should().Be(1, "the live provider is re-asked even though discovery listed the asset");
        h.Downloader.DownloadCallCount.Should().Be(0, "a snapshot row is not authority to download");
    }

    /// <summary>A provider that can only search: used to prove the acquisition
    /// surface refuses a source that cannot actually deliver anything.</summary>
    private sealed class SearchOnlyProvider : IContentProvider, IProviderSearch
    {
        public string Id => "search-only";

        public string DisplayName => "Search Only";

        public ProviderCapabilities Capabilities => ProviderCapabilities.Search;

        public string? RightsNotice => null;

        public Task<ProviderSearchPage> SearchAsync(ProviderSearchQuery query, CancellationToken ct = default) =>
            Task.FromResult(new ProviderSearchPage([]));
    }
}

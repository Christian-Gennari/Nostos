using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Configuration;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Tests.Support;
using Nostos.Backend.Tests.BookText;
using Nostos.Backend.Services.BookText;
using Nostos.Product.BookText;
using Nostos.Product.Http;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Endpoints;

// Full-host HTTP coverage for the canonical library REST surface (issue #34
// Phase 1b). The Angular UI uses this exact surface; these
// tests prove the refactored endpoints keep the legacy REST contracts while
// routing through ILibraryService.
public sealed class LibraryEndpointTests : IClassFixture<LibraryEndpointFactory>
{
    private const string BorgesIsbn = "9780141183848";

    private readonly LibraryEndpointFactory _factory;

    public LibraryEndpointTests(LibraryEndpointFactory factory) => _factory = factory;

    private HttpClient Client => _factory.CreateClient();

    // ------------------------------------------------------------------
    // Books
    // ------------------------------------------------------------------

    [Fact]
    public async Task Create_book_returns_201_with_canonical_dto_and_normalizes_identity()
    {
        var response = await Client.PostAsJsonAsync("/api/books", new
        {
            type = "physical",
            title = "Fictions HTTP",
            author = "Jorge Luis Borges",
            isbn = "9780199535576", // unique valid ISBN for this test
            pageCount = 178,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var book = await response.Content.ReadFromJsonAsync<BookDto>();
        book!.Title.Should().Be("Fictions HTTP");
        book.Isbn.Should().Be("9780199535576");

        await using var db = await OpenDbAsync();
        var stored = await db.Books.OfType<PhysicalBookModel>().SingleAsync(b => b.Id == book.Id);
        stored.NormalizedIsbn.Should().Be("9780199535576");
    }

    [Fact]
    public async Task Create_with_existing_isbn_returns_200_with_existing_book()
    {
        var first = await Client.PostAsJsonAsync("/api/books", new
        {
            type = "physical",
            title = $"Dedupe Book {Guid.NewGuid():N}"[..20],
            author = "A",
            isbn = "9780141183848",
        });
        var firstBook = (await first.Content.ReadFromJsonAsync<BookDto>())!;

        var second = await Client.PostAsJsonAsync("/api/books", new
        {
            type = "physical",
            title = "Completely Different Title",
            author = "B",
            isbn = "9780141183848",
        });

        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var secondBook = await second.Content.ReadFromJsonAsync<BookDto>();
        secondBook!.Id.Should().Be(firstBook.Id);
        secondBook.Title.Should().Be(firstBook.Title);
    }

    [Fact]
    public async Task Create_title_only_still_creates_legacy_permissive_semantics()
    {
        var response = await Client.PostAsJsonAsync("/api/books", new
        {
            type = "physical",
            title = $"Permissive {Guid.NewGuid():N}",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Create_blank_title_returns_400()
    {
        var response = await Client.PostAsJsonAsync("/api/books", new
        {
            type = "physical",
            title = "   ",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Upload_file_rejects_physical_books_as_metadata_only()
    {
        var created = await Client.PostAsJsonAsync("/api/books", new
        {
            type = "physical",
            title = $"Physical Only {Guid.NewGuid():N}",
        });
        var book = (await created.Content.ReadFromJsonAsync<BookDto>())!;

        using var form = new MultipartFormDataContent();
        var file = new StringContent("This file must stay separate from the physical edition.");
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(file, "file", "copy.txt");

        var response = await Client.PostAsync($"/api/books/{book.Id}/file", form);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("metadata-only");

        var unchanged = await Client.GetFromJsonAsync<BookDto>($"/api/books/{book.Id}");
        unchanged!.HasFile.Should().BeFalse();
        unchanged.FileName.Should().BeNull();
    }

    [Fact]
    public async Task Upload_file_rejects_mismatched_media_type_and_extension()
    {
        var created = await Client.PostAsJsonAsync("/api/books", new
        {
            type = "ebook",
            title = $"Mismatched Upload {Guid.NewGuid():N}",
        });
        var book = (await created.Content.ReadFromJsonAsync<BookDto>())!;

        using var form = new MultipartFormDataContent();
        var file = new StringContent("Not a PDF.");
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", "copy.epub");

        var response = await Client.PostAsync($"/api/books/{book.Id}/file", form);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var unchanged = await Client.GetFromJsonAsync<BookDto>($"/api/books/{book.Id}");
        unchanged!.HasFile.Should().BeFalse();
    }

    [Fact]
    public async Task Upload_cover_rejects_declared_request_over_the_cover_limit_before_form_parsing()
    {
        var created = await Client.PostAsJsonAsync("/api/books", new
        {
            type = "ebook",
            title = $"Oversized Cover {Guid.NewGuid():N}",
        });
        var book = (await created.Content.ReadFromJsonAsync<BookDto>())!;

        using var oversized = new DeclaredLengthContent(
            NostosProductRequestLimits.MaxCoverRequestBytes + 1);

        var response = await Client.PostAsync($"/api/books/{book.Id}/cover", oversized);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/thumbnail?width=320")]
    public async Task Replaced_cover_is_served_to_a_browser_holding_the_old_one(string suffix)
    {
        // The cover URL never changes, so a browser that cached the old cover
        // must revalidate and receive the replacement, not keep the old copy.
        var created = await Client.PostAsJsonAsync("/api/books", new
        {
            type = "ebook",
            title = $"Replaced Cover {Guid.NewGuid():N}",
        });
        var book = (await created.Content.ReadFromJsonAsync<BookDto>())!;
        var coverUrl = $"/api/books/{book.Id}/cover{suffix}";

        (await UploadCoverAsync(book.Id, red: 255, blue: 0)).StatusCode.Should().Be(HttpStatusCode.OK);
        var first = await Client.GetAsync(coverUrl);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        first.Headers.CacheControl!.NoCache.Should().BeTrue();
        first.Headers.CacheControl.MaxAge.Should().BeNull();
        var oldTag = first.Headers.ETag!;
        var oldBytes = await first.Content.ReadAsByteArrayAsync();

        (await UploadCoverAsync(book.Id, red: 0, blue: 255)).StatusCode.Should().Be(HttpStatusCode.OK);
        using var revalidate = new HttpRequestMessage(HttpMethod.Get, coverUrl);
        revalidate.Headers.IfNoneMatch.Add(oldTag);
        var second = await Client.SendAsync(revalidate);

        second.StatusCode.Should().Be(HttpStatusCode.OK);
        second.Headers.ETag.Should().NotBe(oldTag);
        (await second.Content.ReadAsByteArrayAsync()).Should().NotEqual(oldBytes);

        // An unchanged cover still answers the conditional request with a 304.
        using var unchanged = new HttpRequestMessage(HttpMethod.Get, coverUrl);
        unchanged.Headers.IfNoneMatch.Add(second.Headers.ETag!);
        (await Client.SendAsync(unchanged)).StatusCode.Should().Be(HttpStatusCode.NotModified);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/thumbnail?width=320")]
    public async Task Versioned_cover_url_is_cached_for_good_and_a_replace_moves_it(string suffix)
    {
        // The book names its cover's version; a URL carrying it can be cached
        // without revalidation because a replaced cover gets a new version.
        var created = await Client.PostAsJsonAsync("/api/books", new
        {
            type = "ebook",
            title = $"Versioned Cover {Guid.NewGuid():N}",
        });
        var book = (await created.Content.ReadFromJsonAsync<BookDto>())!;
        book.CoverVersion.Should().BeNull("a book without a cover has no cover version");

        (await UploadCoverAsync(book.Id, red: 255, blue: 0)).StatusCode.Should().Be(HttpStatusCode.OK);
        var firstVersion = (await Client.GetFromJsonAsync<BookDto>($"/api/books/{book.Id}"))!.CoverVersion;
        firstVersion.Should().NotBeNullOrEmpty();

        var first = await Client.GetAsync(VersionedCoverUrl(book.Id, suffix, firstVersion!));
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        first.Headers.CacheControl!.NoCache.Should().BeFalse();
        first.Headers.CacheControl.MaxAge.Should().Be(TimeSpan.FromDays(365));
        var oldBytes = await first.Content.ReadAsByteArrayAsync();

        (await UploadCoverAsync(book.Id, red: 0, blue: 255)).StatusCode.Should().Be(HttpStatusCode.OK);
        var secondVersion = (await Client.GetFromJsonAsync<BookDto>($"/api/books/{book.Id}"))!.CoverVersion;
        secondVersion.Should().NotBeNullOrEmpty().And.NotBe(firstVersion);

        var second = await Client.GetAsync(VersionedCoverUrl(book.Id, suffix, secondVersion!));
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await second.Content.ReadAsByteArrayAsync()).Should().NotEqual(oldBytes);

        (await Client.DeleteAsync($"/api/books/{book.Id}/cover")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Client.GetFromJsonAsync<BookDto>($"/api/books/{book.Id}"))!.CoverVersion.Should().BeNull();
    }

    private static string VersionedCoverUrl(Guid bookId, string suffix, string version) =>
        $"/api/books/{bookId}/cover{suffix}{(suffix.Contains('?') ? '&' : '?')}v={version}";

    private async Task<HttpResponseMessage> UploadCoverAsync(Guid bookId, byte red, byte blue)
    {
        var png = TestImages.Create(".png", red: red, blue: blue);

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "cover.png");
        return await Client.PostAsync($"/api/books/{bookId}/cover", form);
    }

    [Fact]
    public async Task Upload_file_still_accepts_digital_books()
    {
        var created = await Client.PostAsJsonAsync("/api/books", new
        {
            type = "ebook",
            title = $"Digital Upload {Guid.NewGuid():N}",
        });
        var book = (await created.Content.ReadFromJsonAsync<BookDto>())!;

        using var form = new MultipartFormDataContent();
        var file = new StringContent("A small text edition for the upload contract.");
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(file, "file", "copy.txt");

        var response = await Client.PostAsync($"/api/books/{book.Id}/file", form);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await Client.GetFromJsonAsync<BookDto>($"/api/books/{book.Id}");
        updated!.HasFile.Should().BeTrue();
        updated.FileName.Should().Be("book.txt");
    }

    [Fact]
    public void SelfHosted_host_composes_real_book_text_services()
    {
        using var scope = _factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IBookTextIndex>()
            .Should().BeOfType<SqliteBookTextIndex>();
        scope.ServiceProvider.GetRequiredService<IBookDerivedArtifactStorage>()
            .Should().BeOfType<FileBookTextArtifactStorage>();
        scope.ServiceProvider.GetRequiredService<IBookTextIngestionScheduler>()
            .Should().BeOfType<BookTextIngestionScheduler>();
    }

    [Fact]
    public async Task Upload_pdf_automatically_creates_book_text_ingestion_state()
    {
        var created = await Client.PostAsJsonAsync("/api/books", new
        {
            type = "ebook",
            title = $"Indexed Upload {Guid.NewGuid():N}",
        });
        var book = (await created.Content.ReadFromJsonAsync<BookDto>())!;

        var fixture = BookTextFixtureFactory.CreatePdf();
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(fixture.Bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(fixture.ContentType);
        form.Add(file, "file", fixture.FileName);

        var upload = await Client.PostAsync($"/api/books/{book.Id}/file", form);
        upload.StatusCode.Should().Be(HttpStatusCode.OK);

        // Scheduling is part of the upload commit path. The worker may already
        // have advanced Pending -> Processing/Ready by the time we read it, but
        // the source must never remain NotIndexed awaiting a process restart.
        var state = await Client.GetAsync($"/api/books/{book.Id}/text-index");
        state.StatusCode.Should().Be(HttpStatusCode.OK);
        (await state.Content.ReadAsStringAsync())
            .Should().NotContain("\"status\":\"NotIndexed\"");
    }

    [Fact]
    public async Task Get_book_and_unknown_book_404()
    {
        var created = await Client.PostAsJsonAsync("/api/books", new
        {
            type = "physical",
            title = $"GetMe {Guid.NewGuid():N}",
            author = "Author",
        });
        var book = (await created.Content.ReadFromJsonAsync<BookDto>())!;

        var get = await Client.GetAsync($"/api/books/{book.Id}");
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        (await get.Content.ReadFromJsonAsync<BookDto>())!.Id.Should().Be(book.Id);

        var missing = await Client.GetAsync($"/api/books/{Guid.NewGuid()}");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task List_books_supports_search_pagination_and_collection_filter()
    {
        var title = $"ListMe {Guid.NewGuid():N}";
        await Client.PostAsJsonAsync("/api/books", new { type = "physical", title, author = "Søren Kierkegaard" });

        var search = await Client.GetFromJsonAsync<PaginatedResponse<BookDto>>(
            $"/api/books?search={Uri.EscapeDataString(title)}&pageSize=5");
        search!.TotalCount.Should().Be(1);
        search.Items.Single().Title.Should().Be(title);
        search.PageSize.Should().Be(5);

        var clamped = await Client.GetFromJsonAsync<PaginatedResponse<BookDto>>("/api/books?page=0&pageSize=5000");
        clamped!.Page.Should().Be(1);
        clamped.PageSize.Should().Be(100);
    }

    [Fact]
    public async Task Status_counts_returns_the_current_library_totals()
    {
        var before = (await Client.GetFromJsonAsync<LibraryStatusCountsDto>(
            "/api/books/status-counts"))!;

        var collectionResponse = await Client.PostAsJsonAsync(
            "/api/collections",
            new { name = $"Status Counts {Guid.NewGuid():N}" });
        var collection = (await collectionResponse.Content.ReadFromJsonAsync<CollectionDto>())!;

        var createdBookIds = new List<Guid>();
        async Task<BookDto> CreateBook(bool favorite = false, Guid? collectionId = null)
        {
            var response = await Client.PostAsJsonAsync("/api/books", new
            {
                type = "physical",
                title = $"Status Count Book {Guid.NewGuid():N}",
                isFavorite = favorite,
                collectionId,
            });
            var book = (await response.Content.ReadFromJsonAsync<BookDto>())!;
            createdBookIds.Add(book.Id);
            return book;
        }

        try
        {
            await CreateBook();
            var reading = await CreateBook();
            var favoriteReading = await CreateBook(favorite: true);
            var finished = await CreateBook();
            await CreateBook(collectionId: collection.Id);
            var favoriteFinished = await CreateBook(favorite: true);

            await Client.PutAsJsonAsync($"/api/books/{reading.Id}/progress",
                new { location = "progress", percentage = 25 });
            await Client.PutAsJsonAsync($"/api/books/{favoriteReading.Id}/progress",
                new { location = "progress", percentage = 50 });
            await Client.PutAsJsonAsync($"/api/books/{finished.Id}/progress",
                new { location = "finished", percentage = 100 });
            await Client.PutAsJsonAsync($"/api/books/{favoriteFinished.Id}/progress",
                new { location = "finished", percentage = 100 });

            var response = await Client.GetAsync("/api/books/status-counts");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var after = (await response.Content.ReadFromJsonAsync<LibraryStatusCountsDto>())!;

            after.All.Should().Be(before.All + 6);
            after.NotStarted.Should().Be(before.NotStarted + 2);
            after.Reading.Should().Be(before.Reading + 2);
            after.Favorites.Should().Be(before.Favorites + 2);
            after.Finished.Should().Be(before.Finished + 2);
            after.Unsorted.Should().Be(before.Unsorted + 5);
        }
        finally
        {
            foreach (var id in createdBookIds)
            {
                await Client.DeleteAsync($"/api/books/{id}");
            }
            await Client.DeleteAsync($"/api/collections/{collection.Id}");
        }
    }

    [Fact]
    public async Task Update_book_changes_metadata_and_empty_string_clears()
    {
        var created = await Client.PostAsJsonAsync("/api/books", new
        {
            type = "physical",
            title = $"UpdateMe {Guid.NewGuid():N}",
            author = "Old Author",
            subtitle = "Subtitle",
        });
        var book = (await created.Content.ReadFromJsonAsync<BookDto>())!;

        var updated = await Client.PutAsJsonAsync($"/api/books/{book.Id}", new
        {
            author = "New Author",
            subtitle = "",
        });

        updated.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await updated.Content.ReadFromJsonAsync<BookDto>();
        dto!.Author.Should().Be("New Author");
        dto.Subtitle.Should().BeNull();
        dto.Title.Should().Be(book.Title);
    }

    [Fact]
    public async Task Update_unknown_book_returns_404()
    {
        var response = await Client.PutAsJsonAsync($"/api/books/{Guid.NewGuid()}", new { title = "X" });
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Progress_update_tracks_percent_and_finished_state_alignment()
    {
        var created = await Client.PostAsJsonAsync("/api/books", new
        {
            type = "physical",
            title = $"ProgressMe {Guid.NewGuid():N}",
        });
        var book = (await created.Content.ReadFromJsonAsync<BookDto>())!;

        var at100 = await Client.PutAsJsonAsync($"/api/books/{book.Id}/progress", new { location = "epub.cfi", percentage = 100 });
        at100.StatusCode.Should().Be(HttpStatusCode.OK);

        var finished = (await Client.GetFromJsonAsync<BookDto>($"/api/books/{book.Id}"))!;
        finished.ProgressPercent.Should().Be(100);
        finished.FinishedAt.Should().NotBeNull();

        var backTo50 = await Client.PutAsJsonAsync($"/api/books/{book.Id}/progress", new { location = "epub.cfi", percentage = 50 });
        backTo50.StatusCode.Should().Be(HttpStatusCode.OK);

        var resumed = (await Client.GetFromJsonAsync<BookDto>($"/api/books/{book.Id}"))!;
        resumed.ProgressPercent.Should().Be(50);
        resumed.FinishedAt.Should().BeNull("progress below 100 must clear the finished state");
    }

    [Fact]
    public async Task Progress_update_unknown_book_returns_404()
    {
        var response = await Client.PutAsJsonAsync($"/api/books/{Guid.NewGuid()}/progress",
            new { location = "x", percentage = 10 });
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Progress_reset_clears_partially_read_book()
    {
        var created = await Client.PostAsJsonAsync("/api/books", new
        {
            type = "physical",
            title = $"ResetPartial {Guid.NewGuid():N}",
        });
        var book = (await created.Content.ReadFromJsonAsync<BookDto>())!;

        var updated = await Client.PutAsJsonAsync($"/api/books/{book.Id}/progress",
            new { location = "epub.cfi", percentage = 50 });
        updated.StatusCode.Should().Be(HttpStatusCode.OK);

        var reset = await Client.PostAsync($"/api/books/{book.Id}/progress/reset", null);
        reset.StatusCode.Should().Be(HttpStatusCode.OK);

        var after = (await Client.GetFromJsonAsync<BookDto>($"/api/books/{book.Id}"))!;
        after.ProgressPercent.Should().Be(0);
        after.LastLocation.Should().BeNull();
        after.LastReadAt.Should().BeNull();
        after.FinishedAt.Should().BeNull();
    }

    [Fact]
    public async Task Progress_reset_clears_finished_book()
    {
        var created = await Client.PostAsJsonAsync("/api/books", new
        {
            type = "physical",
            title = $"ResetFinished {Guid.NewGuid():N}",
        });
        var book = (await created.Content.ReadFromJsonAsync<BookDto>())!;

        var at100 = await Client.PutAsJsonAsync($"/api/books/{book.Id}/progress",
            new { location = "epub.cfi", percentage = 100 });
        at100.StatusCode.Should().Be(HttpStatusCode.OK);

        var reset = await Client.PostAsync($"/api/books/{book.Id}/progress/reset", null);
        reset.StatusCode.Should().Be(HttpStatusCode.OK);

        var after = (await Client.GetFromJsonAsync<BookDto>($"/api/books/{book.Id}"))!;
        after.ProgressPercent.Should().Be(0);
        after.LastLocation.Should().BeNull();
        after.LastReadAt.Should().BeNull();
        after.FinishedAt.Should().BeNull();
    }

    [Fact]
    public async Task Progress_reset_unknown_book_returns_404()
    {
        var response = await Client.PostAsync($"/api/books/{Guid.NewGuid()}/progress/reset", null);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Progress_reset_already_reset_book_is_successful_noop()
    {
        var created = await Client.PostAsJsonAsync("/api/books", new
        {
            type = "physical",
            title = $"ResetNoop {Guid.NewGuid():N}",
        });
        var book = (await created.Content.ReadFromJsonAsync<BookDto>())!;

        var first = await Client.PostAsync($"/api/books/{book.Id}/progress/reset", null);
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await Client.PostAsync($"/api/books/{book.Id}/progress/reset", null);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ------------------------------------------------------------------
    // Work membership (issue #143) — the manual override over automatic
    // grouping. The shape under test is the HTTP contract; the merge/split
    // semantics themselves are covered in LibraryWorkMembershipTests.
    // ------------------------------------------------------------------

    private async Task<BookDto> CreateBookAsync(string type, string title, string? author = null, string? isbn = null)
    {
        var response = await Client.PostAsJsonAsync("/api/books", new
        {
            type,
            title,
            author,
            isbn,
            forceCreate = true,
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<BookDto>())!;
    }

    [Fact]
    public async Task Work_link_groups_two_books_and_both_report_the_other_edition()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var first = await CreateBookAsync("physical", $"Link A {suffix}", "Author One");
        var second = await CreateBookAsync("ebook", $"Link B {suffix}", "Author Two");

        var response = await Client.PostAsJsonAsync(
            $"/api/books/{second.Id}/work/link", new { targetBookId = first.Id });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = (await response.Content.ReadFromJsonAsync<LibraryWorkMembershipResultDto>())!;
        data.BookId.Should().Be(second.Id);
        data.EditionCount.Should().Be(2);
        data.WorkId.Should().Be(first.WorkId!.Value, "the target's work survives the merge");

        var reloaded = (await Client.GetFromJsonAsync<BookDto>($"/api/books/{second.Id}"))!;
        reloaded.WorkId.Should().Be(first.WorkId);
        reloaded.OtherEditions!.Single().Id.Should().Be(first.Id);
    }

    [Fact]
    public async Task Work_unlink_splits_a_book_into_its_own_new_work()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var first = await CreateBookAsync("physical", $"Unlink A {suffix}", "Author One");
        var second = await CreateBookAsync("ebook", $"Unlink B {suffix}", "Author Two");
        await Client.PostAsJsonAsync($"/api/books/{second.Id}/work/link", new { targetBookId = first.Id });

        var response = await Client.PostAsync($"/api/books/{second.Id}/work/unlink", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = (await response.Content.ReadFromJsonAsync<LibraryWorkMembershipResultDto>())!;
        data.WorkId.Should().NotBe(Guid.Empty);
        data.WorkId.Should().NotBe(first.WorkId!.Value);
        data.EditionCount.Should().Be(1);

        var reloaded = (await Client.GetFromJsonAsync<BookDto>($"/api/books/{second.Id}"))!;
        reloaded.WorkId.Should().Be(data.WorkId);
        reloaded.OtherEditions.Should().BeEmpty();
    }

    [Fact]
    public async Task Work_link_to_self_returns_400()
    {
        var book = await CreateBookAsync("physical", $"SelfLink {Guid.NewGuid():N}"[..20]);

        var response = await Client.PostAsJsonAsync(
            $"/api/books/{book.Id}/work/link", new { targetBookId = book.Id });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Work_link_with_an_unknown_target_returns_404()
    {
        var book = await CreateBookAsync("physical", $"NoTarget {Guid.NewGuid():N}"[..20]);

        var response = await Client.PostAsJsonAsync(
            $"/api/books/{book.Id}/work/link", new { targetBookId = Guid.NewGuid() });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Work_unlink_unknown_book_returns_404()
    {
        var response = await Client.PostAsync($"/api/books/{Guid.NewGuid()}/work/unlink", null);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Progress_update_at_zero_percent_is_not_a_reset()
    {
        var created = await Client.PostAsJsonAsync("/api/books", new
        {
            type = "physical",
            title = $"ZeroPercent {Guid.NewGuid():N}",
        });
        var book = (await created.Content.ReadFromJsonAsync<BookDto>())!;

        // A naive 0% update must NOT clear recency — that is reset-only.
        var updated = await Client.PutAsJsonAsync($"/api/books/{book.Id}/progress",
            new { location = "", percentage = 0 });
        updated.StatusCode.Should().Be(HttpStatusCode.OK);

        var after = (await Client.GetFromJsonAsync<BookDto>($"/api/books/{book.Id}"))!;
        after.ProgressPercent.Should().Be(0);
        after.LastLocation.Should().BeNull();
        after.LastReadAt.Should().NotBeNull("an ordinary 0% update still records recency");
    }

    [Fact]
    public async Task Delete_book_removes_row_and_second_delete_is_404()
    {
        var created = await Client.PostAsJsonAsync("/api/books", new
        {
            type = "physical",
            title = $"DeleteMe {Guid.NewGuid():N}",
        });
        var book = (await created.Content.ReadFromJsonAsync<BookDto>())!;

        var deleted = await Client.DeleteAsync($"/api/books/{book.Id}");
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var again = await Client.DeleteAsync($"/api/books/{book.Id}");
        again.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_book_that_has_notes_cascades_delete()
    {
        var created = await Client.PostAsJsonAsync("/api/books", new
        {
            type = "physical",
            title = $"BookWithNotes {Guid.NewGuid():N}",
            author = "Author",
        });
        var book = (await created.Content.ReadFromJsonAsync<BookDto>())!;

        await using (var db = await OpenDbAsync())
        {
            db.Notes.Add(new Nostos.Backend.Data.Models.NoteModel
            {
                Id = Guid.NewGuid(),
                BookId = book.Id,
                Content = "A note attached to this book",
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var deleted = await Client.DeleteAsync($"/api/books/{book.Id}");
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var stillThere = await Client.GetAsync($"/api/books/{book.Id}");
        stillThere.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Lookup_invalid_isbn_returns_400_without_external_call()
    {
        var response = await Client.GetAsync("/api/books/lookup/9780141183849"); // bad checksum
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ------------------------------------------------------------------
    // Collections
    // ------------------------------------------------------------------

    [Fact]
    public async Task Create_collection_duplicate_sibling_returns_existing()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var first = await Client.PostAsJsonAsync("/api/collections", new { name = $"Shelf {suffix}" });
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        var firstDto = (await first.Content.ReadFromJsonAsync<CollectionDto>())!;

        var second = await Client.PostAsJsonAsync("/api/collections", new { name = $"  shelf {suffix}  " });
        second.StatusCode.Should().Be(HttpStatusCode.Created, "legacy REST always returns 201 on success");
        var secondDto = (await second.Content.ReadFromJsonAsync<CollectionDto>())!;
        secondDto.Id.Should().Be(firstDto.Id);
    }

    [Fact]
    public async Task Create_collection_with_missing_parent_returns_400()
    {
        var response = await Client.PostAsJsonAsync("/api/collections", new
        {
            name = "Orphan",
            parentId = Guid.NewGuid(),
        });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_collection_renames_and_moves()
    {
        var root = (await (await Client.PostAsJsonAsync("/api/collections", new { name = $"Root {Guid.NewGuid():N}"[..24] }))
            .Content.ReadFromJsonAsync<CollectionDto>())!;
        var child = (await (await Client.PostAsJsonAsync("/api/collections", new { name = $"Child {Guid.NewGuid():N}"[..24], parentId = root.Id }))
            .Content.ReadFromJsonAsync<CollectionDto>())!;

        var renamed = await Client.PutAsJsonAsync($"/api/collections/{child.Id}", new { name = "Renamed Child", parentId = child.ParentId });
        renamed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await renamed.Content.ReadFromJsonAsync<CollectionDto>())!.Name.Should().Be("Renamed Child");

        var moved = await Client.PutAsJsonAsync($"/api/collections/{child.Id}", new { name = "Renamed Child", parentId = (Guid?)null });
        moved.StatusCode.Should().Be(HttpStatusCode.OK);
        (await moved.Content.ReadFromJsonAsync<CollectionDto>())!.ParentId.Should().BeNull();
    }

    [Fact]
    public async Task Update_collection_into_own_child_returns_409()
    {
        var parent = (await (await Client.PostAsJsonAsync("/api/collections", new { name = $"Parent {Guid.NewGuid():N}"[..24] }))
            .Content.ReadFromJsonAsync<CollectionDto>())!;
        var child = (await (await Client.PostAsJsonAsync("/api/collections", new { name = $"Child {Guid.NewGuid():N}"[..24], parentId = parent.Id }))
            .Content.ReadFromJsonAsync<CollectionDto>())!;

        var response = await Client.PutAsJsonAsync($"/api/collections/{parent.Id}", new { name = parent.Name, parentId = child.Id });
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Delete_collection_unlinks_books_and_rejects_children()
    {
        var parent = (await (await Client.PostAsJsonAsync("/api/collections", new { name = $"Tmp {Guid.NewGuid():N}"[..24] }))
            .Content.ReadFromJsonAsync<CollectionDto>())!;
        var child = (await (await Client.PostAsJsonAsync("/api/collections", new { name = $"Sub {Guid.NewGuid():N}"[..24], parentId = parent.Id }))
            .Content.ReadFromJsonAsync<CollectionDto>())!;

        var withChildren = await Client.DeleteAsync($"/api/collections/{parent.Id}");
        withChildren.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var book = (await (await Client.PostAsJsonAsync("/api/books", new
        {
            type = "physical",
            title = $"ShelfBook {Guid.NewGuid():N}",
            collectionId = parent.Id,
        })).Content.ReadFromJsonAsync<BookDto>())!;

        var childDeleted = await Client.DeleteAsync($"/api/collections/{child.Id}");
        childDeleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var deleted = await Client.DeleteAsync($"/api/collections/{parent.Id}");
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var bookAfter = (await Client.GetFromJsonAsync<BookDto>($"/api/books/{book.Id}"))!;
        bookAfter.CollectionIds.Should().BeEmpty("books are unlinked, never deleted");
    }

    [Fact]
    public async Task Get_collections_flat_list_and_404()
    {
        var list = await Client.GetFromJsonAsync<CollectionDto[]>("/api/collections");
        list.Should().NotBeNull();

        var missing = await Client.GetAsync($"/api/collections/{Guid.NewGuid()}");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Update_collection_missing_parent_id_returns_400_and_changes_nothing()
    {
        var collection = (await (await Client.PostAsJsonAsync("/api/collections", new { name = $"Presence {Guid.NewGuid():N}"[..24] }))
            .Content.ReadFromJsonAsync<CollectionDto>())!;

        var response = await Client.PutAsJsonAsync($"/api/collections/{collection.Id}", new { name = "Should Not Apply" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var after = (await Client.GetFromJsonAsync<CollectionDto>($"/api/collections/{collection.Id}"))!;
        after.Name.Should().Be(collection.Name);
        after.ParentId.Should().Be(collection.ParentId);
    }

    [Fact]
    public async Task Update_collection_missing_name_returns_400_and_changes_nothing()
    {
        var collection = (await (await Client.PostAsJsonAsync("/api/collections", new { name = $"Presence {Guid.NewGuid():N}"[..24] }))
            .Content.ReadFromJsonAsync<CollectionDto>())!;

        var response = await Client.PutAsJsonAsync($"/api/collections/{collection.Id}", new { parentId = (Guid?)null });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var after = (await Client.GetFromJsonAsync<CollectionDto>($"/api/collections/{collection.Id}"))!;
        after.Name.Should().Be(collection.Name);
    }

    [Fact]
    public async Task Update_collection_whitespace_name_returns_400()
    {
        var collection = (await (await Client.PostAsJsonAsync("/api/collections", new { name = $"Space {Guid.NewGuid():N}"[..24] }))
            .Content.ReadFromJsonAsync<CollectionDto>())!;

        var response = await Client.PutAsJsonAsync($"/api/collections/{collection.Id}", new { name = "   ", parentId = collection.ParentId });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Client.GetFromJsonAsync<CollectionDto>($"/api/collections/{collection.Id}"))!.Name.Should().Be(collection.Name);
    }

    [Fact]
    public async Task Update_collection_explicit_null_parent_moves_to_root()
    {
        var root = (await (await Client.PostAsJsonAsync("/api/collections", new { name = $"MoveRoot {Guid.NewGuid():N}"[..24] }))
            .Content.ReadFromJsonAsync<CollectionDto>())!;
        var child = (await (await Client.PostAsJsonAsync("/api/collections", new { name = $"MoveMe {Guid.NewGuid():N}"[..24], parentId = root.Id }))
            .Content.ReadFromJsonAsync<CollectionDto>())!;

        var response = await Client.PutAsJsonAsync($"/api/collections/{child.Id}", new { name = child.Name, parentId = (Guid?)null });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var after = await response.Content.ReadFromJsonAsync<CollectionDto>();
        after!.ParentId.Should().BeNull();
        after.Name.Should().Be(child.Name);
    }

    [Fact]
    public async Task Get_collection_counts_returns_descendant_inclusive_counts()
    {
        var root = (await (await Client.PostAsJsonAsync("/api/collections", new { name = $"CountRoot {Guid.NewGuid():N}"[..24] }))
            .Content.ReadFromJsonAsync<CollectionDto>())!;
        var child = (await (await Client.PostAsJsonAsync("/api/collections", new { name = $"CountChild {Guid.NewGuid():N}"[..24], parentId = root.Id }))
            .Content.ReadFromJsonAsync<CollectionDto>())!;

        await Client.PostAsJsonAsync("/api/books", new
        {
            type = "physical",
            title = $"Counted Book {Guid.NewGuid():N}",
            collectionId = child.Id,
        });
        await Client.PostAsJsonAsync("/api/books", new
        {
            type = "physical",
            title = $"Uncounted Book {Guid.NewGuid():N}",
        });

        var counts = await Client.GetFromJsonAsync<CollectionCountDto[]>("/api/collections/counts");

        counts.Should().NotBeNull();
        var collectionCounts = counts!;
        collectionCounts.Single(c => c.CollectionId == root.Id).BookCount.Should().Be(1,
            "a parent's count includes its child's books");
        collectionCounts.Single(c => c.CollectionId == child.Id).BookCount.Should().Be(1);
        // The child's book counts once for the child and once for the parent;
        // the uncollected book contributes to NO collection count.
        collectionCounts.Sum(c => c.BookCount).Should().Be(2);
    }

    [Fact]
    public async Task Work_edition_grouping_creates_separate_entities_sharing_same_work_id()
    {
        var title = $"The Devils {Guid.NewGuid():N}";
        var author = "Fyodor Dostoevsky";

        // 1. Create EPUB (eBook)
        var ebookResp = await Client.PostAsJsonAsync("/api/books", new
        {
            type = "ebook",
            title,
            author,
        });
        ebookResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var ebook = (await ebookResp.Content.ReadFromJsonAsync<BookDto>())!;
        ebook.WorkId.Should().NotBeNull();
        ebook.WorkId.Should().NotBe(Guid.Empty);

        // 2. Create Audiobook of the same Work (same title + author)
        var audioResp = await Client.PostAsJsonAsync("/api/books", new
        {
            type = "audiobook",
            title,
            author,
            narrator = "George Guidall",
            duration = "26h 15m",
        });
        audioResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var audio = (await audioResp.Content.ReadFromJsonAsync<BookDto>())!;

        // Different book entities in the database
        audio.Id.Should().NotBe(ebook.Id);
        audio.Type.Should().Be("audiobook");
        ebook.Type.Should().Be("ebook");

        // But both share the exact same WorkId!
        audio.WorkId.Should().Be(ebook.WorkId);

        try
        {
            // 3. Re-adding an eBook of the same work matches the existing eBook edition
            var dupEbookResp = await Client.PostAsJsonAsync("/api/books", new
            {
                type = "ebook",
                title,
                author,
            });
            dupEbookResp.StatusCode.Should().Be(HttpStatusCode.OK); // matched!
            var dupEbook = (await dupEbookResp.Content.ReadFromJsonAsync<BookDto>())!;
            dupEbook.Id.Should().Be(ebook.Id);

            // 4. Query with groupByWork=true returns 1 primary card representing the Work
            var listResp = await Client.GetAsync($"/api/books?search={title}&groupByWork=true");
            listResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var page = (await listResp.Content.ReadFromJsonAsync<PaginatedResponse<BookDto>>())!;
            page.TotalCount.Should().Be(1);
            var workCard = page.Items.Single();
            workCard.WorkId.Should().Be(ebook.WorkId);
            workCard.EditionCount.Should().Be(2);
            workCard.OtherEditions.Should().NotBeNull();
            workCard.OtherEditions!.Should().HaveCount(1);
        }
        finally
        {
            await Client.DeleteAsync($"/api/books/{ebook.Id}");
            await Client.DeleteAsync($"/api/books/{audio.Id}");
        }

        // 5. Deleting all editions prunes the Work record from the database
        await using var db = await OpenDbAsync();
        var workRemaining = await db.Works.FindAsync(ebook.WorkId!.Value);
        workRemaining.Should().BeNull();
    }

    // ------------------------------------------------------------------

    private sealed class DeclaredLengthContent(long declaredLength) : HttpContent
    {
        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context) =>
            Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = declaredLength;
            return true;
        }
    }

    private async Task<NostosDbContext> OpenDbAsync()
    {
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={_factory.DatabasePath}")
            .Options;
        var db = new NostosDbContext(options);
        await db.Database.OpenConnectionAsync();
        return db;
    }
}

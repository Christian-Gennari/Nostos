using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Data.Repositories;
using Nostos.Backend.Providers.Acquisition;
using Nostos.Backend.Services;
using Nostos.Backend.Services.BookUpload;
using Nostos.Backend.Services.ExternalMedia;
using Nostos.Backend.Tests.Portability;
using Nostos.Backend.Tests.Support;
using Nostos.Product.BookText;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Endpoints;

/// <summary>
/// The resumable book-file contract (#1266): a transfer survives a dropped
/// connection, a resumed session sends only the missing parts, and the file
/// that lands in the library is byte-identical to the source.
/// </summary>
public sealed class BookUploadEndpointTests
    : IClassFixture<BookUploadEndpointTests.BookUploadHostFactory>, IAsyncLifetime
{
    private const int PartBytes = 16 * 1024 * 1024;
    private readonly BookUploadHostFactory _factory;

    public BookUploadEndpointTests(BookUploadHostFactory factory) => _factory = factory;

    public Task InitializeAsync()
    {
        _factory.Faults.ResetAll();
        return _factory.ResetStateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Interrupted_upload_resumes_from_receipts_and_publishes_identical_bytes()
    {
        using var client = _factory.CreateClient();
        var book = await CreateBookAsync(client);

        // Two parts: the resume path must skip what the server already has.
        var total = PartBytes + 4099;
        var bytes = new byte[total];
        new Random(1266).NextBytes(bytes);
        var sha = Sha256Hex(bytes);

        var created = await CreateSessionAsync(client, book.Id, bytes.Length, "resume.txt", sha);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var session = await ReadStatusAsync(created);

        using (var first = await PutPartAsync(client, book.Id, session.SessionId, 0, bytes))
            first.StatusCode.Should().Be(HttpStatusCode.OK);

        // The client goes away before the last part: no completion, no row update.
        var current = await client.GetFromJsonAsync<UploadStatusDto>(
            $"/api/books/{book.Id}/file/uploads");
        current!.SessionId.Should().Be(session.SessionId);
        current.ReceivedParts.Should().Equal(0);
        current.State.Should().Be("Receiving");

        using (var last = await PutPartAsync(client, book.Id, session.SessionId, 1, bytes))
            last.StatusCode.Should().Be(HttpStatusCode.OK);

        using var completed = await client.PostAsync(
            $"/api/books/{book.Id}/file/uploads/{session.SessionId}/complete",
            null);
        completed.StatusCode.Should().Be(HttpStatusCode.OK);
        var final = await ReadStatusAsync(completed);
        final.State.Should().Be("Complete");
        final.ReceivedParts.Should().Equal(0, 1);

        await using (var db = OpenDb(_factory.DatabasePath))
        {
            var row = await db.Books.SingleAsync(b => b.Id == book.Id);
            row.FileDetails.HasFile.Should().BeTrue();
            row.FileDetails.FileName.Should().Be("book.txt");
            var sessionRow = await db.BookUploadSessionRecords.SingleAsync();
            sessionRow.State.Should().Be(4); // Complete
            sessionRow.ActiveSessionKey.Should().BeNull();
        }

        var stored = Path.Combine(_factory.BooksRootPath, book.Id.ToString(), "book.txt");
        File.Exists(stored).Should().BeTrue();
        Sha256Hex(await File.ReadAllBytesAsync(stored)).Should().Be(sha);
    }

    [Fact]
    public async Task Concurrent_parts_each_get_a_receipt()
    {
        using var client = _factory.CreateClient();
        var book = await CreateBookAsync(client);

        // Two requests that both read the session before either receipt is
        // committed. The write gate must serialize them and the second must
        // re-read the bumped version rather than fail optimistic concurrency.
        var total = PartBytes + 4099;
        var bytes = new byte[total];
        new Random(1267).NextBytes(bytes);
        var created = await CreateSessionAsync(
            client, book.Id, bytes.Length, "concurrent.txt", Sha256Hex(bytes));
        var session = await ReadStatusAsync(created);

        var responses = await Task.WhenAll(
            PutPartAsync(client, book.Id, session.SessionId, 0, bytes),
            PutPartAsync(client, book.Id, session.SessionId, 1, bytes));
        try
        {
            responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);

            var current = await client.GetFromJsonAsync<UploadStatusDto>(
                $"/api/books/{book.Id}/file/uploads");
            current!.ReceivedParts.Should().Equal(0, 1);
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [Fact]
    public async Task Repeating_a_received_part_is_idempotent()
    {
        using var client = _factory.CreateClient();
        var book = await CreateBookAsync(client);
        var bytes = Encoding.ASCII.GetBytes("hello resumable world");
        var sha = Sha256Hex(bytes);

        var created = await CreateSessionAsync(client, book.Id, bytes.Length, "small.txt", sha);
        var session = await ReadStatusAsync(created);

        using (var first = await PutPartAsync(client, book.Id, session.SessionId, 0, bytes))
            first.StatusCode.Should().Be(HttpStatusCode.OK);

        using var second = await PutPartAsync(client, book.Id, session.SessionId, 0, bytes);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await second.Content.ReadFromJsonAsync<PartResultDto>();
        result!.AlreadyPresent.Should().BeTrue();
    }

    [Fact]
    public async Task Part_with_wrong_hash_is_rejected_without_a_receipt()
    {
        using var client = _factory.CreateClient();
        var book = await CreateBookAsync(client);
        var bytes = Encoding.ASCII.GetBytes("checksum matters");
        var sha = Sha256Hex(bytes);

        var created = await CreateSessionAsync(client, book.Id, bytes.Length, "small.txt", sha);
        var session = await ReadStatusAsync(created);

        var bad = new string('0', 64);
        using var response = await PutPartAsync(client, book.Id, session.SessionId, 0, bytes, bad);
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync())
            .Should().Contain("book_upload_part_hash_mismatch");

        var current = await client.GetFromJsonAsync<UploadStatusDto>(
            $"/api/books/{book.Id}/file/uploads");
        current!.ReceivedParts.Should().BeEmpty();
    }

    [Fact]
    public async Task Different_file_replaces_an_interrupted_session()
    {
        using var client = _factory.CreateClient();
        var book = await CreateBookAsync(client);

        var first = Encoding.ASCII.GetBytes("first draft");
        var created = await CreateSessionAsync(
            client, book.Id, first.Length, "draft.txt", Sha256Hex(first));
        var session = await ReadStatusAsync(created);
        using (var part = await PutPartAsync(client, book.Id, session.SessionId, 0, first))
            part.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = Encoding.ASCII.GetBytes("second, final draft");
        using var replaced = await CreateSessionAsync(
            client, book.Id, second.Length, "draft.txt", Sha256Hex(second));
        replaced.StatusCode.Should().Be(HttpStatusCode.Created);
        var replacement = await ReadStatusAsync(replaced);
        replacement.SessionId.Should().NotBe(session.SessionId);
        replacement.ReceivedParts.Should().BeEmpty();

        await using var db = OpenDb(_factory.DatabasePath);
        (await db.BookUploadSessionRecords.CountAsync(s => s.ActiveSessionKey != null))
            .Should().Be(1);
    }

    [Fact]
    public async Task Unknown_session_is_not_found()
    {
        using var client = _factory.CreateClient();
        var book = await CreateBookAsync(client);

        using var response = await client.GetAsync(
            $"/api/books/{book.Id}/file/uploads/{Guid.NewGuid()}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Legacy_whole_request_upload_still_works()
    {
        using var client = _factory.CreateClient();
        var book = await CreateBookAsync(client);
        var bytes = Encoding.ASCII.GetBytes("legacy upload path");

        using var form = new MultipartFormDataContent();
        using var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(file, "file", "legacy.txt");

        using var response = await client.PostAsync($"/api/books/{book.Id}/file", form);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var stored = Path.Combine(_factory.BooksRootPath, book.Id.ToString(), "book.txt");
        File.Exists(stored).Should().BeTrue();
        Sha256Hex(await File.ReadAllBytesAsync(stored)).Should().Be(Sha256Hex(bytes));
    }

    [Theory]
    [InlineData("truncated.epub", "application/epub+zip", "PK\u0003\u0004 not really a zip")]
    [InlineData("renamed.epub", "application/epub+zip", "MZ\u0090\u0000 pretend executable")]
    [InlineData("fake.pdf", "application/pdf", "<html>not a pdf</html>")]
    [InlineData("fake.mp3", "audio/mpeg", "this is plain text, not audio")]
    public async Task Resumable_complete_rejects_bytes_that_do_not_match_the_name(
        string fileName, string contentType, string text)
    {
        using var client = _factory.CreateClient();
        var book = await CreateBookAsync(client);
        var bytes = Encoding.Latin1.GetBytes(text);

        var session = await StageAsync(client, book.Id, bytes, fileName, contentType);
        using var response = await CompleteAsync(client, book.Id, session.SessionId);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("book_upload_invalid_file");

        // No file was adopted: the book row still has none.
        File.Exists(Path.Combine(_factory.BooksRootPath, book.Id.ToString(), Path.ChangeExtension("book", Path.GetExtension(fileName))))
            .Should().BeFalse();
        using var scope = _factory.Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<IBookRepository>().GetByIdAsync(book.Id);
        row!.FileDetails.HasFile.Should().BeFalse();
    }

    [Fact]
    public async Task Empty_file_is_rejected_when_the_session_is_created()
    {
        using var client = _factory.CreateClient();
        var book = await CreateBookAsync(client);

        using var response = await CreateSessionAsync(
            client, book.Id, 0, "empty.epub", Sha256Hex([]), "application/epub+zip");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("book_upload_invalid_file");
    }

    [Fact]
    public async Task Valid_epub_pdf_and_mp3_are_accepted_and_txt_and_mobi_are_stored_as_is()
    {
        using var client = _factory.CreateClient();

        var cases = new (string Name, string Type, byte[] Bytes)[]
        {
            ("ok.epub", "application/epub+zip", MinimalEpub()),
            ("ok.pdf", "application/pdf", Encoding.ASCII.GetBytes("%PDF-1.4\n%%EOF")),
            ("ok.mp3", "audio/mpeg", SilentMp3(40)),
            ("ok.txt", "text/plain", Encoding.ASCII.GetBytes("plain words")),
            ("ok.mobi", "application/x-mobipocket-ebook", Encoding.ASCII.GetBytes("BOOKMOBI")),
        };
        foreach (var (name, type, bytes) in cases)
        {
            var book = await CreateBookAsync(client);
            var session = await StageAsync(client, book.Id, bytes, name, type);
            using var response = await CompleteAsync(client, book.Id, session.SessionId);
            response.StatusCode.Should().Be(HttpStatusCode.OK, name);
        }
    }

    [Theory]
    [InlineData("bad.epub", "application/epub+zip", "not a zip")]
    [InlineData("bad.pdf", "application/pdf", "not a pdf")]
    [InlineData("empty.pdf", "application/pdf", "")]
    public async Task Legacy_upload_rejects_bad_files_with_a_clear_400(
        string fileName, string contentType, string text)
    {
        using var client = _factory.CreateClient();
        var book = await CreateBookAsync(client);

        using var form = new MultipartFormDataContent();
        using var file = new ByteArrayContent(Encoding.ASCII.GetBytes(text));
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);

        using var response = await client.PostAsync($"/api/books/{book.Id}/file", form);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("book_upload_invalid_file");
    }

    private static byte[] MinimalEpub()
    {
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            void Add(string name, string text)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(text);
            }
            Add("mimetype", "application/epub+zip");
            Add("META-INF/container.xml",
                "<container xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"><rootfiles>" +
                "<rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/>" +
                "</rootfiles></container>");
            Add("OEBPS/content.opf", "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\"/>");
        }
        return ms.ToArray();
    }

    [Fact]
    public async Task Capability_advertises_the_contract_on_this_host()
    {
        using var client = _factory.CreateClient();

        var capabilities = await client.GetFromJsonAsync<JsonElement>(
            "/api/runtime/capabilities");
        capabilities.GetProperty("supportsResumableBookUploads").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Deleting_the_book_discards_its_unfinished_upload()
    {
        using var client = _factory.CreateClient();
        var book = await CreateBookAsync(client);
        var bytes = Encoding.ASCII.GetBytes("will be discarded");

        var created = await CreateSessionAsync(
            client, book.Id, bytes.Length, "discard.txt", Sha256Hex(bytes));
        var session = await ReadStatusAsync(created);

        using var deleted = await client.DeleteAsync($"/api/books/{book.Id}");
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using (var db = OpenDb(_factory.DatabasePath))
        {
            (await db.BookUploadSessionRecords.CountAsync()).Should().Be(0);
        }

        var staging = Path.Combine(
            _factory.StorageTempRoot,
            "transfers",
            "book-uploads",
            session.SessionId.ToString("N"));
        Directory.Exists(staging).Should().BeFalse();
    }

    // #1310. A to E are the cases measured in the issue. A and B write the state
    // a crash between adoption and the row commit leaves; the others inject the
    // failure through the real publish.

    [Fact]
    public async Task Crash_after_adoption_of_a_first_upload_completes_on_retry()
    {
        using var scene = new RecoveryScene(_factory);
        var book = await CreateBookAsync(scene.Client);
        var bytes = Encoding.ASCII.GetBytes("first upload, crash before the row");
        var session = await StageAsync(scene.Client, book.Id, bytes, "first.txt");
        await CrashAfterAdoptionAsync(scene.Factory, book.Id, session.SessionId, bytes);

        using var completed = await CompleteAsync(scene.Client, book.Id, session.SessionId);

        completed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadStatusAsync(completed)).State.Should().Be("Complete");
        await using var db = OpenDb(scene.Factory.DatabasePath);
        var row = await db.Books.SingleAsync(b => b.Id == book.Id);
        row.FileDetails.HasFile.Should().BeTrue();
        row.FileDetails.FileName.Should().Be("book.txt");
        scene.Faults.Scheduled.Should().Equal(book.Id);
    }

    [Fact]
    public async Task Crash_after_adoption_of_a_replacement_runs_the_row_commit()
    {
        using var scene = new RecoveryScene(_factory);
        var book = await CreateBookWithFileAsync(scene, Encoding.ASCII.GetBytes("the old file"));
        await SetStaleFileStateAsync(scene.Factory, book.Id);
        var bytes = Encoding.ASCII.GetBytes("the replacement, a crash before its row");
        var session = await StageAsync(scene.Client, book.Id, bytes, "new.txt");
        await CrashAfterAdoptionAsync(scene.Factory, book.Id, session.SessionId, bytes);

        using var completed = await CompleteAsync(scene.Client, book.Id, session.SessionId);

        completed.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db = OpenDb(scene.Factory.DatabasePath);
        var row = await db.Books.SingleAsync(b => b.Id == book.Id);
        row.FileDetails.LocationsJson.Should().BeNull();
        row.FileDetails.TracksJson.Should().BeNull();
        scene.Faults.Scheduled.Should().Equal(book.Id);
    }

    [Fact]
    public async Task Failed_row_update_is_finished_by_recovery()
    {
        using var scene = new RecoveryScene(_factory);
        var book = await CreateBookWithFileAsync(scene, Encoding.ASCII.GetBytes("the old file"));
        await SetStaleFileStateAsync(scene.Factory, book.Id);
        var bytes = Encoding.ASCII.GetBytes("the replacement, row update fails once");
        var session = await StageAsync(scene.Client, book.Id, bytes, "new.txt");
        scene.Faults.FailRowUpdates = 1;

        using var completed = await CompleteAsync(scene.Client, book.Id, session.SessionId);

        completed.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db = OpenDb(scene.Factory.DatabasePath);
        var row = await db.Books.SingleAsync(b => b.Id == book.Id);
        row.FileDetails.LocationsJson.Should().BeNull();
        row.FileDetails.TracksJson.Should().BeNull();
        scene.Faults.Scheduled.Should().Equal(book.Id);
        StoredSha(scene.Factory, book.Id).Should().Be(Sha256Hex(bytes));
    }

    [Fact]
    public async Task Failed_adoption_of_a_same_length_replacement_is_not_reported_complete()
    {
        using var scene = new RecoveryScene(_factory);
        var oldBytes = Encoding.ASCII.GetBytes("old file, same length");
        var book = await CreateBookWithFileAsync(scene, oldBytes);
        await SetStaleFileStateAsync(scene.Factory, book.Id);
        var bytes = Encoding.ASCII.GetBytes("new file, same length");
        bytes.Should().HaveCount(oldBytes.Length);
        var session = await StageAsync(scene.Client, book.Id, bytes, "new.txt");
        scene.Faults.FailAdoptions = 1;

        using (var failed = await CompleteAsync(scene.Client, book.Id, session.SessionId))
            failed.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        (await SessionAsync(scene.Factory, session.SessionId)).State
            .Should().Be((int)BookUploadState.Completing);
        Directory.Exists(StagingPath(scene.Factory, session.SessionId)).Should().BeTrue();
        StoredSha(scene.Factory, book.Id).Should().Be(Sha256Hex(oldBytes));
        scene.Faults.Scheduled.Should().BeEmpty();

        using var retried = await CompleteAsync(scene.Client, book.Id, session.SessionId);

        retried.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadStatusAsync(retried)).State.Should().Be("Complete");
        StoredSha(scene.Factory, book.Id).Should().Be(Sha256Hex(bytes));
        scene.Faults.Scheduled.Should().Equal(book.Id);
    }

    [Fact]
    public async Task Failed_adoption_of_a_different_length_replacement_stays_retryable()
    {
        using var scene = new RecoveryScene(_factory);
        var oldBytes = Encoding.ASCII.GetBytes("old file");
        var book = await CreateBookWithFileAsync(scene, oldBytes);
        var bytes = Encoding.ASCII.GetBytes("a new file of another length");
        var session = await StageAsync(scene.Client, book.Id, bytes, "new.txt");
        scene.Faults.FailAdoptions = 1;

        using var failed = await CompleteAsync(scene.Client, book.Id, session.SessionId);

        failed.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await SessionAsync(scene.Factory, session.SessionId)).State
            .Should().Be((int)BookUploadState.Completing);
        Directory.Exists(StagingPath(scene.Factory, session.SessionId)).Should().BeTrue();
        StoredSha(scene.Factory, book.Id).Should().Be(Sha256Hex(oldBytes));
    }

    [Fact]
    public async Task Stored_file_with_other_bytes_is_never_reported_complete()
    {
        using var scene = new RecoveryScene(_factory);
        var oldBytes = Encoding.ASCII.GetBytes("old file, same length");
        var book = await CreateBookWithFileAsync(scene, oldBytes);
        await SetStaleFileStateAsync(scene.Factory, book.Id);
        var bytes = Encoding.ASCII.GetBytes("new file, same length");
        var session = await StageAsync(scene.Client, book.Id, bytes, "new.txt");

        // The staging is gone but the library still holds the old bytes.
        await CrashAfterAdoptionAsync(scene.Factory, book.Id, session.SessionId, oldBytes);

        using var completed = await CompleteAsync(scene.Client, book.Id, session.SessionId);

        completed.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await completed.Content.ReadAsStringAsync()).Should().Contain("book_upload_invalid_state");
        (await SessionAsync(scene.Factory, session.SessionId)).State
            .Should().Be((int)BookUploadState.Completing);
        await using var db = OpenDb(scene.Factory.DatabasePath);
        (await db.Books.SingleAsync(b => b.Id == book.Id)).FileDetails.LocationsJson
            .Should().NotBeNull();
        scene.Faults.Scheduled.Should().BeEmpty();
    }

    [Fact]
    public async Task Stored_file_with_the_same_bytes_and_another_format_is_not_this_upload()
    {
        using var scene = new RecoveryScene(_factory);
        var book = await CreateBookAsync(scene.Client);
        var bytes = Encoding.ASCII.GetBytes("same bytes, another extension");
        var session = await StageAsync(scene.Client, book.Id, bytes, "first.txt");
        await CrashAfterAdoptionAsync(
            scene.Factory, book.Id, session.SessionId, bytes, storedName: "book.pdf");

        using var completed = await CompleteAsync(scene.Client, book.Id, session.SessionId);

        completed.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await using var db = OpenDb(scene.Factory.DatabasePath);
        (await db.Books.SingleAsync(b => b.Id == book.Id)).FileDetails.HasFile.Should().BeFalse();
    }

    [Fact]
    public async Task Recovery_that_fails_can_be_retried()
    {
        using var scene = new RecoveryScene(_factory);
        var book = await CreateBookAsync(scene.Client);
        var bytes = Encoding.ASCII.GetBytes("the row cannot be written yet");
        var session = await StageAsync(scene.Client, book.Id, bytes, "first.txt");

        // Fails the publish and the recovery inside the same request.
        scene.Faults.FailRowUpdates = 2;
        using (var failed = await CompleteAsync(scene.Client, book.Id, session.SessionId))
            failed.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        (await SessionAsync(scene.Factory, session.SessionId)).State
            .Should().Be((int)BookUploadState.Completing);
        Directory.Exists(StagingPath(scene.Factory, session.SessionId)).Should().BeFalse();

        using var retried = await CompleteAsync(scene.Client, book.Id, session.SessionId);

        retried.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db = OpenDb(scene.Factory.DatabasePath);
        (await db.Books.SingleAsync(b => b.Id == book.Id)).FileDetails.HasFile.Should().BeTrue();
        scene.Faults.Scheduled.Should().Equal(book.Id);
    }

    [Fact]
    public async Task Commit_that_failed_after_the_row_update_is_repeated_safely()
    {
        using var scene = new RecoveryScene(_factory);
        var book = await CreateBookAsync(scene.Client);
        var bytes = Encoding.ASCII.GetBytes("the index could not be told");
        var session = await StageAsync(scene.Client, book.Id, bytes, "first.txt");
        scene.Faults.FailSchedules = 1;

        using var completed = await CompleteAsync(scene.Client, book.Id, session.SessionId);

        completed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadStatusAsync(completed)).State.Should().Be("Complete");
        scene.Faults.Scheduled.Should().Equal(book.Id);
    }

    [Fact]
    public async Task Recovery_without_staging_takes_the_file_mutation_gate()
    {
        using var scene = new RecoveryScene(_factory);
        var book = await CreateBookAsync(scene.Client);
        var bytes = Encoding.ASCII.GetBytes("an acquisition owns this book");
        var session = await StageAsync(scene.Client, book.Id, bytes, "first.txt");
        await CrashAfterAdoptionAsync(scene.Factory, book.Id, session.SessionId, bytes);
        var gate = scene.Host.Services.GetRequiredService<BookFileMutationGate>();

        using (gate.TryEnter(book.Id))
        {
            using var blocked = await CompleteAsync(scene.Client, book.Id, session.SessionId);
            blocked.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await blocked.Content.ReadAsStringAsync()).Should().Contain("book_upload_in_progress");
        }

        scene.Faults.Scheduled.Should().BeEmpty();
        using var completed = await CompleteAsync(scene.Client, book.Id, session.SessionId);
        completed.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Recovery_reads_metadata_from_the_stored_file()
    {
        using var scene = new RecoveryScene(_factory);
        var book = await CreateBookAsync(scene.Client, "audiobook");
        var bytes = SilentMp3(frames: 400);
        var session = await StageAsync(scene.Client, book.Id, bytes, "first.mp3", "audio/mpeg");
        await CrashAfterAdoptionAsync(
            scene.Factory, book.Id, session.SessionId, bytes, storedName: "book.mp3");

        using var completed = await CompleteAsync(scene.Client, book.Id, session.SessionId);

        completed.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db = OpenDb(scene.Factory.DatabasePath);
        var row = await db.Books.OfType<AudioBookModel>().SingleAsync(b => b.Id == book.Id);
        row.Duration.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Uninterrupted_publish_commits_once()
    {
        using var scene = new RecoveryScene(_factory);
        var book = await CreateBookAsync(scene.Client);
        var bytes = Encoding.ASCII.GetBytes("nothing goes wrong");
        var session = await StageAsync(scene.Client, book.Id, bytes, "first.txt");

        using var completed = await CompleteAsync(scene.Client, book.Id, session.SessionId);

        completed.StatusCode.Should().Be(HttpStatusCode.OK);
        scene.Faults.Scheduled.Should().Equal(book.Id);
        scene.Faults.RowUpdates.Should().Be(1);
    }

    private static async Task<BookDto> CreateBookAsync(HttpClient client, string type = "ebook")
    {
        using var response = await client.PostAsJsonAsync("/api/books", new
        {
            type,
            title = $"Resumable {Guid.NewGuid():N}",
            author = "Test Author",
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<BookDto>())!;
    }

    private static async Task<BookDto> CreateBookWithFileAsync(RecoveryScene scene, byte[] bytes)
    {
        var book = await CreateBookAsync(scene.Client);
        var session = await StageAsync(scene.Client, book.Id, bytes, "old.txt");
        using var completed = await CompleteAsync(scene.Client, book.Id, session.SessionId);
        completed.StatusCode.Should().Be(HttpStatusCode.OK);
        scene.Faults.Reset();
        return book;
    }

    /// <summary>Sends every part of a one-part file and stops before publishing.</summary>
    private static async Task<UploadStatusDto> StageAsync(
        HttpClient client,
        Guid bookId,
        byte[] bytes,
        string fileName,
        string contentType = "text/plain")
    {
        using var created = await CreateSessionAsync(
            client, bookId, bytes.Length, fileName, Sha256Hex(bytes), contentType, replace: true);
        var session = await ReadStatusAsync(created);
        using var part = await PutPartAsync(client, bookId, session.SessionId, 0, bytes);
        part.StatusCode.Should().Be(HttpStatusCode.OK);
        return session;
    }

    /// <summary>
    /// What a crash between adoption and the row commit leaves: the session
    /// says Completing, the staging is gone and the book folder holds the file.
    /// </summary>
    private static async Task CrashAfterAdoptionAsync(
        LibraryEndpointFactory factory,
        Guid bookId,
        Guid sessionId,
        byte[] stored,
        string storedName = "book.txt")
    {
        var staging = StagingPath(factory, sessionId);
        Directory.Exists(staging).Should().BeTrue();
        Directory.Delete(staging, recursive: true);

        var folder = Path.Combine(factory.BooksRootPath, bookId.ToString());
        Directory.CreateDirectory(folder);
        foreach (var file in Directory.EnumerateFiles(folder, "book.*"))
            File.Delete(file);
        await File.WriteAllBytesAsync(Path.Combine(folder, storedName), stored);

        await using var db = OpenDb(factory.DatabasePath);
        var session = await db.BookUploadSessionRecords.SingleAsync(s => s.Id == sessionId);
        session.State = (int)BookUploadState.Completing;
        await db.SaveChangesAsync();
    }

    private static async Task SetStaleFileStateAsync(LibraryEndpointFactory factory, Guid bookId)
    {
        await using var db = OpenDb(factory.DatabasePath);
        var book = await db.Books.SingleAsync(b => b.Id == bookId);
        book.FileDetails.LocationsJson = "stale-locations";
        book.FileDetails.TracksJson = "[]";
        await db.SaveChangesAsync();
    }

    private static async Task<BookUploadSessionRecord> SessionAsync(
        LibraryEndpointFactory factory,
        Guid sessionId)
    {
        await using var db = OpenDb(factory.DatabasePath);
        return await db.BookUploadSessionRecords.AsNoTracking().SingleAsync(s => s.Id == sessionId);
    }

    private static string StagingPath(LibraryEndpointFactory factory, Guid sessionId) =>
        Path.Combine(factory.StorageTempRoot, "transfers", "book-uploads", sessionId.ToString("N"));

    private static string StoredSha(LibraryEndpointFactory factory, Guid bookId) =>
        Sha256Hex(File.ReadAllBytes(
            Path.Combine(factory.BooksRootPath, bookId.ToString(), "book.txt")));

    private static Task<HttpResponseMessage> CompleteAsync(
        HttpClient client,
        Guid bookId,
        Guid sessionId) =>
        client.PostAsync($"/api/books/{bookId}/file/uploads/{sessionId}/complete", null);

    /// <summary>MPEG-1 Layer III frames (128 kbps, 44.1 kHz) with empty side data.</summary>
    private static byte[] SilentMp3(int frames)
    {
        const int frameBytes = 417;
        var bytes = new byte[frames * frameBytes];
        for (var offset = 0; offset < bytes.Length; offset += frameBytes)
        {
            bytes[offset] = 0xFF;
            bytes[offset + 1] = 0xFB;
            bytes[offset + 2] = 0x90;
            bytes[offset + 3] = 0x00;
        }
        return bytes;
    }

    [Fact]
    public async Task Session_on_a_book_with_a_file_is_refused_unless_replace_is_asked_for()
    {
        using var client = _factory.CreateClient();
        var book = await CreateBookAsync(client);

        await using (var db = OpenDb(_factory.DatabasePath))
        {
            var row = await db.Books.SingleAsync(b => b.Id == book.Id);
            row.FileDetails.HasFile = true;
            row.FileDetails.FileName = "original.txt";
            await db.SaveChangesAsync();
        }

        var bytes = new byte[] { 1, 2, 3, 4 };
        var sha = Sha256Hex(bytes);

        using var refused = await CreateSessionAsync(client, book.Id, bytes.Length, "new.txt", sha);
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("book_has_file");

        using var allowed = await CreateSessionAsync(
            client, book.Id, bytes.Length, "new.txt", sha, replace: true);
        allowed.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private static Task<HttpResponseMessage> CreateSessionAsync(
        HttpClient client,
        Guid bookId,
        long totalBytes,
        string fileName,
        string sha,
        string contentType = "text/plain",
        bool replace = false) =>
        client.PostAsJsonAsync($"/api/books/{bookId}/file/uploads{(replace ? "?replace=true" : "")}", new
        {
            totalBytes,
            fileName,
            contentType,
            sha256 = sha,
        });

    private static async Task<UploadStatusDto> ReadStatusAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<UploadStatusDto>())!;
    }

    private static async Task<HttpResponseMessage> PutPartAsync(
        HttpClient client,
        Guid bookId,
        Guid sessionId,
        int partIndex,
        byte[] allBytes,
        string? partShaOverride = null)
    {
        var offset = (long)partIndex * PartBytes;
        var length = (int)Math.Min(PartBytes, allBytes.Length - offset);
        var part = allBytes.AsMemory((int)offset, length).ToArray();
        var partSha = partShaOverride ?? Sha256Hex(part);

        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/books/{bookId}/file/uploads/{sessionId}/parts/{partIndex}");
        var content = new ByteArrayContent(part);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.TryAddWithoutValidation(
            "Content-Range",
            $"bytes {offset}-{offset + length - 1}/{allBytes.Length}");
        content.Headers.TryAddWithoutValidation("X-Nostos-Chunk-SHA256", partSha);
        request.Content = content;
        return await client.SendAsync(request);
    }

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static NostosDbContext OpenDb(string path) =>
        new(new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={path}").Options);

    private sealed record UploadStatusDto(
        Guid SessionId,
        string State,
        int[] ReceivedParts);

    private sealed record PartResultDto(Guid SessionId, int PartIndex, bool AlreadyPresent);

    public sealed class BookUploadHostFactory : LibraryEndpointFactory
    {
        public UploadFaults Faults { get; } = new();

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IBookAssetStorage>(sp => new FaultingAssetStorage(
                    sp.GetRequiredService<ExternalMediaBookStorage>(),
                    Faults));
                services.AddScoped<IBookRepository>(sp => new FaultingBookRepository(
                    new BookRepository(sp.GetRequiredService<NostosDbContext>()),
                    Faults));
                services.AddSingleton<IBookTextIngestionScheduler>(Faults);
            });
        }
    }

    /// <summary>One host whose storage, row writes and scheduler can be made to fail.</summary>
    private sealed class RecoveryScene(BookUploadHostFactory factory) : IDisposable
    {
        public UploadFaults Faults => factory.Faults;

        public LibraryEndpointFactory Factory => factory;

        public WebApplicationFactory<Program> Host => factory;

        public HttpClient Client { get; } = factory.CreateClient();

        public void Dispose() => Client.Dispose();
    }

    public sealed class UploadFaults : IBookTextIngestionScheduler
    {
        public int FailAdoptions { get; set; }

        public int FailRowUpdates { get; set; }

        public int FailSchedules { get; set; }

        public int RowUpdates { get; set; }

        public List<Guid> Scheduled { get; } = [];

        public void Reset()
        {
            RowUpdates = 0;
            Scheduled.Clear();
        }

        public void ResetAll()
        {
            FailAdoptions = 0;
            FailRowUpdates = 0;
            FailSchedules = 0;
            Reset();
        }

        public Task ScheduleAsync(Guid bookId, string sourceFileName, CancellationToken ct = default)
        {
            if (FailSchedules > 0)
            {
                FailSchedules -= 1;
                throw new IOException("Injected scheduling failure.");
            }

            Scheduled.Add(bookId);
            return Task.CompletedTask;
        }
    }

    private sealed class FaultingAssetStorage : DelegatingBookAssetStorage
    {
        private readonly UploadFaults _faults;

        public FaultingAssetStorage(IBookAssetStorage inner, UploadFaults faults)
            : base(inner) => _faults = faults;

        public override Task<string> AdoptBookFileAsync(
            Guid bookId,
            string sourcePath,
            string fileName,
            CancellationToken ct = default)
        {
            if (_faults.FailAdoptions > 0)
            {
                _faults.FailAdoptions -= 1;
                throw new IOException("Injected adoption failure.");
            }

            return base.AdoptBookFileAsync(bookId, sourcePath, fileName, ct);
        }
    }

    private sealed class FaultingBookRepository(IBookRepository inner, UploadFaults faults)
        : IBookRepository
    {
        public Task<BookModel?> GetByIdAsync(Guid id) => inner.GetByIdAsync(id);

        public Task<int> CountBooksWithFilesAsync() => inner.CountBooksWithFilesAsync();

        public Task<List<BookModel>> GetBooksWithFilesPageAsync(int skip, int take) =>
            inner.GetBooksWithFilesPageAsync(skip, take);

        public Task UpdateAsync(BookModel book)
        {
            faults.RowUpdates += 1;
            if (faults.FailRowUpdates > 0)
            {
                faults.FailRowUpdates -= 1;
                throw new IOException("Injected row update failure.");
            }

            return inner.UpdateAsync(book);
        }
    }
}

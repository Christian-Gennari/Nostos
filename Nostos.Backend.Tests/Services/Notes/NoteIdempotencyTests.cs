using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Configuration;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Data.Repositories;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Services.Notes;
using Nostos.Backend.Tests.Services.Ai;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Services.Notes;

/// <summary>
/// Exactly-once capture (issue #260 §3). A repeated (ClientId, IdempotencyKey)
/// pair replays the stored result without touching the notes table; a request
/// without the pair keeps the original non-idempotent behaviour; a rejected
/// capture writes no receipt so it stays retryable with the same key. The
/// concurrency test is what proves the process-local gate rather than the
/// sequential case.
/// </summary>
public sealed class NoteIdempotencyTests : IClassFixture<SqliteTestFixture>
{
    private const string Client = "test-client";
    private const string Key = "key-0001";

    private readonly SqliteTestFixture _fixture;

    public NoteIdempotencyTests(SqliteTestFixture fixture) => _fixture = fixture;

    // ------------------------------------------------------------------
    // Replay
    // ------------------------------------------------------------------

    [Fact]
    public async Task Same_key_twice_replays_the_stored_note_without_a_second_row()
    {
        var h = NewHarness();
        using var _ = h;
        var book = await SeedBookAsync(h.Db);

        var first = await h.Service.CreateAsync(book.Id, new CreateNoteDto("captured"), Client, Key);
        var second = await h.Service.CreateAsync(book.Id, new CreateNoteDto("captured"), Client, Key);

        first.Success.Should().BeTrue();
        second.Success.Should().BeTrue();
        second.Value.Should().Be(first.Value, "the replay returns the stored result verbatim");
        second.Value!.Id.Should().Be(first.Value!.Id);

        (await h.Db.Notes.CountAsync()).Should().Be(1, "a replay must not create a second note");
        (await h.Db.NoteCommandReceipts.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task CaptureAsync_shares_the_same_idempotency_path()
    {
        var h = NewHarness();
        using var _ = h;
        var book = await SeedBookAsync(h.Db);

        var request = new CaptureNoteRequest(book.Id, "a spoken capture", ClientId: Client, IdempotencyKey: Key);
        var first = await h.Service.CaptureAsync(request);
        var second = await h.Service.CaptureAsync(request);

        first.Success.Should().BeTrue();
        second.Value!.Id.Should().Be(first.Value!.Id);
        (await h.Db.Notes.CountAsync()).Should().Be(1);
        (await h.Db.NoteCommandReceipts.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Different_key_creates_two_notes()
    {
        var h = NewHarness();
        using var _ = h;
        var book = await SeedBookAsync(h.Db);

        var first = await h.Service.CreateAsync(book.Id, new CreateNoteDto("one"), Client, "key-a");
        var second = await h.Service.CreateAsync(book.Id, new CreateNoteDto("two"), Client, "key-b");

        first.Success.Should().BeTrue();
        second.Success.Should().BeTrue();
        second.Value!.Id.Should().NotBe(first.Value!.Id);
        (await h.Db.Notes.CountAsync()).Should().Be(2);
        (await h.Db.NoteCommandReceipts.CountAsync()).Should().Be(2);
    }

    // ------------------------------------------------------------------
    // No keys => today's behaviour
    // ------------------------------------------------------------------

    [Fact]
    public async Task No_keys_keeps_the_non_idempotent_behaviour()
    {
        var h = NewHarness();
        using var _ = h;
        var book = await SeedBookAsync(h.Db);

        var first = await h.Service.CreateAsync(book.Id, new CreateNoteDto("one"));
        var second = await h.Service.CreateAsync(book.Id, new CreateNoteDto("one"));

        first.Success.Should().BeTrue();
        second.Success.Should().BeTrue();
        second.Value!.Id.Should().NotBe(first.Value!.Id);
        (await h.Db.Notes.CountAsync()).Should().Be(2, "an unkeyed create is not deduplicated");
        (await h.Db.NoteCommandReceipts.CountAsync()).Should().Be(0);
    }

    // ------------------------------------------------------------------
    // Invalid key pairs
    // ------------------------------------------------------------------

    [Fact]
    public async Task Only_one_half_of_the_key_pair_is_invalid_idempotency()
    {
        var h = NewHarness();
        using var _ = h;
        var book = await SeedBookAsync(h.Db);

        var clientOnly = await h.Service.CreateAsync(book.Id, new CreateNoteDto("x"), Client, null);
        var keyOnly = await h.Service.CreateAsync(book.Id, new CreateNoteDto("x"), null, Key);

        clientOnly.Success.Should().BeFalse();
        clientOnly.ErrorCode.Should().Be(NoteErrorCodes.InvalidIdempotency);
        keyOnly.Success.Should().BeFalse();
        keyOnly.ErrorCode.Should().Be(NoteErrorCodes.InvalidIdempotency);

        (await h.Db.Notes.CountAsync()).Should().Be(0);
        (await h.Db.NoteCommandReceipts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Oversized_key_values_are_invalid_idempotency()
    {
        var h = NewHarness();
        using var _ = h;
        var book = await SeedBookAsync(h.Db);

        var longClient = await h.Service.CreateAsync(
            book.Id, new CreateNoteDto("x"), new string('c', 65), Key);
        var longKey = await h.Service.CreateAsync(
            book.Id, new CreateNoteDto("x"), Client, new string('k', 129));

        longClient.ErrorCode.Should().Be(NoteErrorCodes.InvalidIdempotency);
        longKey.ErrorCode.Should().Be(NoteErrorCodes.InvalidIdempotency);
        (await h.Db.Notes.CountAsync()).Should().Be(0);
    }

    // ------------------------------------------------------------------
    // Rejected capture stays retryable
    // ------------------------------------------------------------------

    [Fact]
    public async Task Rejected_capture_writes_no_receipt_and_the_same_key_succeeds_on_retry()
    {
        var h = NewHarness();
        using var _ = h;
        var book = await SeedBookAsync(h.Db);

        var rejected = await h.Service.CreateAsync(book.Id, new CreateNoteDto("   "), Client, Key);
        rejected.Success.Should().BeFalse();
        rejected.ErrorCode.Should().Be(NoteErrorCodes.EmptyNote);
        (await h.Db.NoteCommandReceipts.CountAsync()).Should().Be(0,
            "a fixable rejection must not burn the key");

        var corrected = await h.Service.CreateAsync(book.Id, new CreateNoteDto("now it has content"), Client, Key);

        corrected.Success.Should().BeTrue();
        (await h.Db.Notes.CountAsync()).Should().Be(1);
        (await h.Db.NoteCommandReceipts.CountAsync()).Should().Be(1);
    }

    // ------------------------------------------------------------------
    // Concurrency — proves the gate, not the sequential case
    // ------------------------------------------------------------------

    [Fact]
    public async Task Concurrent_same_key_captures_create_exactly_one_note()
    {
        var path = _fixture.CreateDatabasePath();
        var bookId = await SeedBookOnPathAsync(path);

        // Two independent contexts over the same database, as two concurrent
        // in-process requests would use.
        using var first = NewHarness(path);
        using var second = NewHarness(path);
        var dto = new CreateNoteDto("concurrent capture");

        var calls = new[]
        {
            first.Service.CreateAsync(bookId, dto, Client, Key),
            second.Service.CreateAsync(bookId, dto, Client, Key),
        };
        var results = await Task.WhenAll(calls);

        results.Should().OnlyContain(r => r.Success);
        results.Select(r => r.Value!.Id).Distinct().Should().ContainSingle(
            "both callers must observe the one persisted note");

        await using var verify = CreateContext(path);
        (await verify.Notes.CountAsync()).Should().Be(1, "the gate lets only one capture through");
        (await verify.NoteCommandReceipts.CountAsync()).Should().Be(1);
    }

    // ------------------------------------------------------------------
    // HTTP surface
    // ------------------------------------------------------------------

    [Fact]
    public async Task Replay_returns_201_with_an_identical_body_and_location()
    {
        using var factory = new LibraryEndpointFactory();
        var client = factory.CreateClient();
        var book = await CreateBookAsync(client);

        using var firstRequest = new HttpRequestMessage(
            HttpMethod.Post, $"/api/books/{book.Id}/notes")
        {
            Content = JsonContent.Create(new { content = "captured over HTTP" }),
        };
        firstRequest.Headers.Add("X-Client-Id", Client);
        firstRequest.Headers.Add("Idempotency-Key", Key);

        var first = await client.SendAsync(firstRequest);
        var firstBody = await first.Content.ReadAsStringAsync();

        using var secondRequest = new HttpRequestMessage(
            HttpMethod.Post, $"/api/books/{book.Id}/notes")
        {
            Content = JsonContent.Create(new { content = "captured over HTTP" }),
        };
        secondRequest.Headers.Add("X-Client-Id", Client);
        secondRequest.Headers.Add("Idempotency-Key", Key);

        var second = await client.SendAsync(secondRequest);
        var secondBody = await second.Content.ReadAsStringAsync();

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.Created);
        second.Headers.Location.Should().Be(first.Headers.Location);
        secondBody.Should().Be(firstBody, "the replay must be byte-identical");

        await using var verify = CreateContext(factory.DatabasePath);
        (await verify.Notes.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Single_half_key_returns_http_400()
    {
        using var factory = new LibraryEndpointFactory();
        var client = factory.CreateClient();
        var book = await CreateBookAsync(client);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/books/{book.Id}/notes")
        {
            Content = JsonContent.Create(new { content = "only a key" }),
        };
        request.Headers.Add("Idempotency-Key", Key);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("ClientId and IdempotencyKey must be supplied together.");

        await using var verify = CreateContext(factory.DatabasePath);
        (await verify.Notes.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Oversized_keys_return_http_400()
    {
        using var factory = new LibraryEndpointFactory();
        var client = factory.CreateClient();
        var book = await CreateBookAsync(client);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/books/{book.Id}/notes")
        {
            Content = JsonContent.Create(new { content = "oversized keys" }),
        };
        request.Headers.Add("X-Client-Id", new string('c', 65));
        request.Headers.Add("Idempotency-Key", new string('k', 129));

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("ClientId is limited to 64 characters and IdempotencyKey to 128.");

        await using var verify = CreateContext(factory.DatabasePath);
        (await verify.Notes.CountAsync()).Should().Be(0);
        (await verify.NoteCommandReceipts.CountAsync()).Should().Be(0);
    }

    // ------------------------------------------------------------------
    // Retention (note receipts pruned by the existing service + worker)
    // ------------------------------------------------------------------

    [Fact]
    public async Task PruneNoteReceipts_removes_expired_and_over_cap_and_keeps_the_rest()
    {
        var path = _fixture.CreateDatabasePath();
        await using (var bootstrap = CreateContext(path))
        {
            // 3 expired against the 90-day window.
            for (var i = 0; i < 3; i++)
            {
                bootstrap.NoteCommandReceipts.Add(NewReceipt($"expired-{i}", DateTime.UtcNow.AddDays(-91 - i)));
            }

            // 102 fresh against a cap of 100, so the two oldest fresh rows are
            // the cap victims after the expired phase runs first.
            var now = DateTime.UtcNow;
            for (var i = 0; i < 102; i++)
            {
                bootstrap.NoteCommandReceipts.Add(NewReceipt($"fresh-{i:D3}", now.AddMinutes(-i)));
            }

            await bootstrap.SaveChangesAsync();
        }

        var retention = NewRetention(path, maximumReceipts: 100);
        var result = await retention.PruneNoteReceiptsAsync();

        result.ExpiredDeleted.Should().Be(3);
        result.OverCapDeleted.Should().Be(2);
        result.Remaining.Should().Be(100);

        await using var verify = CreateContext(path);
        (await verify.NoteCommandReceipts.CountAsync()).Should().Be(100);
        (await verify.NoteCommandReceipts.AnyAsync(r => r.IdempotencyKey == "expired-0")).Should().BeFalse();
        (await verify.NoteCommandReceipts.AnyAsync(r => r.IdempotencyKey == "fresh-000")).Should().BeTrue();
        (await verify.NoteCommandReceipts.AnyAsync(r => r.IdempotencyKey == "fresh-101")).Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // Harness
    // ------------------------------------------------------------------

    private Harness NewHarness() => NewHarness(_fixture.CreateDatabasePath());

    private static Harness NewHarness(string path)
    {
        var db = CreateContext(path);
        var topics = new TopicRepository(db);
        var service = new NoteService(
            new NoteRepository(db),
            new BookRepository(db),
            topics,
            new NoteProcessorService(topics),
            new FakeThoughtProcessor(),
            db,
            NullLogger<NoteService>.Instance);
        return new Harness(db, service);
    }

    private static NostosDbContext CreateContext(string path)
    {
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;
        var db = new NostosDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    private static LibraryReceiptRetentionService NewRetention(string path, int maximumReceipts)
    {
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;
        return new LibraryReceiptRetentionService(
            new TestContextFactory(options),
            new LibraryReceiptRetentionOptions
            {
                RetentionDays = 90,
                MaximumReceipts = maximumReceipts,
            },
            new SilentLogger<LibraryReceiptRetentionService>());
    }

    private static NoteCommandReceipt NewReceipt(string key, DateTime createdAt) => new()
    {
        ClientId = Client,
        IdempotencyKey = key,
        Command = "CaptureNote",
        ResultJson = "{}",
        CreatedAtUtc = createdAt,
    };

    private static async Task<PhysicalBookModel> SeedBookAsync(NostosDbContext db)
    {
        var book = new PhysicalBookModel { Id = Guid.NewGuid(), Title = "A Book" };
        db.Books.Add(book);
        await db.SaveChangesAsync();
        return book;
    }

    private static async Task<Guid> SeedBookOnPathAsync(string path)
    {
        await using var db = CreateContext(path);
        return (await SeedBookAsync(db)).Id;
    }

    private static async Task<BookDto> CreateBookAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/books", new
        {
            type = "physical",
            title = $"Idempotency Book {Guid.NewGuid():N}",
            author = "An Author",
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<BookDto>())!;
    }

    private sealed class Harness(NostosDbContext db, NoteService service) : IDisposable
    {
        public NostosDbContext Db { get; } = db;
        public NoteService Service { get; } = service;
        public void Dispose() => Db.Dispose();
    }

    private sealed class TestContextFactory(DbContextOptions<NostosDbContext> options)
        : IDbContextFactory<NostosDbContext>
    {
        public NostosDbContext CreateDbContext() => new(options);

        public Task<NostosDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class SilentLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => false;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }
}

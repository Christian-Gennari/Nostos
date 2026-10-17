using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Data.Repositories;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Notes;
using Nostos.Backend.Tests.Services.Ai;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Services.Notes;

/// <summary>
/// Finding notes by who wrote the book (issue #1038). The Studio's "@" palette
/// is typed as a person's name first — "@schopenhauer will" — and the search
/// matched only note text and book titles, so an author's name found nothing.
/// </summary>
public sealed class NoteAuthorSearchTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public NoteAuthorSearchTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task An_authors_name_alone_finds_their_notes_and_passages()
    {
        using var h = CreateHarness();
        var seed = await AuthorSearchSeed.SeedAsync(h.Db);

        var hits = await h.Service.SearchAsync("Schopenhauer", 50);

        hits.Select(hit => hit.Id).Should().BeEquivalentTo(
            [seed.WillQuoteId, seed.SufferingQuoteId, seed.PassageId]);
        hits.Should().OnlyContain(hit => hit.BookAuthor == "Arthur Schopenhauer");
    }

    [Fact]
    public async Task An_author_and_a_word_put_that_authors_matching_note_first()
    {
        using var h = CreateHarness();
        var seed = await AuthorSearchSeed.SeedAsync(h.Db);

        var hits = await h.Service.SearchAsync("schopenhauer will", 50);

        // Nietzsche's note about the will shares one word; it may follow, but
        // never ahead of the note that holds both.
        hits.Should().NotBeEmpty();
        hits[0].Id.Should().Be(seed.WillQuoteId);
    }

    [Fact]
    public async Task A_full_name_matches_although_no_field_holds_both_words_in_that_order()
    {
        using var h = CreateHarness();
        var seed = await AuthorSearchSeed.SeedAsync(h.Db);

        var hits = await new NoteRepository(h.Db).SearchByAllTermsAsync(
            ["schopenhauer", "arthur"], 50);

        hits.Select(n => n.Id).Should().BeEquivalentTo(
            [seed.WillQuoteId, seed.SufferingQuoteId, seed.PassageId]);
    }

    [Fact]
    public async Task Every_term_has_to_match_some_field()
    {
        using var h = CreateHarness();
        await AuthorSearchSeed.SeedAsync(h.Db);

        var hits = await new NoteRepository(h.Db).SearchByAllTermsAsync(
            ["schopenhauer", "zarathustra"], 50);

        hits.Should().BeEmpty();
    }

    [Fact]
    public async Task Matching_ignores_case_including_non_ascii_letters()
    {
        using var h = CreateHarness();
        var seed = await AuthorSearchSeed.SeedAsync(h.Db);
        var repo = new NoteRepository(h.Db);

        (await repo.SearchByAllTermsAsync(["SCHOPENHAUER"], 50)).Should().HaveCount(3);
        (await repo.SearchByAllTermsAsync(["SØREN"], 50))
            .Should().ContainSingle().Which.Id.Should().Be(seed.KierkegaardId);
    }

    [Fact]
    public async Task A_passage_is_found_by_its_title()
    {
        using var h = CreateHarness();
        var seed = await AuthorSearchSeed.SeedAsync(h.Db);

        var hits = await h.Service.SearchAsync("vanity of existence", 50);

        hits.Should().ContainSingle().Which.Id.Should().Be(seed.PassageId);
    }

    private Harness CreateHarness()
    {
        var path = _fixture.CreateDatabasePath();
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;

        var db = new NostosDbContext(options);
        db.Database.EnsureCreated();
        return new Harness(db, AuthorSearchSeed.CreateService(db));
    }

    private sealed record Harness(NostosDbContext Db, NoteService Service) : IDisposable
    {
        public void Dispose() => Db.Dispose();
    }
}

/// <summary>
/// A small library with two authors who both wrote about the will, shared by
/// the SQLite tests and the PostgreSQL compatibility test.
/// </summary>
internal static class AuthorSearchSeed
{
    public sealed record Ids(
        Guid WillQuoteId,
        Guid SufferingQuoteId,
        Guid PassageId,
        Guid NietzscheId,
        Guid KierkegaardId);

    public static NoteService CreateService(NostosDbContext db)
    {
        var topics = new TopicRepository(db);
        return new NoteService(
            new NoteRepository(db),
            new BookRepository(db),
            topics,
            new NoteProcessorService(topics),
            new FakeThoughtProcessor(),
            db,
            NullLogger<NoteService>.Instance);
    }

    public static async Task<Ids> SeedAsync(NostosDbContext db)
    {
        var created = new DateTime(2026, 10, 17, 9, 0, 0, DateTimeKind.Utc);
        var world = new PhysicalBookModel
        {
            Id = Guid.NewGuid(),
            Title = "The World as Will and Representation",
            Author = "Arthur Schopenhauer",
        };
        var zarathustra = new PhysicalBookModel
        {
            Id = Guid.NewGuid(),
            Title = "Thus Spoke Zarathustra",
            Author = "Friedrich Nietzsche",
        };
        var either = new PhysicalBookModel
        {
            Id = Guid.NewGuid(),
            Title = "Either/Or",
            Author = "Søren Kierkegaard",
        };
        db.Books.AddRange(world, zarathustra, either);

        NoteModel Note(Guid bookId, string content, string? quote, int minutes) => new()
        {
            Id = Guid.NewGuid(),
            BookId = bookId,
            Content = content,
            SelectedText = quote,
            CreatedAt = created.AddMinutes(minutes),
        };

        var willQuote = Note(world.Id, "On striving.", "Every act of the will is an act of the body.", 1);
        var suffering = Note(world.Id, "Life swings like a pendulum.", "Life swings like a pendulum between pain and boredom.", 2);
        var passage = Note(world.Id, "Kept to reread.", "Human life must be some kind of mistake.", 3);
        passage.Kind = NoteKinds.Passage;
        passage.PassageTitle = "On the Vanity of Existence";
        passage.SourceAnchorKind = "pdf_page";
        passage.SourceAnchorValue = "12";
        passage.PassageEnd = "13";
        var nietzsche = Note(zarathustra.Id, "Overcoming.", "Willing liberates: that is the true doctrine of will.", 4);
        var kierkegaard = Note(either.Id, "Choice.", "Choose despair.", 5);
        db.Notes.AddRange(willQuote, suffering, passage, nietzsche, kierkegaard);
        await db.SaveChangesAsync();

        return new Ids(willQuote.Id, suffering.Id, passage.Id, nietzsche.Id, kierkegaard.Id);
    }
}

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Data.Repositories;
using Nostos.Backend.Mapping;
using Nostos.Backend.Services.Notes;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Services.Notes;

/// <summary>
/// PDF quotes carry ligature glyphs ("Speciﬁcally", issue #1379) that search
/// and export treat as different characters.
/// </summary>
public sealed class TextLigaturesTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public TextLigaturesTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData("Speciﬁcally", "Specifically")]
    [InlineData("oﬀer ﬂow", "offer flow")]
    [InlineData("oﬃce, baﬄe", "office, baffle")]
    [InlineData("ﬅ ﬆ", "st st")]
    [InlineData("plain text", "plain text")]
    public void Expand_replaces_latin_ligatures_with_their_letters(string input, string expected) =>
        TextLigatures.Expand(input).Should().Be(expected);

    [Fact]
    public void Expand_keeps_null_and_empty()
    {
        TextLigatures.Expand(null).Should().BeNull();
        TextLigatures.Expand("").Should().BeEmpty();
    }

    [Fact]
    public void A_created_note_stores_its_quote_without_ligatures()
    {
        var model = new CreateNoteDto(Content: "", SelectedText: "Speciﬁcally").ToModel(Guid.NewGuid());

        model.SelectedText.Should().Be("Specifically");
    }

    [Fact]
    public void An_edited_quote_is_stored_without_ligatures()
    {
        var model = new NoteModel { SelectedText = "old" };

        model.Apply(new UpdateNoteDto(Content: "x", SelectedText: "eﬃcient"));

        model.SelectedText.Should().Be("efficient");
    }

    [Fact]
    public async Task A_search_finds_a_quote_stored_before_ligatures_were_cleaned()
    {
        var path = _fixture.CreateDatabasePath();
        var options = new DbContextOptionsBuilder<NostosDbContext>().UseSqlite($"Data Source={path}").Options;
        using var db = new NostosDbContext(options);
        db.Database.EnsureCreated();
        var book = new PhysicalBookModel { Id = Guid.NewGuid(), Title = "Dagbok", Author = "A" };
        db.Books.Add(book);
        var note = new NoteModel
        {
            Id = Guid.NewGuid(),
            BookId = book.Id,
            Content = "",
            SelectedText = "Speciﬁcally, the oﬃce",
            CreatedAt = DateTime.UtcNow,
        };
        db.Notes.Add(note);
        await db.SaveChangesAsync();
        var repo = new NoteRepository(db);

        (await repo.SearchByAllTermsAsync(["specifically"], 50)).Should().ContainSingle().Which.Id.Should().Be(note.Id);
        (await repo.SearchByAllTermsAsync(["OFFICE"], 50)).Should().ContainSingle();
        (await repo.BrowseAsync(new NoteBrowseQuery("Specifically"), 50, 0)).Total.Should().Be(1);
        (await repo.SearchByAllTermsAsync(["specifically", "nothing"], 50)).Should().BeEmpty();
    }
}

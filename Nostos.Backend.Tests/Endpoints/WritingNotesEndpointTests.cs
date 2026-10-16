using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Endpoints;

public sealed class WritingNotesEndpointTests : IClassFixture<LibraryEndpointFactory>
{
    private readonly LibraryEndpointFactory _factory;

    public WritingNotesEndpointTests(LibraryEndpointFactory factory)
    {
        _factory = factory;
    }

    private HttpClient Client => _factory.CreateClient();

    private async Task<(Guid WritingId, Guid NoteId, Guid BookId, string BookTitle)> SeedGraphAsync(WritingType writingType = WritingType.Document)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();

        var book = new PhysicalBookModel
        {
            Id = Guid.NewGuid(),
            Title = "Phaedrus",
            Author = "Plato",
            CreatedAt = DateTime.UtcNow,
        };
        var note = new NoteModel
        {
            Id = Guid.NewGuid(),
            BookId = book.Id,
            Book = book,
            Content = "The soul is immortal, for that which is ever in motion is immortal.",
            SelectedText = "ever in motion is immortal",
            CfiRange = "epubcfi(/6/12)",
            CreatedAt = DateTime.UtcNow,
            SourceAnchorKind = "epub_cfi",
            SourceAnchorValue = "epubcfi(/6/12)",
            AnchorVerified = true,
        };
        var writing = new WritingModel
        {
            Id = Guid.NewGuid(),
            Name = writingType == WritingType.Folder ? "Philosophy Folder" : "Essay on the Soul",
            Type = writingType,
            Content = writingType == WritingType.Folder ? null : "<p>Discussion of immortality</p>",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.Books.Add(book);
        db.Notes.Add(note);
        db.Writings.Add(writing);
        await db.SaveChangesAsync();

        return (writing.Id, note.Id, book.Id, book.Title);
    }

    [Fact]
    public async Task A_kept_passage_carries_its_title_and_size_and_only_its_opening()
    {
        // Issue #1014: Studio lists a passage by its title, never a whole chapter.
        var (writingId, noteId, _, _) = await SeedGraphAsync();
        var text = string.Join(' ', Enumerable.Repeat("Imagine, then, that it takes place in Spain.", 80));
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
            var note = await db.Notes.SingleAsync(n => n.Id == noteId);
            note.Kind = "passage";
            note.PassageTitle = "The Grand Inquisitor";
            note.PassageEnd = "epubcfi(/6/14)";
            note.SelectedText = text;
            note.CfiRange = null;
            await db.SaveChangesAsync();
        }

        var response = await Client.PostAsJsonAsync($"/api/writings/{writingId}/notes", new AddWritingSourceDto(noteId));
        var dto = (await response.Content.ReadFromJsonAsync<WritingSourceDto>())!;

        dto.Kind.Should().Be("passage");
        dto.PassageTitle.Should().Be("The Grand Inquisitor");
        dto.PassageEnd.Should().Be("epubcfi(/6/14)");
        dto.PassageWordCount.Should().Be(text.Split(' ').Length);
        dto.SelectedText!.Length.Should().BeLessThan(text.Length);
    }

    [Fact]
    public async Task GetKeptNotes_EmptyWhenNone_Returns200WithEmptyArray()
    {
        var (writingId, _, _, _) = await SeedGraphAsync();

        var response = await Client.GetAsync($"/api/writings/{writingId}/notes");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var list = await response.Content.ReadFromJsonAsync<WritingSourceDto[]>();
        list.Should().NotBeNull();
        list.Should().BeEmpty();
    }

    [Fact]
    public async Task AddKeptNote_Returns201First_Then200OnRepeatAdd_Idempotent()
    {
        var (writingId, noteId, bookId, bookTitle) = await SeedGraphAsync();

        // First add: 201 Created
        var firstResponse = await Client.PostAsJsonAsync(
            $"/api/writings/{writingId}/notes",
            new AddWritingSourceDto(noteId));

        firstResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        firstResponse.Headers.Location.Should().NotBeNull();

        var firstDto = await firstResponse.Content.ReadFromJsonAsync<WritingSourceDto>();
        firstDto.Should().NotBeNull();
        firstDto!.Id.Should().Be(noteId);
        firstDto.BookId.Should().Be(bookId);
        firstDto.BookTitle.Should().Be(bookTitle);
        firstDto.Content.Should().Contain("The soul is immortal");
        firstDto.SelectedText.Should().Be("ever in motion is immortal");
        firstDto.CfiRange.Should().Be("epubcfi(/6/12)");
        firstDto.SourceAnchorKind.Should().Be("epub_cfi");
        firstDto.SourceAnchorValue.Should().Be("epubcfi(/6/12)");
        firstDto.AnchorVerified.Should().BeTrue();
        var addedAt = firstDto.AddedAt;

        // Second add (repeat): 200 OK (idempotent, exactly one row)
        var secondResponse = await Client.PostAsJsonAsync(
            $"/api/writings/{writingId}/notes",
            new AddWritingSourceDto(noteId));

        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var secondDto = await secondResponse.Content.ReadFromJsonAsync<WritingSourceDto>();
        secondDto.Should().NotBeNull();
        secondDto!.Id.Should().Be(noteId);
        secondDto.AddedAt.Should().Be(addedAt);

        // GET confirms exactly one row
        var listResponse = await Client.GetAsync($"/api/writings/{writingId}/notes");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await listResponse.Content.ReadFromJsonAsync<WritingSourceDto[]>();
        list.Should().ContainSingle();
    }

    [Fact]
    public async Task RemoveKeptNote_Returns204_And204AgainOnSecondRemove_Idempotent()
    {
        var (writingId, noteId, _, _) = await SeedGraphAsync();

        // Add
        var addRes = await Client.PostAsJsonAsync(
            $"/api/writings/{writingId}/notes",
            new AddWritingSourceDto(noteId));
        addRes.StatusCode.Should().Be(HttpStatusCode.Created);

        // First remove: 204 NoContent
        var firstDelete = await Client.DeleteAsync($"/api/writings/{writingId}/notes/{noteId}");
        firstDelete.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Second remove: 204 NoContent (idempotent even when membership row is already gone)
        var secondDelete = await Client.DeleteAsync($"/api/writings/{writingId}/notes/{noteId}");
        secondDelete.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Confirm list is empty
        var listResponse = await Client.GetAsync($"/api/writings/{writingId}/notes");
        var list = await listResponse.Content.ReadFromJsonAsync<WritingSourceDto[]>();
        list.Should().BeEmpty();
    }

    [Fact]
    public async Task GetKeptNotes_Returns404_WhenWritingDoesNotExist()
    {
        var missingWritingId = Guid.NewGuid();
        var response = await Client.GetAsync($"/api/writings/{missingWritingId}/notes");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AddKeptNote_Returns404_WhenWritingDoesNotExist()
    {
        var (_, noteId, _, _) = await SeedGraphAsync();
        var missingWritingId = Guid.NewGuid();

        var response = await Client.PostAsJsonAsync(
            $"/api/writings/{missingWritingId}/notes",
            new AddWritingSourceDto(noteId));
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AddKeptNote_Returns404_WhenNoteDoesNotExist()
    {
        var (writingId, _, _, _) = await SeedGraphAsync();
        var missingNoteId = Guid.NewGuid();

        var response = await Client.PostAsJsonAsync(
            $"/api/writings/{writingId}/notes",
            new AddWritingSourceDto(missingNoteId));
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AddKeptNote_Returns400_WhenTargetWritingIsAFolder()
    {
        var (folderId, noteId, _, _) = await SeedGraphAsync(writingType: WritingType.Folder);

        var response = await Client.PostAsJsonAsync(
            $"/api/writings/{folderId}/notes",
            new AddWritingSourceDto(noteId));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RemoveKeptNote_Returns404_WhenWritingDoesNotExist()
    {
        var missingWritingId = Guid.NewGuid();
        var noteId = Guid.NewGuid();

        var response = await Client.DeleteAsync($"/api/writings/{missingWritingId}/notes/{noteId}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetKeptNotes_DegenerateDanglingRow_Returns200AndOmitsRow()
    {
        var (writingId, noteId, _, _) = await SeedGraphAsync();

        // Valid add
        await Client.PostAsJsonAsync(
            $"/api/writings/{writingId}/notes",
            new AddWritingSourceDto(noteId));

        // Inject dangling row directly into database file
        var danglingNoteId = Guid.NewGuid();
        await using (var conn = new SqliteConnection($"Data Source={_factory.DatabasePath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                PRAGMA foreign_keys = OFF;
                INSERT INTO WritingNotes (WritingId, NoteId, AddedAt)
                VALUES ($writingId, $noteId, $addedAt);";
            cmd.Parameters.AddWithValue("$writingId", writingId.ToString());
            cmd.Parameters.AddWithValue("$noteId", danglingNoteId.ToString());
            cmd.Parameters.AddWithValue("$addedAt", DateTime.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }

        var response = await Client.GetAsync($"/api/writings/{writingId}/notes");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await response.Content.ReadFromJsonAsync<WritingSourceDto[]>();
        list.Should().ContainSingle();
        list!.Single().Id.Should().Be(noteId);
    }
}

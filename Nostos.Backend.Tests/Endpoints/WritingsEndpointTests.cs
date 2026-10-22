using System.Net;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Endpoints;

public sealed class WritingsEndpointTests : IClassFixture<LibraryEndpointFactory>
{
    private readonly LibraryEndpointFactory _factory;

    public WritingsEndpointTests(LibraryEndpointFactory factory)
    {
        _factory = factory;
    }

    private HttpClient Client => _factory.CreateClient();

    private async Task<WritingModel> SeedWritingAsync(
        string name = "Existing Draft",
        string? content = "<p>Existing manuscript</p>",
        WritingType type = WritingType.Document,
        Guid? parentId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();

        var writing = new WritingModel
        {
            Id = Guid.NewGuid(),
            Name = name,
            Type = type,
            Content = content,
            ParentId = parentId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.Writings.Add(writing);
        await db.SaveChangesAsync();
        return writing;
    }

    [Fact]
    public async Task PageSetup_IsEmptyUntilSaved_ThenReadsBackAsWritten_WithoutTouchingTheWriting()
    {
        var writing = await SeedWritingAsync();

        var empty = await Client.GetAsync($"/api/writings/{writing.Id}/page-setup");
        empty.StatusCode.Should().Be(HttpStatusCode.NoContent,
            "a writing never exported has no page setup; the dialog starts from the Studio's preferences");

        var setup = new WritingPageSetupDto("letter", 25.44, "eb-garamond", 1.5, "chicago-notes");
        var saved = await Client.PutAsJsonAsync($"/api/writings/{writing.Id}/page-setup", setup);
        saved.StatusCode.Should().Be(HttpStatusCode.OK);
        (await saved.Content.ReadFromJsonAsync<WritingPageSetupDto>())
            .Should().Be(setup with { MarginMm = 25.4 });

        var read = await Client.GetFromJsonAsync<WritingPageSetupDto>($"/api/writings/{writing.Id}/page-setup");
        read.Should().Be(setup with { MarginMm = 25.4 });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
        var stored = await db.Writings.FindAsync(writing.Id);
        stored!.Content.Should().Be(writing.Content);
        stored.UpdatedAt.Should().Be(writing.UpdatedAt, "page setup is not a change to the writing's words");
    }

    [Fact]
    public async Task PageSetup_IsSavedForAFolderToo()
    {
        var folder = await SeedWritingAsync("Thesis", null, WritingType.Folder);
        var setup = new WritingPageSetupDto("a4", 30, "newsreader", 2, "apa");

        var saved = await Client.PutAsJsonAsync($"/api/writings/{folder.Id}/page-setup", setup);

        saved.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Client.GetFromJsonAsync<WritingPageSetupDto>($"/api/writings/{folder.Id}/page-setup")).Should().Be(setup);
    }

    [Theory]
    [InlineData("tabloid", 25, "newsreader", 1.5, "apa")]
    [InlineData("a4", 1, "newsreader", 1.5, "apa")]
    [InlineData("a4", 200, "newsreader", 1.5, "apa")]
    [InlineData("a4", 25, "Comic Sans; drop", 1.5, "apa")]
    [InlineData("a4", 25, "newsreader", 9, "apa")]
    [InlineData("a4", 25, "newsreader", 1.5, "")]
    public async Task PageSetup_RejectsAnUnusableSetup_AndKeepsTheSavedOne(
        string paper, double margin, string font, double spacing, string style)
    {
        var writing = await SeedWritingAsync();
        var good = new WritingPageSetupDto("a4", 25, "newsreader", 1.5, "apa");
        (await Client.PutAsJsonAsync($"/api/writings/{writing.Id}/page-setup", good)).EnsureSuccessStatusCode();

        var response = await Client.PutAsJsonAsync(
            $"/api/writings/{writing.Id}/page-setup",
            new WritingPageSetupDto(paper, margin, font, spacing, style));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Client.GetFromJsonAsync<WritingPageSetupDto>($"/api/writings/{writing.Id}/page-setup")).Should().Be(good);
    }

    [Fact]
    public async Task PageSetup_ForAMissingWriting_IsNotFound()
    {
        var id = Guid.NewGuid();
        (await Client.GetAsync($"/api/writings/{id}/page-setup")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Client.PutAsJsonAsync($"/api/writings/{id}/page-setup", new WritingPageSetupDto("a4", 25, "newsreader", 1.5, "apa")))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Update_NameOnly_PreservesExistingContent_InResponseAndPersistedGet()
    {
        var writing = await SeedWritingAsync();

        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/writings/{writing.Id}")
        {
            Content = new StringContent(
                """{"name":"Renamed Draft"}""",
                Encoding.UTF8,
                "application/json"),
        };

        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await response.Content.ReadFromJsonAsync<WritingContentDto>();
        updated.Should().NotBeNull();
        updated!.Name.Should().Be("Renamed Draft");
        updated.Content.Should().Be("<p>Existing manuscript</p>");

        var persistedResponse = await Client.GetAsync($"/api/writings/{writing.Id}");
        persistedResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var persisted = await persistedResponse.Content.ReadFromJsonAsync<WritingContentDto>();
        persisted.Should().NotBeNull();
        persisted!.Name.Should().Be("Renamed Draft");
        persisted.Content.Should().Be("<p>Existing manuscript</p>");
    }

    [Fact]
    public async Task Update_ExplicitNullEmptyAndNonemptyContent_RetainClearingAndReplacementSemantics()
    {
        var writing = await SeedWritingAsync();

        using (var explicitNull = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/writings/{writing.Id}")
        {
            Content = new StringContent(
                """{"name":"Draft","content":null}""",
                Encoding.UTF8,
                "application/json"),
        })
        {
            var response = await Client.SendAsync(explicitNull);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var dto = await response.Content.ReadFromJsonAsync<WritingContentDto>();
            dto.Should().NotBeNull();
            dto!.Content.Should().BeEmpty();

            var persisted = await Client.GetFromJsonAsync<WritingContentDto>(
                $"/api/writings/{writing.Id}");
            persisted.Should().NotBeNull();
            persisted!.Content.Should().BeEmpty();
        }

        using (var explicitEmpty = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/writings/{writing.Id}")
        {
            Content = new StringContent(
                """{"name":"Draft","content":""}""",
                Encoding.UTF8,
                "application/json"),
        })
        {
            var response = await Client.SendAsync(explicitEmpty);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var dto = await response.Content.ReadFromJsonAsync<WritingContentDto>();
            dto.Should().NotBeNull();
            dto!.Content.Should().BeEmpty();
        }

        using (var replacement = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/writings/{writing.Id}")
        {
            Content = new StringContent(
                """{"NaMe":"Saved Draft","CoNtEnT":"<p>Replacement manuscript</p>"}""",
                Encoding.UTF8,
                "application/json"),
        })
        {
            var response = await Client.SendAsync(replacement);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var dto = await response.Content.ReadFromJsonAsync<WritingContentDto>();
            dto.Should().NotBeNull();
            dto!.Name.Should().Be("Saved Draft");
            dto.Content.Should().Be("<p>Replacement manuscript</p>");

            var persisted = await Client.GetFromJsonAsync<WritingContentDto>(
                $"/api/writings/{writing.Id}");
            persisted.Should().NotBeNull();
            persisted!.Name.Should().Be("Saved Draft");
            persisted.Content.Should().Be("<p>Replacement manuscript</p>");
        }
    }

    [Fact]
    public async Task Update_InvalidJsonOrNonstringName_Returns400_AndUnknownIdReturns404()
    {
        var writing = await SeedWritingAsync();

        using (var malformed = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/writings/{writing.Id}")
        {
            Content = new StringContent(
                """{"name":""",
                Encoding.UTF8,
                "application/json"),
        })
        {
            var response = await Client.SendAsync(malformed);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        using (var nonstring = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/writings/{writing.Id}")
        {
            Content = new StringContent(
                """{"name":42}""",
                Encoding.UTF8,
                "application/json"),
        })
        {
            var response = await Client.SendAsync(nonstring);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        var missingResponse = await Client.PutAsJsonAsync(
            $"/api/writings/{Guid.NewGuid()}",
            new UpdateWritingDto("Missing", "content"));
        missingResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Crud_CreateLocationListGetAndDelete_PreservesOrdinaryEndpointShape()
    {
        var createResponse = await Client.PostAsJsonAsync(
            "/api/writings",
            new CreateWritingDto("New Document", "Document", null));

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<WritingDto>();
        created.Should().NotBeNull();
        created!.Name.Should().Be("New Document");
        created.Type.Should().Be("Document");
        created.ParentId.Should().BeNull();
        createResponse.Headers.Location.Should().NotBeNull();
        createResponse.Headers.Location!.OriginalString.Should().Be(
            $"/api/writings/{created.Id}");

        var listResponse = await Client.GetAsync("/api/writings");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await listResponse.Content.ReadFromJsonAsync<WritingDto[]>();
        list.Should().NotBeNull();
        list.Should().Contain(item => item.Id == created.Id);

        var getResponse = await Client.GetAsync($"/api/writings/{created.Id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var fetched = await getResponse.Content.ReadFromJsonAsync<WritingContentDto>();
        fetched.Should().NotBeNull();
        fetched!.Id.Should().Be(created.Id);
        fetched.Name.Should().Be("New Document");

        var deleteResponse = await Client.DeleteAsync($"/api/writings/{created.Id}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var missingGet = await Client.GetAsync($"/api/writings/{created.Id}");
        missingGet.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var missingDelete = await Client.DeleteAsync($"/api/writings/{Guid.NewGuid()}");
        missingDelete.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Move_RejectsSelfAndDescendant_AndAllowsOrdinaryValidParent()
    {
        var root = await SeedWritingAsync(
            name: "Root",
            content: null,
            type: WritingType.Folder);
        var child = await SeedWritingAsync(
            name: "Child",
            content: null,
            type: WritingType.Folder,
            parentId: root.Id);
        var otherParent = await SeedWritingAsync(
            name: "Other Parent",
            content: null,
            type: WritingType.Folder);

        var selfResponse = await Client.PutAsJsonAsync(
            $"/api/writings/{root.Id}/move",
            new MoveWritingDto(root.Id));
        selfResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var descendantResponse = await Client.PutAsJsonAsync(
            $"/api/writings/{root.Id}/move",
            new MoveWritingDto(child.Id));
        descendantResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var validResponse = await Client.PutAsJsonAsync(
            $"/api/writings/{child.Id}/move",
            new MoveWritingDto(otherParent.Id));
        validResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var moved = await validResponse.Content.ReadFromJsonAsync<WritingDto>();
        moved.Should().NotBeNull();
        moved!.Id.Should().Be(child.Id);
        moved.ParentId.Should().Be(otherParent.Id);
    }
}

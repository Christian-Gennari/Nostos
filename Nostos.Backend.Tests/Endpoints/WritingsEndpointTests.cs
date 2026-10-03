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

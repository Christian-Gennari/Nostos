using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Endpoints;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Endpoints;

/// <summary>
/// Full-host coverage for the provider settings surface
/// <c>/api/settings/providers</c> (issue #774). Every test gets its own host and
/// real temporary SQLite file, because settings writes must not leak between
/// tests; "stores nothing" and "stores exactly this" are asserted on the file.
/// </summary>
public sealed class ProviderSettingsEndpointTests
{
    private static readonly string[] GeneralProviders =
        ["gutenberg", "standard-ebooks", "wikisource", "librivox", "litteraturbanken"];

    [Fact]
    public async Task Get_lists_all_registered_providers_with_their_effective_choices()
    {
        using var factory = new LibraryEndpointFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(ProviderSettingsEndpoints.Route);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ProviderSettingsResponseDto>();
        body!.Providers.Select(provider => provider.Id)
            .Should().BeEquivalentTo(GeneralProviders);
        body.Providers.Should().OnlyContain(provider => provider.Enabled == provider.EnabledByDefault);
        body.Providers.Should().OnlyContain(provider => !string.IsNullOrWhiteSpace(provider.Description));

        var gutenberg = body.Providers.Single(provider => provider.Id == "gutenberg");
        gutenberg.DisplayName.Should().Be("Project Gutenberg");
        gutenberg.Description.Should().Be("Public-domain ebooks in many languages.");
        gutenberg.RightsNotice.Should().Be("Public domain in the USA (Project Gutenberg)");
        gutenberg.Capabilities.Should().Contain("search");

        var litteraturbanken = body.Providers.Single(provider => provider.Id == "litteraturbanken");
        litteraturbanken.Enabled.Should().BeFalse();
        litteraturbanken.EnabledByDefault.Should().BeFalse();
    }

    [Fact]
    public async Task Put_disabled_removes_the_provider_from_the_consumer_view_and_keeps_it_in_settings()
    {
        using var factory = new LibraryEndpointFactory();
        using var client = factory.CreateClient();

        var put = await client.PutAsJsonAsync(
            $"{ProviderSettingsEndpoints.Route}/wikisource",
            new ProviderPreferenceUpdateDto(false));

        put.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await put.Content.ReadFromJsonAsync<ProviderSettingsItemDto>();
        updated!.Enabled.Should().BeFalse();
        updated.EnabledByDefault.Should().BeTrue();

        // The consumer view Add Book reads no longer offers it...
        var consumer = await client.GetFromJsonAsync<List<ProviderSummaryDto>>("/api/providers");
        consumer!.Select(provider => provider.Id).Should().NotContain("wikisource");

        // ...while the management view still lists it so it can be re-enabled.
        var settings = await client.GetFromJsonAsync<ProviderSettingsResponseDto>(
            ProviderSettingsEndpoints.Route);
        settings!.Providers.Single(provider => provider.Id == "wikisource").Enabled.Should().BeFalse();

        // Durable, not merely echoed.
        using var db = OpenDb(factory.DatabasePath);
        var stored = await db.ProviderPreferences.AsNoTracking().SingleAsync();
        stored.ProviderId.Should().Be("wikisource");
        stored.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task Put_a_body_without_enabled_is_a_typed_400_and_stores_nothing()
    {
        using var factory = new LibraryEndpointFactory();
        using var client = factory.CreateClient();

        var empty = await client.PutAsJsonAsync(
            $"{ProviderSettingsEndpoints.Route}/gutenberg",
            new { });

        empty.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ProblemTitleAsync(empty)).Should().Be("invalid_provider_preference");

        var explicitNull = await client.PutAsJsonAsync(
            $"{ProviderSettingsEndpoints.Route}/gutenberg",
            new { enabled = (bool?)null });

        explicitNull.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ProblemTitleAsync(explicitNull)).Should().Be("invalid_provider_preference");

        // The provider is untouched, and no row exists: an empty body can never
        // silently disable a source.
        var consumer = await client.GetFromJsonAsync<List<ProviderSummaryDto>>("/api/providers");
        consumer!.Select(provider => provider.Id).Should().Contain("gutenberg");

        using var db = OpenDb(factory.DatabasePath);
        (await db.ProviderPreferences.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Put_an_unregistered_id_is_provider_unknown_and_stores_nothing()
    {
        using var factory = new LibraryEndpointFactory();
        using var client = factory.CreateClient();

        var put = await client.PutAsJsonAsync(
            $"{ProviderSettingsEndpoints.Route}/not-registered",
            new ProviderPreferenceUpdateDto(true));

        put.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ProblemTitleAsync(put)).Should().Be("provider_unknown");

        using var db = OpenDb(factory.DatabasePath);
        (await db.ProviderPreferences.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Put_is_idempotent_and_updates_the_one_row()
    {
        using var factory = new LibraryEndpointFactory();
        using var client = factory.CreateClient();

        var first = await client.PutAsJsonAsync(
            $"{ProviderSettingsEndpoints.Route}/librivox",
            new ProviderPreferenceUpdateDto(false));
        var second = await client.PutAsJsonAsync(
            $"{ProviderSettingsEndpoints.Route}/librivox",
            new ProviderPreferenceUpdateDto(false));

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await second.Content.ReadFromJsonAsync<ProviderSettingsItemDto>())!
            .Enabled.Should().BeFalse();

        using var db = OpenDb(factory.DatabasePath);
        (await db.ProviderPreferences.CountAsync()).Should().Be(1);

        // Re-enabling updates the same row rather than adding a second.
        var reenable = await client.PutAsJsonAsync(
            $"{ProviderSettingsEndpoints.Route}/librivox",
            new ProviderPreferenceUpdateDto(true));
        reenable.StatusCode.Should().Be(HttpStatusCode.OK);
        (await db.ProviderPreferences.CountAsync()).Should().Be(1);
        (await db.ProviderPreferences.AsNoTracking().SingleAsync()).Enabled.Should().BeTrue();
    }

    private static NostosDbContext OpenDb(string path) =>
        new(new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options);

    private static async Task<string?> ProblemTitleAsync(HttpResponseMessage response)
    {
        var document = await response.Content.ReadFromJsonAsync<JsonElement>();
        return document.GetProperty("title").GetString();
    }
}

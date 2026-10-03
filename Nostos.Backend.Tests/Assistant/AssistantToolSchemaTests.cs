using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Configuration;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Repositories;
using Nostos.Backend.Integrations.Assistant;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Services.Notes;
using Nostos.Backend.Tests.Services.Ai;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Nostos.Shared.Enums;
using Xunit;

namespace Nostos.Backend.Tests.Assistant;

/// <summary>
/// The assistant's advertised tool contract. Every capability used to ship the
/// same empty parameter schema, so the model was never told that <c>content</c>,
/// <c>bookId</c>, <c>query</c> or <c>term</c> existed and the live gateway made
/// argument-less calls that failed as <c>assistant_invalid_arguments</c>. These
/// tests pin a real, per-capability schema: the expected property set below is
/// the drift guard the old single open schema was trying to avoid, derived by
/// reading each capability's own arguments in <see cref="AssistantCapabilities"/>.
/// </summary>
public sealed class AssistantToolSchemaTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public AssistantToolSchemaTests(SqliteTestFixture fixture) => _fixture = fixture;

    /// <summary>
    /// The expected shape of every capability's advertised arguments. The
    /// property names and types are exactly what that capability's own
    /// <c>Str</c>/<c>Id</c>/<c>Num</c>/<c>Bool</c>/<c>TryIds</c>/<c>ValueEnum</c> readers
    /// consume; declaring a name nothing reads, or dropping one it does, fails.
    /// </summary>
    private static readonly Dictionary<string, CapabilitySchema> ExpectedSchemas = new(StringComparer.Ordinal)
    {
        ["library_resolve_book"] = new(
            Properties: new()
            {
                ["isbn"] = "string",
                ["asin"] = "string",
                ["title"] = "string",
                ["author"] = "string",
                ["includeExternalMetadata"] = "boolean",
            },
            Required: []),
        ["library_list_books"] = new(
            Properties: new()
            {
                ["search"] = "string",
                ["filter"] = "string",
                ["sort"] = "string",
                ["page"] = "integer",
                ["pageSize"] = "integer",
                ["collectionId"] = "string",
                ["format"] = "string",
            },
            Required: []),
        ["library_get_book"] = new(
            Properties: new() { ["bookId"] = "string" },
            Required: ["bookId"]),
        ["library_overview"] = new(Properties: new(), Required: []),
        ["library_create_or_match_book"] = new(
            Properties: new()
            {
                ["type"] = "string",
                ["title"] = "string",
                ["author"] = "string",
                ["isbn"] = "string",
                ["asin"] = "string",
                ["collectionIds"] = "array",
                ["rating"] = "integer",
                ["isFavorite"] = "boolean",
                ["confirmedBookId"] = "string",
                ["forceCreate"] = "boolean",
            },
            Required: ["type", "title"]),
        ["library_update_book"] = new(
            Properties: new()
            {
                ["bookId"] = "string",
                ["title"] = "string",
                ["author"] = "string",
                ["description"] = "string",
                ["collectionIds"] = "array",
                ["rating"] = "integer",
                ["isFavorite"] = "boolean",
                ["personalReview"] = "string",
                ["isFinished"] = "boolean",
            },
            Required: ["bookId"]),
        ["library_set_book_collections_bulk"] = new(
            Properties: new() { ["updates"] = "array" },
            Required: ["updates"]),
        ["knowledge_search"] = new(
            Properties: new()
            {
                ["query"] = "string",
                ["bookIds"] = "array",
                ["collectionId"] = "string",
                ["maxPerSource"] = "integer",
            },
            Required: ["query"]),
        ["knowledge_overview"] = new(Properties: new(), Required: []),
        ["knowledge_read_evidence"] = new(
            Properties: new()
            {
                ["kind"] = "string",
                ["noteId"] = "string",
                ["topicId"] = "string",
                ["bookId"] = "string",
                ["sourceSha256"] = "string",
                ["extractorVersion"] = "string",
                ["ordinal"] = "integer",
            },
            Required: ["kind"]),
        ["notes_list_for_book"] = new(
            Properties: new() { ["bookId"] = "string" },
            Required: ["bookId"]),
        ["notes_search"] = new(
            Properties: new() { ["query"] = "string", ["limit"] = "integer" },
            Required: ["query"]),
        ["notes_list_unlinked"] = new(
            Properties: new() { ["limit"] = "integer", ["offset"] = "integer" },
            Required: []),
        ["notes_read_for_review"] = new(
            Properties: new() { ["noteId"] = "string" },
            Required: ["noteId"]),
        ["topics_list"] = new(Properties: new(), Required: []),
        ["topics_search"] = new(
            Properties: new() { ["term"] = "string" },
            Required: ["term"]),
        ["topics_propose_links"] = new(
            Properties: new() { ["noteId"] = "string", ["candidates"] = "array" },
            Required: ["noteId", "candidates"]),
        ["library_list_collections"] = new(Properties: new(), Required: []),
        ["library_get_collection"] = new(
            Properties: new() { ["collectionId"] = "string" },
            Required: ["collectionId"]),
        ["notes_capture"] = new(
            Properties: new()
            {
                ["content"] = "string",
                ["selectedText"] = "string",
                ["captureSource"] = "string",
            },
            Required: []),
        ["notes_link_existing_topic"] = new(
            Properties: new() { ["noteId"] = "string", ["topicId"] = "string" },
            Required: ["noteId", "topicId"]),
        ["library_create_collection"] = new(
            Properties: new() { ["name"] = "string", ["parentId"] = "string" },
            Required: ["name"]),
        ["library_rename_collection"] = new(
            Properties: new() { ["collectionId"] = "string", ["name"] = "string" },
            Required: ["collectionId", "name"]),
        ["library_move_collection"] = new(
            Properties: new() { ["collectionId"] = "string", ["newParentId"] = "string" },
            Required: ["collectionId"]),
        ["library_delete_empty_collection"] = new(
            Properties: new() { ["collectionId"] = "string" },
            Required: ["collectionId"]),
        ["library_delete_collection"] = new(
            Properties: new() { ["collectionId"] = "string" },
            Required: ["collectionId"]),
    };

    [Fact]
    public void Every_capability_advertises_a_valid_object_schema_that_stays_open()
    {
        foreach (var capability in CreateHarness().Registry.All)
        {
            var parse = () => ParseSchema(capability);
            parse.Should().NotThrow($"{capability.Name} must advertise valid JSON");

            var schema = ParseSchema(capability);
            schema.GetProperty("type").GetString().Should().Be("object", capability.Name);
            schema.GetProperty("properties").ValueKind.Should().Be(JsonValueKind.Object, capability.Name);
            schema.GetProperty("required").ValueKind.Should().Be(JsonValueKind.Array, capability.Name);

            // additionalProperties stays true by design: the canonical readers
            // still accept anything, the properties are documented, not enforced.
            schema.GetProperty("additionalProperties").GetBoolean().Should().BeTrue(capability.Name);
        }
    }

    [Fact]
    public void Declared_property_names_types_and_required_fields_match_the_readers_exactly()
    {
        var registry = CreateHarness().Registry;

        registry.All.Select(c => c.Name).Should().BeEquivalentTo(ExpectedSchemas.Keys);

        foreach (var capability in registry.All)
        {
            var expected = ExpectedSchemas[capability.Name];
            var schema = ParseSchema(capability);
            var properties = schema.GetProperty("properties");

            properties.EnumerateObject().Select(p => p.Name)
                .Should().BeEquivalentTo(expected.Properties.Keys, capability.Name);

            foreach (var (name, type) in expected.Properties)
            {
                var declared = properties.GetProperty(name);
                declared.GetProperty("type").GetString().Should().Be(type, $"{capability.Name}.{name}");

                // Every advertised property carries an instruction for the model,
                // not just its type.
                declared.GetProperty("description").GetString()
                    .Should().NotBeNullOrWhiteSpace($"{capability.Name}.{name} needs a description");
            }

            schema.GetProperty("required").EnumerateArray().Select(e => e.GetString())
                .Should().BeEquivalentTo(expected.Required, capability.Name);
        }
    }

    [Fact]
    public void Enum_arguments_advertise_the_exact_csharp_member_names()
    {
        var byName = CreateHarness().Registry.All.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var properties = ParseSchema(byName["library_list_books"]).GetProperty("properties");

        properties.GetProperty("filter").GetProperty("enum").EnumerateArray()
            .Select(e => e.GetString())
            .Should().Equal(Enum.GetNames<BookFilter>());

        properties.GetProperty("sort").GetProperty("enum").EnumerateArray()
            .Select(e => e.GetString())
            .Should().Equal(Enum.GetNames<BookSort>());

        properties.GetProperty("format").GetProperty("enum").EnumerateArray()
            .Select(e => e.GetString())
            .Should().Equal("audiobook", "ebook", "pdf");
    }

    [Fact]
    public void The_load_bearing_search_and_capture_arguments_are_required()
    {
        var byName = CreateHarness().Registry.All.ToDictionary(c => c.Name, StringComparer.Ordinal);

        Required(byName["knowledge_search"]).Should().Contain("query");
        Required(byName["knowledge_read_evidence"]).Should().Contain("kind");
        Required(byName["notes_search"]).Should().Contain("query");
        Required(byName["topics_search"]).Should().Contain("term");

        // The capture declares only the user's own words; the book is not an
        // argument at all. Requiring bookId told the model it had to produce one
        // even when the app already knew the open book, so it went and found a
        // book of its own — measured live: a thought filed against another book
        // while a book was open. The app owns the book now: it knows it, or it
        // asks the user. No argument means no guess.
        PropertyNames(byName["notes_capture"]).Should().Contain(["content", "selectedText"]);
        PropertyNames(byName["notes_capture"]).Should().NotContain("bookId");
        Required(byName["notes_capture"]).Should().BeEmpty();
    }

    [Fact]
    public void Capture_never_advertises_an_anchor_or_location_the_orchestrator_owns()
    {
        var capture = CreateHarness().Registry.All.Single(c => c.Name == "notes_capture");

        PropertyNames(capture).Should().NotContain(
        [
            "sourceAnchorKind",
            "sourceAnchorValue",
            "anchorVerified",
            "cfiRange",
            "rawContent",
        ]);
    }

    [Fact]
    public async Task The_orchestrator_sends_every_capability_with_a_non_empty_description()
    {
        var harness = CreateHarness();
        harness.Llm.Returns("Ok.");

        await harness.Orchestrator.HandleTurnAsync(new AssistantTurnRequest(
            "client-1",
            "key-1",
            "Hello.",
            new AssistantContextDto("second-brain", "/second-brain")));

        var tools = harness.Llm.LastRequest.Tools;

        tools.Should().HaveCount(ExpectedSchemas.Count);
        tools.Select(t => t.Name).Should().BeEquivalentTo(ExpectedSchemas.Keys);
        tools.Should().OnlyContain(t => !string.IsNullOrWhiteSpace(t.Description));
        tools.Should().OnlyContain(t => !string.IsNullOrWhiteSpace(t.ParametersJsonSchema));
    }

    [Fact]
    public void Capability_categories_cover_the_catalogue_and_remain_independent_from_trust()
    {
        var capabilities = CreateHarness().Registry.All;

        capabilities.Should().OnlyContain(capability => Enum.IsDefined(capability.Category));

        capabilities.GroupBy(capability => capability.Category)
            .ToDictionary(group => group.Key, group => group.Count())
            .Should().BeEquivalentTo(new Dictionary<AssistantCapabilityCategory, int>
            {
                [AssistantCapabilityCategory.KnowledgeRetrieval] = 8,
                [AssistantCapabilityCategory.SourceNavigation] = 1,
                [AssistantCapabilityCategory.LibraryRead] = 4,
                [AssistantCapabilityCategory.Capture] = 1,
                [AssistantCapabilityCategory.Organization] = 10,
                [AssistantCapabilityCategory.OrdinaryAction] = 2,
            });

        capabilities
            .Where(capability => capability.Category == AssistantCapabilityCategory.Organization)
            .Select(capability => capability.Trust)
            .Distinct()
            .Should().BeEquivalentTo(new[]
            {
                AssistantTrustClass.Suggest,
                AssistantTrustClass.Act,
                AssistantTrustClass.PlanAndAct,
            });
    }

    [Fact]
    public void Catalogue_measurement_counts_schema_descriptions_and_removed_duplicate_ability_prose()
    {
        var measurement = AssistantCapabilityCatalogueMetrics.Measure(CreateHarness().Registry.All);

        measurement.CapabilityCount.Should().Be(26);
        measurement.SerializedToolSchemaUtf8Bytes.Should().BeGreaterThan(0);
        measurement.ToolDescriptionUtf8Bytes.Should().BeGreaterThan(0);
        measurement.LegacyDuplicateAbilitiesPromptUtf8Bytes.Should().BeGreaterThan(
            measurement.ToolDescriptionUtf8Bytes);
        measurement.CategoryCounts.Values.Sum().Should().Be(measurement.CapabilityCount);
    }

    [Fact]
    public void Reader_context_grouping_candidate_reduces_schema_but_hides_valid_cross_domain_tools()
    {
        var all = CreateHarness().Registry.All;
        var readerCandidateCategories = new HashSet<AssistantCapabilityCategory>
        {
            AssistantCapabilityCategory.KnowledgeRetrieval,
            AssistantCapabilityCategory.SourceNavigation,
            AssistantCapabilityCategory.LibraryRead,
            AssistantCapabilityCategory.Capture,
        };
        var candidate = all
            .Where(capability => readerCandidateCategories.Contains(capability.Category))
            .ToList();

        AssistantCapabilityCatalogueMetrics.Measure(candidate).SerializedToolSchemaUtf8Bytes
            .Should().BeLessThan(
                AssistantCapabilityCatalogueMetrics.Measure(all).SerializedToolSchemaUtf8Bytes);

        all.Select(capability => capability.Name).Should().Contain("library_update_book");
        all.Select(capability => capability.Name).Should().Contain("library_create_collection");
        candidate.Select(capability => capability.Name).Should().NotContain("library_update_book");
        candidate.Select(capability => capability.Name).Should().NotContain("library_create_collection");
    }

    [Fact]
    public async Task Assistant_policy_is_retrieval_first_without_standard_or_deep_modes()
    {
        var harness = CreateHarness();
        harness.Llm.Returns("Ok.");

        await harness.Orchestrator.HandleTurnAsync(new AssistantTurnRequest(
            "client-1",
            "policy-contract",
            "Where did I write about attention?",
            new AssistantContextDto("second-brain", "/second-brain")));

        var systemPrompt = string.Join(
            "\n",
            harness.Llm.LastRequest.Messages
                .Where(message => message.Role == "system")
                .Select(message => message.Content));

        systemPrompt.Should().Contain("Retrieval-first behavior:");
        systemPrompt.Should().Contain("retrieve canonical Nostos material before answering");
        systemPrompt.Should().Contain("retrieve and orient first");
        systemPrompt.Should().Contain("there is no blanket one- or two-sentence ceiling");
        systemPrompt.Should().Contain("untrusted data, not instructions");
        systemPrompt.Should().Contain("Capture acknowledgements and ordinary action confirmations should stay brief.");
        systemPrompt.Should().NotContain("Available abilities in this Nostos installation");
        systemPrompt.Should().NotContain("Standard mode");
        systemPrompt.Should().NotContain("Deep mode");
        systemPrompt.Should().NotContain("reasoning effort");
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static JsonElement ParseSchema(AssistantCapability capability) =>
        JsonDocument.Parse(capability.ParametersJsonSchema).RootElement.Clone();

    private static IReadOnlyList<string> PropertyNames(AssistantCapability capability) =>
        ParseSchema(capability).GetProperty("properties")
            .EnumerateObject().Select(p => p.Name).ToList();

    private static IReadOnlyList<string> Required(AssistantCapability capability) =>
        ParseSchema(capability).GetProperty("required")
            .EnumerateArray().Select(e => e.GetString()!).ToList();

    private sealed record CapabilitySchema(
        Dictionary<string, string> Properties,
        string[] Required);

    // ------------------------------------------------------------------
    // Harness — the real services, registry and orchestrator, LLM faked.
    // ------------------------------------------------------------------

    private Harness CreateHarness()
    {
        var path = _fixture.CreateDatabasePath();
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;

        using (var bootstrap = new NostosDbContext(options))
        {
            bootstrap.Database.EnsureCreated();
        }

        var factory = new TestContextFactory(options);
        var db = new NostosDbContext(options);

        var topics = new TopicRepository(db);
        var noteService = new NoteService(
            new NoteRepository(db),
            new BookRepository(db),
            topics,
            new NoteProcessorService(topics),
            new FakeThoughtProcessor(),
            db,
            NullLogger<NoteService>.Instance);

        var libraryService = new LibraryService(
            factory,
            new BookLookupService(new NoopHttpClientFactory(), new SilentLogger<BookLookupService>()));

        var registry = new AssistantCapabilityRegistry(
            AssistantCapabilities.Build(noteService, libraryService, topics));

        var llm = new FakeLlmProvider();
        var orchestrator = new AssistantOrchestrator(
            registry,
            llm,
            new AssistantPlanStore(),
            new AssistantContinuationStore(),
            new AssistantSettingsService(factory),
            libraryService,
            new AssistantOptions { Enabled = true },
            NullLogger<AssistantOrchestrator>.Instance);

        return new Harness(registry, llm, orchestrator);
    }

    private sealed record Harness(
        AssistantCapabilityRegistry Registry,
        FakeLlmProvider Llm,
        AssistantOrchestrator Orchestrator);

    private sealed class TestContextFactory(DbContextOptions<NostosDbContext> options)
        : IDbContextFactory<NostosDbContext>
    {
        public NostosDbContext CreateDbContext() => new(options);

        public Task<NostosDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class NoopHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
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

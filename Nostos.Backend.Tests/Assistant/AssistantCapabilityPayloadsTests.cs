using System.Text.Json;
using FluentAssertions;
using Nostos.Backend.Integrations.Assistant;
using Nostos.Product.BookText;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Assistant;

/// <summary>
/// The typed view of the capability result shapes (issue #1598). These are the
/// readers that replaced the string-keyed <see cref="JsonElement"/> probing in
/// the orchestration loop and the retrieval turn state, so their tolerance is
/// part of the contract: a numeric enum (what a capability writes) and its name
/// both read, and a payload of another shape is "not this shape" rather than a
/// throw.
/// </summary>
public sealed class AssistantCapabilityPayloadsTests
{
    [Fact]
    public void A_capture_envelope_yields_the_note_and_its_processing_mode()
    {
        var data = Payload(
            """
            {"success":true,"errorCode":null,"errorMessage":null,"value":{"id":"0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0","bookId":"11111111-2222-3333-4444-555555555555","content":"A thought","cfiRange":null,"selectedText":null,"createdAt":"2026-11-01T10:00:00Z","bookTitle":"Book","processingMode":"clarify"}}
            """);

        AssistantCapabilityPayloads.ReadCapturedNoteId(data)
            .Should().Be("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0");
        AssistantCapabilityPayloads.IsRewritten(data).Should().BeTrue();
    }

    [Theory]
    [InlineData("verbatim", false)]
    [InlineData(null, false)]
    [InlineData("light_polish", true)]
    public void A_capture_is_a_rewrite_only_when_the_stored_note_says_so(string? mode, bool rewritten)
    {
        var modeJson = mode is null ? "null" : $"\"{mode}\"";
        var data = Payload(
            "{\"success\":true,\"value\":{\"id\":\"0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0\","
            + "\"bookId\":\"11111111-2222-3333-4444-555555555555\",\"content\":\"A thought\","
            + "\"cfiRange\":null,\"selectedText\":null,\"createdAt\":\"2026-11-01T10:00:00Z\","
            + "\"bookTitle\":\"Book\",\"processingMode\":" + modeJson + "}}");

        AssistantCapabilityPayloads.IsRewritten(data).Should().Be(rewritten);
    }

    [Fact]
    public void A_book_text_status_reads_whether_it_is_written_as_a_number_or_a_name()
    {
        var numeric = Payload(
            """
            {"passages":[],"states":[{"bookId":"11111111-2222-3333-4444-555555555555","status":2,"sourceFileName":"a.epub","format":0,"sourceSha256":null,"extractorVersion":null,"errorCode":null,"errorMessage":null,"attempts":1,"chunkCount":3,"characterCount":100,"updatedAtUtc":"2026-11-01T10:00:00Z"}],"evidenceAvailable":false}
            """);
        var named = Payload(
            """
            {"passages":[],"states":[{"bookId":"11111111-2222-3333-4444-555555555555","status":"Ready","sourceFileName":"a.epub","format":0,"sourceSha256":null,"extractorVersion":null,"errorCode":null,"errorMessage":null,"attempts":1,"chunkCount":3,"characterCount":100,"updatedAtUtc":"2026-11-01T10:00:00Z"}],"evidenceAvailable":false}
            """);

        AssistantCapabilityPayloads.ReadBookTextSearch(numeric)!.States.Single().Status
            .Should().Be(BookTextIngestionStatus.Ready);
        AssistantCapabilityPayloads.ReadBookTextSearch(named)!.States.Single().Status
            .Should().Be(BookTextIngestionStatus.Ready);
    }

    [Fact]
    public void A_payload_of_another_shape_is_not_this_shape()
    {
        var wrongStates = Payload("""{"passages":[],"states":"not an array","evidenceAvailable":false}""");
        var notAnObject = Payload("\"a string\"");

        AssistantCapabilityPayloads.ReadBookTextSearch(wrongStates).Should().BeNull();
        AssistantCapabilityPayloads.ReadKnowledgeSearch(notAnObject).Should().BeNull();
        AssistantCapabilityPayloads.ReadCapturedNoteId(wrongStates).Should().BeNull();
        AssistantCapabilityPayloads.ReadQuoteLookupStatus(wrongStates).Should().BeNull();
        AssistantCapabilityPayloads.ReadLibraryBook(wrongStates).Should().BeNull();
        AssistantCapabilityPayloads.ReadResolvedBook(notAnObject).Should().BeNull();
        AssistantCapabilityPayloads.ReadTopics(wrongStates).Should().BeNull();
    }

    [Fact]
    public void An_absent_payload_is_not_this_shape()
    {
        AssistantCapabilityPayloads.ReadBookTextSearch(null).Should().BeNull();
        AssistantCapabilityPayloads.ReadCapturedNoteId(null).Should().BeNull();
        AssistantCapabilityPayloads.IsRewritten(null).Should().BeFalse();
    }

    [Fact]
    public void The_mcp_library_envelope_and_resolution_are_the_typed_view_of_these_reads()
    {
        var envelope = Payload(
            """
            {"reply":"Here it is.","data":{"id":"11111111-2222-3333-4444-555555555555","title":"The Magic Mountain","type":"physical"},"stateVersion":"v1","duplicate":false}
            """);
        AssistantCapabilityPayloads.ReadLibraryBook(envelope)!.Id
            .Should().Be(Guid.Parse("11111111-2222-3333-4444-555555555555"));
        AssistantCapabilityPayloads.ReadLibraryBook(envelope)!.Title.Should().Be("The Magic Mountain");

        var resolved = Payload(
            """
            {"resolution":0,"matchedBook":{"id":"11111111-2222-3333-4444-555555555555","title":"The Magic Mountain","type":"physical"},"candidates":null,"prefill":null,"lookupError":null}
            """);
        AssistantCapabilityPayloads.ReadResolvedBook(resolved)!.MatchedBook!.Title
            .Should().Be("The Magic Mountain");

        var candidates = Payload(
            """
            {"resolution":1,"matchedBook":null,"candidates":[{"bookId":"11111111-2222-3333-4444-555555555555","title":"The Magic Mountain","author":"Mann","isbn":null}],"prefill":null,"lookupError":null}
            """);
        AssistantCapabilityPayloads.ReadResolvedBook(candidates)!.Candidates!.Single().Title
            .Should().Be("The Magic Mountain");
    }

    [Fact]
    public void Topic_rows_and_the_quote_status_read_their_typed_fields()
    {
        var topics = Payload(
            """
            [{"id":"11111111-2222-3333-4444-555555555555","name":"Homecoming","usageCount":2},{"id":"00000000-0000-0000-0000-000000000000","name":"No id","usageCount":0}]
            """);
        AssistantCapabilityPayloads.ReadTopicIds(topics)
            .Should().Equal(Guid.Parse("11111111-2222-3333-4444-555555555555"));

        AssistantCapabilityPayloads.ReadQuoteLookupStatus(Payload("""{"status":"choose","message":"Pick one."}"""))
            .Should().Be("choose");
    }

    [Fact]
    public void The_note_id_argument_reads_only_a_parseable_id()
    {
        AssistantCapabilityPayloads.ReadNoteId(Payload("""{"noteId":"11111111-2222-3333-4444-555555555555"}"""))
            .Should().Be(Guid.Parse("11111111-2222-3333-4444-555555555555"));
        AssistantCapabilityPayloads.ReadNoteId(Payload("""{"noteId":"not-a-guid"}""")).Should().BeNull();
        AssistantCapabilityPayloads.ReadNoteId(Payload("{}")).Should().BeNull();
    }

    private static JsonElement Payload(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

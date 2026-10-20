using System.Text.Json;
using FluentAssertions;
using Nostos.Backend.Integrations.Assistant;
using Nostos.Backend.Services.Knowledge;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Assistant;

public sealed class AssistantCitationsTests
{
    [Theory]
    [InlineData("Plain reply.", 0, "Plain reply.")]
    [InlineData("Claim [^1].", 1, "Claim [^1].")]
    [InlineData("Claim [^2].", 1, "Claim.")]
    [InlineData("Claim [^0] and [^1].", 1, "Claim and [^1].")]
    [InlineData("Twice [^1][^1] here.", 1, "Twice [^1] here.")]
    [InlineData("Two [^1][^2].", 2, "Two [^1][^2].")]
    [InlineData("Claim [^1].\n\n[^1]: Meditations, p. 4", 1, "Claim [^1].")]
    [InlineData("No evidence [^1].", 0, "No evidence.")]
    public void Normalize_keeps_only_markers_that_point_at_this_turns_evidence(string reply, int evidence, string expected) =>
        AssistantCitations.Normalize(reply, evidence).Should().Be(expected);

    [Fact]
    public void Annotate_numbers_each_item_by_its_place_in_the_turns_evidence()
    {
        var first = Note(Guid.NewGuid());
        var second = Note(Guid.NewGuid());
        var data = JsonSerializer.SerializeToElement(new object[]
        {
            new { id = second.Handle.NoteId, content = "b" },
            new { id = first.Handle.NoteId, content = "a" },
        });

        var annotated = AssistantCitations.Annotate("notes_search", data, [second, first], [first, second]);

        annotated.Should().NotBeNull();
        annotated!.Value[0].GetProperty("cite").GetInt32().Should().Be(2);
        annotated.Value[1].GetProperty("cite").GetInt32().Should().Be(1);
        annotated.Value[0].GetProperty("content").GetString().Should().Be("b");
    }

    [Fact]
    public void Annotate_leaves_a_result_alone_when_its_items_do_not_match_the_evidence()
    {
        var note = Note(Guid.NewGuid());
        var data = JsonSerializer.SerializeToElement(new object[] { new { id = 1 }, new { id = 2 } });

        AssistantCitations.Annotate("notes_search", data, [note], [note]).Should().BeNull();
        AssistantCitations.Annotate("topics_list", data, [note, note], [note]).Should().BeNull();
    }

    [Fact]
    public void Annotate_skips_evidence_past_the_turns_cap()
    {
        var kept = Note(Guid.NewGuid());
        var dropped = Note(Guid.NewGuid());
        var data = JsonSerializer.SerializeToElement(new object[] { new { id = 1 }, new { id = 2 } });

        var annotated = AssistantCitations.Annotate("notes_search", data, [kept, dropped], [kept]);

        annotated!.Value[0].GetProperty("cite").GetInt32().Should().Be(1);
        annotated.Value[1].TryGetProperty("cite", out _).Should().BeFalse();
    }

    private static AssistantEvidenceReferenceDto Note(Guid id) =>
        new(new AssistantEvidenceHandleDto(KnowledgeEvidenceKinds.Note, NoteId: id), "Note");
}

using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Nostos.Backend.Services.Notes.Imports;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Services.Notes;

/// <summary>
/// The canary's schema check (issue #1525): it passes on the committed
/// recording, fails when a field the mapper reads disappears, and fails when
/// one changes type. It must never carry a title or a highlight into its
/// message.
/// </summary>
public sealed class ReadwiseExportSchemaTests
{
    [Fact]
    public void The_recorded_page_passes()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(ImportFixtures.ReadwiseApiExport));

        var issues = ReadwiseExportSchema.Check(document.RootElement);

        issues.Should().BeEmpty();
        ReadwiseExportSchema.Describe(issues).Should().BeEmpty();
    }

    [Fact]
    public void Removing_a_field_the_mapper_reads_fails_with_its_path()
    {
        using var document = Changed(root =>
            root["results"]!.AsArray()[0]!.AsObject()["highlights"]!.AsArray()[0]!.AsObject()
                .Remove("location_type"));

        var issues = ReadwiseExportSchema.Check(document.RootElement);

        issues.Should().ContainSingle();
        issues[0].Path.Should().Be("$.results[0].highlights[0].location_type");
        issues[0].Actual.Should().Be("missing");
        ReadwiseExportSchema.Describe(issues).Should().Contain("expected string or null");
    }

    [Fact]
    public void Changing_a_field_type_fails_with_both_types()
    {
        using var document = Changed(root => root["count"] = "many");

        var issues = ReadwiseExportSchema.Check(document.RootElement);

        issues.Should().ContainSingle();
        issues[0].Path.Should().Be("$.count");
        issues[0].Expected.Should().Be("number");
        issues[0].Actual.Should().Be("string");
    }

    [Fact]
    public void A_missing_root_array_is_reported_once_and_does_not_crash()
    {
        using var document = JsonDocument.Parse("""{"count":1,"nextPageCursor":null}""");

        var issues = ReadwiseExportSchema.Check(document.RootElement);

        issues.Should().ContainSingle();
        issues[0].Path.Should().Be("$.results");
        issues[0].Actual.Should().Be("missing");
    }

    [Fact]
    public void A_documented_string_cursor_and_the_live_number_cursor_are_both_accepted()
    {
        foreach (var cursor in new[] { "\"synthetic-cursor\"", "9000000001", "null" })
        {
            using var document = JsonDocument.Parse(
                $$"""{"count":0,"nextPageCursor":{{cursor}},"results":[]}""");

            ReadwiseExportSchema.Check(document.RootElement).Should().BeEmpty();
        }
    }

    [Fact]
    public void The_failure_message_never_carries_recording_content()
    {
        using var document = Changed(root =>
            root["results"]!.AsArray()[0]!.AsObject().Remove("title"));

        var exception = Assert.Throws<ReadwiseFormatException>(() =>
            ReadwiseApiMapper.Map(document.RootElement));

        exception.Message.Should().Contain("$.results[0].title");
        exception.Message.Should().NotContain("Synthetic Title");
        exception.Message.Should().NotContain("Synthetic Author");
        exception.Message.Should().NotContain("Synthetic highlight");
    }

    private static JsonDocument Changed(Action<JsonObject> change)
    {
        var root = JsonNode.Parse(File.ReadAllText(ImportFixtures.ReadwiseApiExport))!.AsObject();
        change(root);
        return JsonDocument.Parse(root.ToJsonString());
    }
}

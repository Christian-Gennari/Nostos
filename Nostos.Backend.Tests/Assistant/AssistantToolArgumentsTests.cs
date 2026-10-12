using System.Text.Json;
using FluentAssertions;
using Nostos.Backend.Integrations.Assistant;
using Xunit;

namespace Nostos.Backend.Tests.Assistant;

public sealed class AssistantToolArgumentsTests
{
    [Fact]
    public void Reads_a_json_object()
    {
        var obj = AssistantToolArguments.ParseObject("{\"query\":\"stoicism\",\"limit\":3}");

        obj["query"]!.GetValue<string>().Should().Be("stoicism");
        obj["limit"]!.GetValue<int>().Should().Be(3);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{\"query\":")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    public void Reads_anything_that_is_not_an_object_as_empty(string? json)
    {
        AssistantToolArguments.ParseObject(json).Should().BeEmpty();
    }

    [Fact]
    public void Element_form_keeps_the_properties()
    {
        var element = AssistantToolArguments.ParseElement("{\"bookId\":\"b1\"}");

        element.ValueKind.Should().Be(JsonValueKind.Object);
        element.GetProperty("bookId").GetString().Should().Be("b1");
    }

    [Fact]
    public void Element_form_of_bad_input_is_an_empty_object()
    {
        var element = AssistantToolArguments.ParseElement("not json");

        element.ValueKind.Should().Be(JsonValueKind.Object);
        element.EnumerateObject().Should().BeEmpty();
    }
}

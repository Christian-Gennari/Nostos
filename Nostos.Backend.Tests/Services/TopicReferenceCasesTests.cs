using System.Text.Json;
using FluentAssertions;
using Nostos.Backend.Services;
using Xunit;

namespace Nostos.Backend.Tests.Services;

/// <summary>
/// The cases in Nostos.Shared/Fixtures/topic-references.json. The frontend's
/// note composer runs the same file (topic-text.spec.ts), so the text a pill
/// writes on the server and the text the composer reads back cannot drift.
/// </summary>
public sealed class TopicReferenceCasesTests
{
    private static readonly JsonElement Cases = Load();

    public static TheoryData<int> Compose() => Indexes("compose");
    public static TheoryData<int> Remove() => Indexes("remove");

    [Theory]
    [MemberData(nameof(Compose))]
    public void AppendAll_writes_pill_topics_as_the_composer_reads_them(int index)
    {
        var item = Cases.GetProperty("compose")[index];
        var topics = item.GetProperty("topics").EnumerateArray().Select(t => t.GetString()!).ToList();

        TopicReferences.AppendAll(item.GetProperty("words").GetString(), topics)
            .Should().Be(item.GetProperty("content").GetString());
    }

    [Theory]
    [MemberData(nameof(Remove))]
    public void Remove_matches_the_composer(int index)
    {
        var item = Cases.GetProperty("remove")[index];

        TopicReferences.Remove(item.GetProperty("content").GetString(), item.GetProperty("name").GetString()!)
            .Should().Be(item.GetProperty("expected").GetString());
    }

    [Fact]
    public void Every_split_case_names_topics_the_server_accepts()
    {
        foreach (var item in Cases.GetProperty("split").EnumerateArray())
            foreach (var topic in item.GetProperty("topics").EnumerateArray())
                TopicReferences.IsValidName(topic.GetString()).Should().BeTrue();
    }

    private static TheoryData<int> Indexes(string group)
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < Cases.GetProperty(group).GetArrayLength(); i++) data.Add(i);
        return data;
    }

    private static JsonElement Load()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "Nostos.Shared")))
            directory = directory.Parent;
        if (directory is null) throw new InvalidOperationException("The tests run outside the repository.");
        var path = Path.Combine(directory.FullName, "Nostos.Shared", "Fixtures", "topic-references.json");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    }
}

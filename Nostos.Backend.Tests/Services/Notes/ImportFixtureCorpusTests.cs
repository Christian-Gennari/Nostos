using System.Text.Json;
using FluentAssertions;
using Nostos.Backend.Services.Notes.Imports;
using Nostos.Backend.Tests.Support;
using Xunit;
using Xunit.Sdk;

namespace Nostos.Backend.Tests.Services.Notes;

/// <summary>
/// The versioned highlight-import corpus (issue #1524): every
/// <c>Fixtures/&lt;source&gt;/&lt;version&gt;/manifest.json</c> names one real
/// export and the counts it must produce. Adding a source version is a file
/// and a manifest; no test code changes.
/// </summary>
public sealed class ImportFixtureCorpusTests
{
    private static readonly string[] Sources = ["applebooks", "kindle", "kobo", "koreader", "readwise"];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static IEnumerable<object[]> Cases()
    {
        foreach (var source in Sources)
        {
            var root = ImportFixtures.FixturePath(source);
            if (!Directory.Exists(root))
                continue;

            // API recordings live under the same source with an `api-` version
            // folder (issue #1525). They are JSON for the mapper tests, not a
            // file HighlightImportSourceReader can parse, so this corpus skips
            // them while still requiring their manifest in the folder test.
            foreach (var directory in Directory.EnumerateDirectories(root)
                         .Where(path => !Path.GetFileName(path).StartsWith("api-", StringComparison.Ordinal))
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                if (File.Exists(Path.Combine(directory, "manifest.json")))
                    yield return [source, Path.GetFileName(directory)];
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_versioned_fixture_reads_with_the_counts_its_manifest_records(string source, string version)
    {
        var directory = ImportFixtures.FixturePath(source, version);
        var manifest = ReadManifest(directory);

        manifest.Source.Should().Be(source);
        manifest.Version.Should().Be(version);

        var path = Path.Combine(directory, manifest.File);
        var companions = manifest.Companions.Select(name => Path.Combine(directory, name)).ToArray();
        foreach (var companion in companions)
            File.Exists(companion).Should().BeTrue();

        var file = HighlightImportSourceReader.Read(path, companions);

        file.Source.Should().Be(manifest.Source);
        file.Books.Should().HaveCount(manifest.Expected.FoundBooks);
        file.Books.Sum(book => book.Annotations.Count).Should().Be(manifest.Expected.ImportedAnnotations);

        var skipped = file.Skipped
            .GroupBy(entry => entry.Reason)
            .ToDictionary(group => group.Key, group => group.Sum(entry => entry.Count));
        var expectedSkipped = manifest.Expected.Skipped
            .ToDictionary(entry => Reason(source, entry.Key), entry => entry.Value);
        skipped.Should().BeEquivalentTo(expectedSkipped);

        if (manifest.Expected.MissingOptionalColumns is { } missing)
            file.MissingOptionalColumns.Should().BeEquivalentTo(missing);
    }

    [Fact]
    public void Every_source_has_at_least_one_versioned_fixture()
    {
        var present = Cases()
            .Select(testCase => (string)testCase[0])
            .Distinct(StringComparer.Ordinal)
            .ToList();

        present.Should().BeEquivalentTo(Sources);
    }

    [Fact]
    public void Every_version_folder_has_a_manifest_and_names_its_files()
    {
        var directories = Sources
            .SelectMany(source => Directory.Exists(ImportFixtures.FixturePath(source))
                ? Directory.EnumerateDirectories(ImportFixtures.FixturePath(source))
                : [])
            .ToList();

        directories.Should().NotBeEmpty();

        foreach (var directory in directories)
        {
            var manifestPath = Path.Combine(directory, "manifest.json");
            File.Exists(manifestPath).Should().BeTrue(
                $"{Path.GetFileName(directory)} must have a manifest.json with its expected counts");

            var manifest = ReadManifest(directory);
            manifest.Version.Should().Be(Path.GetFileName(directory));
            manifest.Source.Should().Be(Path.GetFileName(Path.GetDirectoryName(directory)));
            File.Exists(Path.Combine(directory, manifest.File)).Should().BeTrue();
            foreach (var name in manifest.Companions)
                File.Exists(Path.Combine(directory, name)).Should().BeTrue();
        }
    }

    private static Manifest ReadManifest(string directory) =>
        JsonSerializer.Deserialize<Manifest>(
            File.ReadAllText(Path.Combine(directory, "manifest.json")), Json)
        ?? throw new XunitException($"{directory} has an unreadable manifest.json");

    /// <summary>
    /// The manifest's short reason key is stable when the user-facing wording
    /// changes; this mapping is the one place the two meet.
    /// </summary>
    private static string Reason(string source, string key) => (source, key) switch
    {
        ("kindle", "bookmark") => KindleClippingsParser.BookmarkReason,
        ("kindle", "clipping-limit") => KindleClippingsParser.ClippingLimitReason,
        ("kindle", "empty") => KindleClippingsParser.EmptyReason,
        ("kindle", "unrecognised-format") => KindleClippingsParser.UnrecognisedReason,
        ("readwise", "article") => ReadwiseCsvParser.ArticleReason,
        ("readwise", "tweet") => ReadwiseCsvParser.TweetReason,
        ("readwise", "podcast") => ReadwiseCsvParser.PodcastReason,
        ("readwise", "not-a-book") => ReadwiseCsvParser.OtherReason,
        ("readwise", "empty") => ReadwiseCsvParser.EmptyReason,
        ("readwise", "untitled") => ReadwiseCsvParser.UntitledReason,
        _ => throw new XunitException($"Unknown skipped reason '{key}' for source '{source}'"),
    };

    private sealed record Manifest(
        string Source,
        string Version,
        string File,
        IReadOnlyList<string> Companions,
        ExpectedCounts Expected);

    private sealed record ExpectedCounts(
        int FoundBooks,
        int ImportedAnnotations,
        IReadOnlyDictionary<string, int> Skipped,
        IReadOnlyList<string>? MissingOptionalColumns);
}

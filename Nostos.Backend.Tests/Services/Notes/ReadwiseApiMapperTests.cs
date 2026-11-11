using System.Text.Json.Nodes;
using System.Text.Json;
using FluentAssertions;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Services.Notes.Imports;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Services.Notes;

/// <summary>
/// The mapper against the anonymized recording of Readwise's export API
/// (issue #1525). The recording has a book, articles, and synthetic tweet and
/// podcast entries; see Fixtures/readwise/README.md for its provenance.
/// </summary>
public sealed class ReadwiseApiMapperTests
{
    private static JsonDocument Fixture() =>
        JsonDocument.Parse(File.ReadAllText(ImportFixtures.ReadwiseApiExport));

    [Fact]
    public void The_recorded_page_has_the_shape_the_mapper_reads()
    {
        using var fixture = Fixture();

        ReadwiseExportSchema.Check(fixture.RootElement).Should().BeEmpty();
    }

    [Fact]
    public void A_page_maps_to_the_csv_readers_book_shape_and_skip_reasons()
    {
        using var fixture = Fixture();

        var file = ReadwiseApiMapper.Map(fixture.RootElement);

        file.Source.Should().Be(HighlightImportSourceReader.Readwise);
        file.Books.Should().ContainSingle();

        var book = file.Books.Single();
        book.Title.Should().Be("Synthetic Title 4");
        book.Author.Should().Be("Synthetic Author 3");
        book.SourceKey.Should().Be(
            $"readwise:meta:{BookIdentityNormalizer.NormalizeTitle("Synthetic Title 4")}|{BookIdentityNormalizer.NormalizeAuthor("Synthetic Author 3")}");
        book.Notice.Should().BeNull();

        book.Annotations.Should().Equal(
            new ImportAnnotation(
                "readwise_page",
                "51",
                "Synthetic highlight 4.",
                "Synthetic note 3.",
                CreatedAt: new DateTime(2024, 1, 1, 12, 9, 0, DateTimeKind.Utc)),
            new ImportAnnotation(
                "readwise_page",
                "58",
                "Synthetic highlight 5.",
                "Synthetic note 4.",
                CreatedAt: new DateTime(2024, 1, 1, 12, 12, 0, DateTimeKind.Utc)));

        file.Skipped
            .GroupBy(entry => entry.Reason)
            .ToDictionary(group => group.Key, group => group.Sum(entry => entry.Count))
            .Should().BeEquivalentTo(new Dictionary<string, int>
            {
                [ReadwiseBookRules.ArticleReason] = 3,
                [ReadwiseBookRules.TweetReason] = 1,
                [ReadwiseBookRules.PodcastReason] = 1,
            });
    }

    [Fact]
    public void An_amazon_id_wins_the_identity_the_same_way_it_does_in_the_csv_reader()
    {
        const string json =
            """
            {
              "count": 1,
              "nextPageCursor": null,
              "results": [{
                "user_book_id": 1,
                "is_deleted": false,
                "title": "A Book",
                "author": "An Author",
                "category": "books",
                "asin": "B00SYNTH02",
                "highlights": [{
                  "is_deleted": false,
                  "is_discard": false,
                  "text": "A line.",
                  "note": null,
                  "location": 12,
                  "location_type": "location",
                  "highlighted_at": "2024-01-01T10:00:00Z"
                }]
              }]
            }
            """;
        using var document = JsonDocument.Parse(json);

        var file = ReadwiseApiMapper.Map(document.RootElement);

        file.Books.Single().SourceKey.Should().Be("readwise:asin:B00SYNTH02");
        file.Books.Single().Annotations.Single().AnchorKind.Should().Be("readwise_location");
    }

    [Fact]
    public void A_categoryless_tweet_or_podcast_is_still_left_out()
    {
        const string json =
            """
            {
              "count": 2,
              "nextPageCursor": null,
              "results": [
                {
                  "user_book_id": 1,
                  "is_deleted": false,
                  "title": "Tweets From A Reader",
                  "author": "@reader",
                  "category": null,
                  "asin": null,
                  "highlights": [{
                    "is_deleted": false,
                    "is_discard": false,
                    "text": "A tweet.",
                    "note": null,
                    "location": 1,
                    "location_type": "order",
                    "highlighted_at": null
                  }]
                },
                {
                  "user_book_id": 2,
                  "is_deleted": false,
                  "title": "An Episode",
                  "author": "A Host",
                  "category": null,
                  "asin": null,
                  "highlights": [{
                    "is_deleted": false,
                    "is_discard": false,
                    "text": "A podcast line.",
                    "note": null,
                    "location": 60,
                    "location_type": "time_offset",
                    "highlighted_at": null
                  }]
                }
              ]
            }
            """;
        using var document = JsonDocument.Parse(json);

        var file = ReadwiseApiMapper.Map(document.RootElement);

        file.Books.Should().BeEmpty();
        file.Skipped.Select(entry => entry.Reason).Should().Equal(
            ReadwiseBookRules.TweetReason,
            ReadwiseBookRules.PodcastReason);
    }

    [Fact]
    public void A_deleted_or_discarded_highlight_and_a_deleted_book_are_ignored()
    {
        const string json =
            """
            {
              "count": 3,
              "nextPageCursor": null,
              "results": [
                {
                  "user_book_id": 1,
                  "is_deleted": true,
                  "title": "A Deleted Book",
                  "author": "An Author",
                  "category": "books",
                  "asin": null,
                  "highlights": [{
                    "is_deleted": false,
                    "is_discard": false,
                    "text": "Never import me.",
                    "note": null,
                    "location": 1,
                    "location_type": "page",
                    "highlighted_at": null
                  }]
                },
                {
                  "user_book_id": 2,
                  "is_deleted": false,
                  "title": "A Book",
                  "author": "An Author",
                  "category": "books",
                  "asin": null,
                  "highlights": [
                    {
                      "is_deleted": true,
                      "is_discard": false,
                      "text": "Deleted.",
                      "note": null,
                      "location": 2,
                      "location_type": "page",
                      "highlighted_at": null
                    },
                    {
                      "is_deleted": false,
                      "is_discard": true,
                      "text": "Discarded.",
                      "note": null,
                      "location": 3,
                      "location_type": "page",
                      "highlighted_at": null
                    },
                    {
                      "is_deleted": false,
                      "is_discard": false,
                      "text": "Kept.",
                      "note": "",
                      "location": 4,
                      "location_type": "page",
                      "highlighted_at": null
                    }
                  ]
                }
              ]
            }
            """;
        using var document = JsonDocument.Parse(json);

        var file = ReadwiseApiMapper.Map(document.RootElement);

        var book = file.Books.Single();
        book.Title.Should().Be("A Book");
        book.Annotations.Should().ContainSingle().Which.Text.Should().Be("Kept.");
        file.Skipped.Should().BeEmpty();
    }

    [Fact]
    public void A_field_the_mapper_reads_disappearing_is_a_format_failure_with_its_path_only()
    {
        using var fixture = Fixture();
        var root = JsonNode.Parse(fixture.RootElement.GetRawText())!;
        root["results"]!.AsArray()[0]!.AsObject()["highlights"]!.AsArray()[0]!.AsObject()
            .Remove("location_type");
        using var changed = JsonDocument.Parse(root.ToJsonString());

        var act = () => ReadwiseApiMapper.Map(changed.RootElement);

        var exception = act.Should().Throw<ReadwiseFormatException>().Which;
        exception.Message.Should().Contain("$.results[0].highlights[0].location_type");
        exception.Message.Should().Contain("missing");
        exception.Message.Should().NotContain("Synthetic Title");
        exception.Message.Should().NotContain("Synthetic highlight");
    }
}

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Nostos.Backend.Services.Notes.Imports;
using Nostos.Backend.Tests.Mcp;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Endpoints;

/// <summary>
/// The <c>HighlightImportShape</c> event (issue #1523): one warning per file
/// that fails to parse, loses an optional column, or holds entries the parser
/// does not recognise, carrying structure alone.
/// </summary>
public sealed class HighlightImportShapeLogTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("nostos-import-shape-log-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task A_file_that_is_not_an_export_logs_its_shape_and_an_unknown_source()
    {
        using var factory = new ShapeLogFactory();
        var client = factory.CreateClient();

        var response = await UploadAsync(client, "preview", Write("notes.txt", "This is not an export."));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var line = ShapeLines(factory).Single();
        line.Should().Contain("Outcome=parse-failed");
        line.Should().Contain("Source=unknown");
        line.Should().Contain("Shape=lines 1; separators 0");

        // The event is about structure; the file's own words stay out.
        line.Should().NotContain("This is not an export");
    }

    [Fact]
    public async Task A_kobo_database_without_an_optional_column_logs_which_one_went_missing()
    {
        using var factory = new ShapeLogFactory();
        var client = factory.CreateClient();

        var response = await UploadAsync(client, "preview", CreateKoboDatabase());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var line = ShapeLines(factory).Single();
        line.Should().Contain("Source=kobo");
        line.Should().Contain("Outcome=missing-optional-columns");
        line.Should().Contain("Shape=tables: Bookmark, content");
        line.Should().Contain("Bookmark.Annotation");
        line.Should().Contain("Bookmark.Type");
    }

    [Fact]
    public async Task A_preview_that_skips_unrecognised_kindle_entries_logs_their_masked_format()
    {
        using var factory = new ShapeLogFactory();
        var client = factory.CreateClient();
        var path = Write(
            "My Clippings.txt",
            """
            Pride and Prejudice (Austen, Jane)
            - Your Highlight on page 1 | Location 5-6 | Added on Monday, January 1, 2024 10:00:00 AM

            It is a truth universally acknowledged
            ==========
            Pride and Prejudice (Austen, Jane)
            - Unparsed Thing 12 | Else 34

            A secret sentence.
            ==========
            """);

        var response = await UploadAsync(client, "preview", path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var preview = (await response.Content.ReadFromJsonAsync<HighlightImportPreview>())!;
        preview.Skipped.Should().ContainSingle(skip => skip.Reason == KindleClippingsParser.UnrecognisedReason);

        var line = ShapeLines(factory).Single();
        line.Should().Contain("Outcome=unrecognised-entries");
        line.Should().Contain("Source=kindle");
        line.Should().Contain("unrecognised patterns: - Xxxxxxxx Xxxxx 12 | Xxxx 34");

        // The title, author and highlight of the entry are not in the event.
        line.Should().NotContain("Pride and Prejudice");
        line.Should().NotContain("Austen");
        line.Should().NotContain("It is a truth");
        line.Should().NotContain("A secret sentence");
    }

    [Fact]
    public async Task A_clean_readwise_export_logs_nothing()
    {
        using var factory = new ShapeLogFactory();
        var client = factory.CreateClient();

        var response = await UploadAsync(
            client, "preview", ImportFixtures.ReadwiseExport);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        ShapeLines(factory).Should().BeEmpty();
    }

    [Fact]
    public async Task A_commit_does_not_log_the_missing_columns_its_preview_already_logged()
    {
        using var factory = new ShapeLogFactory();
        var client = factory.CreateClient();
        var path = CreateKoboDatabase();

        (await UploadAsync(client, "preview", path)).StatusCode.Should().Be(HttpStatusCode.OK);
        var response = await UploadAsync(client, "commit", path, decisions: "[]");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        ShapeLines(factory).Should().ContainSingle();
    }

    [Fact]
    public async Task A_koreader_sidecar_the_plugin_sends_that_does_not_parse_logs_its_shape()
    {
        using var factory = new ShapeLogFactory();
        var client = factory.CreateClient();
        using var body = new MultipartFormDataContent();
        body.Add(new StringContent("return { [\"doc_props\"] = { title = \"Secret Title\" "), "file", "metadata.lua");

        var response = await client.PostAsync("/api/notes/import/koreader", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var line = ShapeLines(factory).Single();
        line.Should().Contain("Outcome=parse-failed");
        line.Should().NotContain("Secret Title");
    }

    [Fact]
    public void The_kindle_and_readwise_fixtures_never_put_their_titles_authors_or_text_in_the_event()
    {
        using var provider = new CapturingLoggerProvider();
        var logger = provider.CreateLogger("shape-test");
        var fixtures = new[]
        {
            ImportFixtures.KindleEnglish,
            ImportFixtures.ReadwiseExport,
        };

        foreach (var path in fixtures)
        {
            foreach (var outcome in new[]
                     {
                         HighlightImportShapeLog.ParseFailed,
                         HighlightImportShapeLog.MissingOptionalColumns,
                         HighlightImportShapeLog.UnrecognisedEntries,
                     })
            {
                HighlightImportShapeLog.Write(logger, path, outcome);
            }
        }

        var logged = string.Join("\n", provider.Lines);
        logged.Should().Contain(HighlightImportShapeLog.Name).And.Contain("lines");
        foreach (var secret in new[]
                 {
                     "Pride and Prejudice", "Austen", "It is a truth", "The famous opening",
                     "Walden", "Thoreau", "Meditations", "Aurelius", "Synthetic Field Guide",
                     "Ada Reader", "Read Slowly", "Example Blog", "Tweets From", "adareader",
                 })
        {
            logged.Should().NotContain(secret);
        }
    }

    private static IReadOnlyList<string> ShapeLines(ShapeLogFactory factory) =>
        factory.Logs.Lines.Where(line => line.Contains("HighlightImportShape", StringComparison.Ordinal)).ToList();

    private static async Task<HttpResponseMessage> UploadAsync(
        HttpClient client, string step, string path, string? decisions = null)
    {
        using var body = new MultipartFormDataContent();
        body.Add(new ByteArrayContent(await File.ReadAllBytesAsync(path)), "file", Path.GetFileName(path));
        if (decisions is not null)
            body.Add(new StringContent(decisions), "decisions");
        return await client.PostAsync($"/api/notes/imports/{step}", body);
    }

    private string Write(string name, string text)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>A Kobo database with every column the reader needs, except Annotation and Type.</summary>
    private string CreateKoboDatabase()
    {
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.sqlite");
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE content (
                ContentID TEXT NOT NULL, ContentType TEXT NOT NULL, Title TEXT,
                Attribution TEXT, ISBN TEXT, PRIMARY KEY (ContentID));
            CREATE TABLE Bookmark (
                BookmarkID TEXT NOT NULL, VolumeID TEXT NOT NULL, Text TEXT,
                Hidden BOOL, DateCreated TEXT, PRIMARY KEY (BookmarkID));
            INSERT INTO content VALUES ('vol-1', '6', 'Synthetic Kobo Book', 'Ada Reader', NULL);
            INSERT INTO Bookmark VALUES ('bm-1', 'vol-1', 'A highlight.', 0, '2026-09-28T12:00:00.000');
            """;
        command.ExecuteNonQuery();
        return path;
    }

    private sealed class ShapeLogFactory : LibraryEndpointFactory
    {
        public CapturingLoggerProvider Logs { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureLogging(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddProvider(Logs);
            });
        }
    }
}

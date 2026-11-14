using System.IO.Compression;
using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Notes;
using Nostos.Backend.Services.Portability.Markdown;
using Nostos.Backend.Services.Writings;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Notes, highlights, passages and topics as a .zip of Markdown (issue #1221).
/// </summary>
public sealed class MarkdownNotesExportTests
{
    // ------------------------------------------------------------ file names

    [Theory]
    [InlineData("Being and Time", "Being and Time")]
    [InlineData("What is: Art? <A> \"Study\" / part|two*", "What is Art A Study part two")]
    [InlineData("C# and the ^block [link]", "C and the block link")]
    [InlineData("CON", "CON_")]
    [InlineData("lpt1.txt", "lpt1.txt_")]
    [InlineData("  ...hidden. ", "hidden")]
    [InlineData("Brott och straff — Достоевский 📚", "Brott och straff — Достоевский 📚")]
    [InlineData("report‮fdp.exe", "reportfdp.exe")]
    [InlineData("tab\tand\nnewline", "tab and newline")]
    [InlineData("", "Untitled")]
    [InlineData("???", "Untitled")]
    public void A_segment_is_safe_on_every_system_and_keeps_its_letters(string name, string expected) =>
        MarkdownExportNames.Segment(name).Should().Be(expected);

    [Fact]
    public void A_segment_is_composed_and_cut_at_a_whole_character()
    {
        MarkdownExportNames.Segment("Café").Should().Be("Café");

        var cut = MarkdownExportNames.Segment(string.Concat(Enumerable.Repeat("é📚", 100)));
        Encoding.UTF8.GetByteCount(cut).Should().BeLessThanOrEqualTo(MarkdownExportNames.MaxNameBytes);
        cut.Should().NotContain("�");
        char.IsHighSurrogate(cut[^1]).Should().BeFalse();
    }

    [Fact]
    public void Names_are_unique_ignoring_case_and_stable_in_claim_order()
    {
        var names = new MarkdownNameAllocator();
        names.Claim("Ethics", "Spinoza").Should().Be("Ethics");
        names.Claim("ethics", "Aristotle").Should().Be("ethics (Aristotle)");
        names.Claim("ETHICS", "Aristotle").Should().Be("ETHICS (2)");
        names.Claim("Ethics").Should().Be("Ethics (3)");
        names.Claim("Étude").Should().Be("Étude");
        names.Claim("étude").Should().Be("étude (2)");
    }

    // -------------------------------------------------------------- location

    [Fact]
    public void Notes_come_in_reading_order_and_unplaced_ones_last()
    {
        var at = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        MarkdownNote Cfi(string cfi, int minute) =>
            Note(cfiRange: cfi, created: at.AddMinutes(minute));

        var later = Cfi("epubcfi(/6/10!/4/2,/1:0,/1:5)", 0);
        var early = Cfi("epubcfi(/6/4!/4/20/1:3)", 1);
        var sameItemLater = Cfi("epubcfi(/6/4!/4/100,/1:0,/1:9)", 2);
        var unplaced = Note(created: at.AddMinutes(-5));

        MarkdownNotesRenderer.InReadingOrder([unplaced, later, sameItemLater, early])
            .Should().Equal(early, sameItemLater, later, unplaced);

        var p2 = Note(kind: "pdf_page", value: "2", verified: true);
        var p10 = Note(kind: "pdf_page", value: "10", verified: true);
        MarkdownNotesRenderer.InReadingOrder([p10, p2]).Should().Equal(p2, p10);
    }

    [Fact]
    public void A_pdf_note_saved_before_anchors_still_has_its_page_and_its_place_in_order()
    {
        string Page(int n) => "{\"pageNumber\":" + n + ",\"rects\":[]}";
        var p1 = Note(cfiRange: Page(1));
        var p3 = Note(cfiRange: Page(3), passage: true, end: "5");
        var p12 = Note(cfiRange: Page(12));

        MarkdownNotesRenderer.InReadingOrder([p12, p3, p1]).Should().Equal(p1, p3, p12);
        MarkdownNotesRenderer.Locate(p1).Words.Should().Be("Page 1");
        MarkdownNotesRenderer.Locate(p3).Words.Should().Be("Pages 3–5");
    }

    [Fact]
    public void An_epub_note_is_named_by_its_chapter_when_the_contents_have_one()
    {
        var note = Note(kind: "epub_cfi", value: "epubcfi(/6/6!/4/2/1:0)", verified: true);
        IReadOnlyList<string?> sections = [null, "Preface", "The Storm", "The Storm"];

        MarkdownNotesRenderer.Locate(note, sections: sections).Words.Should().Be("In “The Storm”");
        MarkdownNotesRenderer.Locate(note, sections: [null, "Preface"]).Words.Should().Be("Section 3 of the book");
        MarkdownNotesRenderer.Locate(note).Words.Should().Be("Section 3 of the book");
    }

    [Fact]
    public void Chapter_titles_come_from_the_nav_document_and_carry_over_unnamed_sections()
    {
        using var epub = new MemoryStream(EpubWithNav());

        EpubSections.Titles(epub).Should().Equal(null, "Part One", "Part One", "The Storm");
    }

    [Fact]
    public async Task Sections_of_a_forward_only_reader_are_staged_on_the_host_scratch_root_and_deleted()
    {
        var scratch = new RecordingProductScratchStorage(
            Path.Combine(Path.GetTempPath(), $"nostos-sections-scratch-{Guid.NewGuid():N}"));

        try
        {
            // Object storage streams forward only, so the EPUB is copied out
            // once; the copy lives on the host's scratch root (issue #1596) and
            // deletes itself when it closes.
            var titles = await EpubSections.ReadAsync(
                new ForwardOnlyStorage(EpubWithNav()),
                Guid.NewGuid(),
                CancellationToken.None,
                scratch);

            titles.Should().Equal(null, "Part One", "Part One", "The Storm");
            scratch.Files.Should().ContainSingle()
                .Which.Should().StartWith(scratch.Root).And.EndWith(".tmp");
            scratch.Leftovers().Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(scratch.Root))
                Directory.Delete(scratch.Root, recursive: true);
        }
    }

    [Fact]
    public async Task The_notes_archive_is_built_on_the_host_scratch_root_and_deletes_itself()
    {
        await using var library = await LocalPortableTestLibrary.CreateAsync();
        var scratch = new RecordingProductScratchStorage(
            Path.Combine(Path.GetTempPath(), $"nostos-notes-scratch-{Guid.NewGuid():N}"));

        try
        {
            var exporter = new MarkdownNotesExporter(library.Db, library.Storage, scratch);

            var stream = await exporter.ExportAsync(includeWritings: false);
            try
            {
                stream.Length.Should().BeGreaterThan(0);
                scratch.Files.Should().ContainSingle()
                    .Which.Should().StartWith(scratch.Root).And.EndWith(".zip");
                Directory.Exists(scratch.Root).Should().BeTrue();
            }
            finally
            {
                await stream.DisposeAsync();
            }

            scratch.Leftovers().Should().BeEmpty("closing the download deletes the archive");
        }
        finally
        {
            if (Directory.Exists(scratch.Root))
                Directory.Delete(scratch.Root, recursive: true);
        }
    }

    [Fact]
    public void A_location_is_said_in_words_and_a_page_only_when_verified()
    {
        var chapters = new List<MarkdownNotesRenderer.AudioChapter>
        {
            new("Opening", 0),
            new("The Storm", 3600),
        };

        MarkdownNotesRenderer.Locate(Note(kind: "pdf_page", value: "42", verified: true))
            .Words.Should().Be("Page 42");
        MarkdownNotesRenderer.Locate(Note(kind: "pdf_page", value: "42", verified: false))
            .Words.Should().BeNull("an unverified page is not stated as the page");
        MarkdownNotesRenderer.Locate(Note(kind: "audio_timestamp", value: "3723.4", verified: true), chapters)
            .Words.Should().Be("At 1:02:03, in “The Storm”");
        MarkdownNotesRenderer.Locate(Note(kind: "audio_timestamp", value: "65", verified: true, passage: true, end: "130"), chapters)
            .Words.Should().Be("From 1:05 to 2:10, in “Opening”");
        MarkdownNotesRenderer.Locate(Note(kind: "physical_page", value: "17"))
            .Words.Should().Be("Page 17 (as noted)");
        // Kindle and Readwise positions are theirs, said as theirs (#1219).
        MarkdownNotesRenderer.Locate(Note(kind: "kindle_location", value: "5-7"))
            .Words.Should().Be("Location 5–7 on Kindle");
        MarkdownNotesRenderer.Locate(Note(kind: "kindle_page", value: "12"))
            .Words.Should().Be("Page 12 on Kindle");
        MarkdownNotesRenderer.Locate(Note(kind: "kindle_unknown", value: "annotation:abc"))
            .Words.Should().Be("From Kindle");
        MarkdownNotesRenderer.Locate(Note(kind: "readwise_page", value: "88"))
            .Words.Should().Be("Page 88 in Readwise");
        MarkdownNotesRenderer.Locate(Note(kind: "readwise_order", value: "3"))
            .Words.Should().Be("From Readwise");

        var epub = MarkdownNotesRenderer.Locate(Note(cfiRange: "epubcfi(/6/8[ch3]!/4/2,/1:0,/1:5)"));
        epub.Words.Should().Be("Section 4 of the book", "the raw CFI is not printed");
    }

    [Fact]
    public void A_citation_becomes_its_words_and_code_stays_as_written()
    {
        var book = Guid.NewGuid().ToString();
        var content = $"As [(Smith 2020, p. 4)](nostos:cite/{book}?p=4&as=quote) said.\n\n`[x](nostos:cite/{book})`\n\n[a link](https://example.org)";
        WritingCitationIndex.CitationsAsWords(content).Should().Be(
            $"As (Smith 2020, p. 4) said.\n\n`[x](nostos:cite/{book})`\n\n[a link](https://example.org)");
    }

    [Fact]
    public void Front_matter_strings_read_back_as_themselves()
    {
        MarkdownNotesRenderer.Yaml("He said \"no\"\\ then\nleft").Should().Be("\"He said \\\"no\\\"\\\\ then\\nleft\"");
        MarkdownNotesRenderer.Authors("Deleuze & Guattari").Should().Equal("Deleuze", "Guattari");
        MarkdownNotesRenderer.Authors("Aurelius, Marcus").Should().Equal("Aurelius, Marcus");
        MarkdownNotesRenderer.Authors("Unknown").Should().BeEmpty();
    }

    // -------------------------------------------------------------- endpoint

    [Fact]
    public async Task The_endpoint_streams_a_zip_of_books_topics_writings_and_a_readme()
    {
        using var factory = new LibraryEndpointFactory();
        var seeded = await SeedAsync(factory);

        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/portability/markdown");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/zip");
        response.Content.Headers.ContentDisposition!.FileName.Should().StartWith("nostos-notes-").And.EndWith(".zip");

        var files = await ReadZipAsync(response);
        files.Keys.Should().OnlyContain(path => path.StartsWith("Nostos notes/"));
        files.Keys.Should().BeEquivalentTo(
            "Nostos notes/README.md",
            "Nostos notes/Books/What is Art “Studies”.md",
            "Nostos notes/Books/The Odyssey.md",
            "Nostos notes/Topics/Angst.md",
            "Nostos notes/Topics/Either or.md",
            "Nostos notes/Topics/Unused.md",
            "Nostos notes/Writings/Essays/On dread.md");

        // A book without notes has no file.
        files.Keys.Should().NotContain(path => path.Contains("Empty shelf"));

        var epub = files["Nostos notes/Books/What is Art “Studies”.md"];
        epub.Should().StartWith("---\ntitle: \"What is: Art? “Studies”\"\n");
        epub.Should().Contain("authors: [\"Søren Kierkegaard\"]\n");
        epub.Should().Contain("year: 1844\n");
        epub.Should().Contain("isbn: \"978-0-14-044577-0\"\n");
        epub.Should().Contain("format: \"EPUB\"\n");
        epub.Should().Contain("notes: 2\n");
        epub.Should().Contain($"nostos_id: \"{seeded.EpubId}\"\n");
        // Reading order: section 2 before section 5, though written later.
        epub.IndexOf("## Section 2 of the book", StringComparison.Ordinal)
            .Should().BeLessThan(epub.IndexOf("## Section 5 of the book", StringComparison.Ordinal));
        // The book's words are escaped; the reader's words and links stay as written.
        epub.Should().Contain("> Anxiety is the \\*dizziness\\* of freedom \\[sic\\]");
        epub.Should().Contain("Dread *is* freedom's possibility. [[Angst]] [[Either/or]]");
        epub.Should().Contain("*Highlight · 2026-03-02*").And.NotContain("epubcfi");

        var audio = files["Nostos notes/Books/The Odyssey.md"];
        audio.Should().Contain("format: \"Audiobook\"\n");
        audio.Should().Contain("narrator: \"Ian McKellen\"\n");
        audio.Should().Contain("## Passage: The Cyclops");
        audio.Should().Contain("*Passage · From 1:02:03 to 1:05:00, in “Book Nine” · 2026-03-04*");
        audio.Should().Contain("## At 0:30, in “Book One”");

        var angst = files["Nostos notes/Topics/Angst.md"];
        angst.Should().Contain("# Angst");
        angst.Should().Contain("The feeling before a choice.");
        angst.Should().Contain("### [[Books/What is Art “Studies”|What is: Art? “Studies”]]");
        angst.Should().Contain("- Section 5 of the book — “Anxiety is the \\*dizziness\\* of freedom \\[sic\\]” — Dread *is* freedom's possibility.");
        angst.Should().Contain("### [[Books/The Odyssey|The Odyssey]]");

        var eitherOr = files["Nostos notes/Topics/Either or.md"];
        eitherOr.Should().StartWith("---\naliases: [\"Either/or\"]\n");

        var unused = files["Nostos notes/Topics/Unused.md"];
        unused.Should().Contain("notes: 0\n").And.Contain("Words without notes.").And.Contain("*No notes carry this topic yet.*");

        var writing = files["Nostos notes/Writings/Essays/On dread.md"];
        writing.Should().Contain("As (Kierkegaard 1844) wrote.");
        writing.Should().NotContain("nostos:cite");

        var readme = files["Nostos notes/README.md"];
        readme.Should().Contain("4 notes from 2 books, and 3 topics");
        readme.Should().Contain("`Writings/`");
    }

    [Fact]
    public async Task Writings_can_be_left_out_and_an_empty_library_still_exports_a_readme()
    {
        using var factory = new LibraryEndpointFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/portability/markdown?writings=false");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var files = await ReadZipAsync(response);
        files.Keys.Should().Equal("Nostos notes/README.md");
        files["Nostos notes/README.md"].Should().Contain("0 notes from 0 books").And.NotContain("`Writings/`");
    }

    private sealed record Seeded(Guid EpubId);

    private static async Task<Seeded> SeedAsync(LibraryEndpointFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
        var day = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

        WorkModel Work(string title) => new()
        {
            Id = Guid.NewGuid(),
            Title = title,
            NormalizedTitle = title.ToUpperInvariant(),
            NormalizedAuthor = string.Empty,
            CreatedAt = day,
        };

        var epubWork = Work("Art");
        var epub = new EBookModel
        {
            Title = "What is: Art? “Studies”",
            Author = "Søren Kierkegaard",
            Isbn = "978-0-14-044577-0",
            Metadata = new BookMetadata { PublishedDate = "1844-06-17" },
            FileDetails = new FileInfoDetails { HasFile = true, FileName = "art.epub" },
            WorkId = epubWork.Id,
            Work = epubWork,
            CreatedAt = day,
        };
        var audioWork = Work("Odyssey");
        var audio = new AudioBookModel
        {
            Title = "The Odyssey",
            Author = "Homer",
            Narrator = "Ian McKellen",
            FileDetails = new FileInfoDetails
            {
                HasFile = true,
                FileName = "odyssey.m4b",
                ChaptersJson = "[{\"Title\":\"Book One\",\"StartTime\":0},{\"Title\":\"Book Nine\",\"StartTime\":3600}]",
            },
            WorkId = audioWork.Id,
            Work = audioWork,
            CreatedAt = day.AddMinutes(1),
        };
        var emptyWork = Work("Empty");
        var empty = new PhysicalBookModel
        {
            Title = "Empty shelf",
            WorkId = emptyWork.Id,
            Work = emptyWork,
            CreatedAt = day.AddMinutes(2),
        };

        var angst = new TopicModel { Topic = "Angst", Description = "The feeling before a choice." };
        var eitherOr = new TopicModel { Topic = "Either/or" };
        var unused = new TopicModel { Topic = "Unused", Description = "Words without notes." };

        var dizziness = new NoteModel
        {
            Book = epub,
            Content = "Dread *is* freedom's possibility. [[Angst]] [[Either/or]]",
            SelectedText = "Anxiety is the *dizziness* of freedom [sic]",
            CfiRange = "epubcfi(/6/10!/4/2,/1:0,/1:10)",
            CreatedAt = day,
        };
        var opening = new NoteModel
        {
            Book = epub,
            Content = string.Empty,
            SelectedText = "Preface",
            CfiRange = "epubcfi(/6/4!/4/2,/1:0,/1:10)",
            CreatedAt = day.AddDays(1),
        };
        var cyclops = new NoteModel
        {
            Book = audio,
            Content = "Nobody is my name.",
            Kind = "passage",
            PassageTitle = "The Cyclops",
            SourceAnchorKind = "audio_timestamp",
            SourceAnchorValue = "3723",
            PassageEnd = "3900",
            AnchorVerified = true,
            CreatedAt = day.AddDays(3),
        };
        var early = new NoteModel
        {
            Book = audio,
            Content = "Sing, muse. [[Angst]]",
            SourceAnchorKind = "audio_timestamp",
            SourceAnchorValue = "30",
            AnchorVerified = true,
            CreatedAt = day.AddDays(4),
        };

        db.AddRange(epub, audio, empty, angst, eitherOr, unused, dizziness, opening, cyclops, early);
        db.NoteTopics.AddRange(
            new NoteTopicModel { Note = dizziness, Topic = angst },
            new NoteTopicModel { Note = dizziness, Topic = eitherOr },
            new NoteTopicModel { Note = early, Topic = angst });

        var essays = new WritingModel { Name = "Essays", Type = WritingType.Folder };
        db.Writings.AddRange(
            essays,
            new WritingModel
            {
                Name = "On dread",
                Type = WritingType.Document,
                Parent = essays,
                Content = $"As [(Kierkegaard 1844)](nostos:cite/{epub.Id}?as=reference) wrote.",
            });

        await db.SaveChangesAsync();
        return new Seeded(epub.Id);
    }

    private static async Task<Dictionary<string, string>> ReadZipAsync(HttpResponseMessage response)
    {
        using var bytes = new MemoryStream();
        await response.Content.CopyToAsync(bytes);
        bytes.Position = 0;
        using var archive = new ZipArchive(bytes, ZipArchiveMode.Read);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            files[entry.FullName] = await reader.ReadToEndAsync();
        }
        return files;
    }

    private static MarkdownNote Note(
        string kind = "unknown",
        string? value = null,
        bool verified = false,
        string? cfiRange = null,
        bool passage = false,
        string? end = null,
        DateTime? created = null) =>
        new(Guid.NewGuid(), string.Empty, cfiRange, null,
            created ?? new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            passage ? "passage" : "note", kind, value, verified, passage ? "Title" : null, end, []);

    /// <summary>An EPUB whose table of contents names sections one and three.</summary>
    private static byte[] EpubWithNav()
    {
        using var epub = new MemoryStream();
        using (var zip = new ZipArchive(epub, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, string text)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open());
                writer.Write(text);
            }

            Add("META-INF/container.xml",
                "<container xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"><rootfiles><rootfile full-path=\"OEBPS/content.opf\"/></rootfiles></container>");
            Add("OEBPS/content.opf",
                "<package xmlns=\"http://www.idpf.org/2007/opf\"><manifest>"
                + "<item id=\"nav\" href=\"nav.xhtml\" properties=\"nav\" media-type=\"application/xhtml+xml\"/>"
                + "<item id=\"c0\" href=\"cover.xhtml\" media-type=\"application/xhtml+xml\"/>"
                + "<item id=\"c1\" href=\"text/one.xhtml\" media-type=\"application/xhtml+xml\"/>"
                + "<item id=\"c2\" href=\"text/two.xhtml\" media-type=\"application/xhtml+xml\"/>"
                + "<item id=\"c3\" href=\"text/three.xhtml\" media-type=\"application/xhtml+xml\"/>"
                + "</manifest><spine><itemref idref=\"c0\"/><itemref idref=\"c1\"/><itemref idref=\"c2\"/><itemref idref=\"c3\"/></spine></package>");
            Add("OEBPS/nav.xhtml",
                "<html xmlns:epub=\"http://www.idpf.org/2007/ops\"><body><nav epub:type=\"toc\"><ol>"
                + "<li><a href=\"text/one.xhtml\">Part  One</a></li>"
                + "<li><a href=\"text/three.xhtml#s2\">The Storm</a></li></ol></nav></body></html>");
        }

        return epub.ToArray();
    }

    /// <summary>The stored EPUB as an object-storage adapter serves it: forward only.</summary>
    private sealed class ForwardOnlyStorage(byte[] epub) : DelegatingBookAssetStorage
    {
        public override Task<StoredAssetRead?> OpenBookFileAsync(
            Guid bookId,
            StorageByteRange? range = null,
            CancellationToken ct = default) =>
            Task.FromResult<StoredAssetRead?>(new StoredAssetRead(
                new StoredAssetInfo("book.epub", "application/epub+zip", epub.Length, "etag", DateTimeOffset.UnixEpoch),
                new ForwardOnlyStream(epub)));
    }

    private sealed class ForwardOnlyStream(byte[] content) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => content.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(count, content.Length - _position);
            content.AsSpan(_position, read).CopyTo(buffer.AsSpan(offset));
            _position += read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

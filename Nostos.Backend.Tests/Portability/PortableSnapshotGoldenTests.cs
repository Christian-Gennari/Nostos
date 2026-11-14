using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Enums;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Characterization (golden) coverage for the portable snapshot projection and
/// every digest derived from it (#1618).
/// </summary>
/// <remarks>
/// <para>
/// The portable archive is an on-disk interchange format: the same library must
/// serialize to the same <c>data/library.json</c> bytes, the same manifest and
/// the same per-entry SHA-256 values no matter which code path reads it. #1618
/// moves the snapshot projection and the hashing helpers out of
/// <c>PortableArchiveService</c> and <c>PortableLibraryRecoverySource</c> into
/// one shared implementation, so these tests pin the observable output on both
/// paths before the refactor and prove it unchanged afterwards.
/// </para>
/// <para>
/// The fixture is seeded from fixed identifiers and fixed timestamps, so the
/// archive is reproducible. Golden files live beside this test under
/// <c>Golden/PortableSnapshot</c>. To intentionally accept a format change, run
/// the test once with <c>NOSTOS_UPDATE_PORTABILITY_GOLDEN=1</c> and review the
/// diff; CI never sets it.
/// </para>
/// </remarks>
public sealed class PortableSnapshotGoldenTests
{
    private const string UpdateGoldenVariable = "NOSTOS_UPDATE_PORTABILITY_GOLDEN";

    private static readonly DateTimeOffset ExportTime =
        new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Export_projection_manifest_and_entry_hashes_match_the_golden_files()
    {
        await using var library = await LocalPortableTestLibrary.CreateAsync();
        await PortableSnapshotGoldenLibrary.PopulateAsync(library);

        using var archive = new MemoryStream();
        await library.Portability(new TestTimeProvider(ExportTime)).ExportAsync(archive);

        var entries = PortableArchiveTestSupport.ReadEntries(archive.ToArray());

        AssertGolden(
            "export/entries.txt",
            string.Join(
                "\n",
                entries.Select(entry =>
                    $"{entry.Name}\t{PortableArchiveTestSupport.Sha256Hex(entry.Bytes)}\t{entry.Bytes.Length}")) + "\n");

        AssertGolden(
            "export/data.json",
            Encoding.UTF8.GetString(
                entries.Single(entry => entry.Name == PortableArchiveFormat.DataPath).Bytes));

        AssertGolden(
            "export/manifest.json",
            Encoding.UTF8.GetString(
                entries.Single(entry => entry.Name == PortableArchiveFormat.ManifestPath).Bytes));
    }

    [Fact]
    public async Task Recovery_source_projection_and_digest_match_the_golden_files()
    {
        await using var library = await LocalPortableTestLibrary.CreateAsync();
        await PortableSnapshotGoldenLibrary.PopulateAsync(library);

        using var payload = new MemoryStream();
        var recovered = await PortableLibraryRecoverySource.WriteRelationalPayloadAsync(
            library.Db,
            payload,
            PortableSnapshotGoldenLibrary.RetainedMedia());

        AssertGolden("recovery/data.json", Encoding.UTF8.GetString(payload.ToArray()));
        AssertGolden(
            "recovery/summary.txt",
            $"""
             dataBytes={recovered.DataBytes}
             dataSha256={recovered.DataSha256}
             works={recovered.Counts.Works}
             books={recovered.Counts.Books}
             notes={recovered.Counts.Notes}
             topics={recovered.Counts.Topics}
             noteTopics={recovered.Counts.NoteTopics}
             writings={recovered.Counts.Writings}
             writingNotes={recovered.Counts.WritingNotes}
             collections={recovered.Counts.Collections}
             collectionMemberships={recovered.Counts.CollectionMemberships}
             acquisitions={recovered.Counts.Acquisitions}
             assistantSettings={recovered.Counts.AssistantSettings}
             noteImportBookLinks={recovered.Counts.NoteImportBookLinks}
             mediaEntries={recovered.Counts.MediaEntries}
             readingListEntries={recovered.Counts.ReadingListEntries}

             """);
    }

    private static void AssertGolden(string relativePath, string actual)
    {
        var path = Path.Combine(
            GoldenRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar));

        if (Environment.GetEnvironmentVariable(UpdateGoldenVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return;
        }

        File.Exists(path).Should().BeTrue(
            $"the golden file {path} must be committed; regenerate it once with " +
            $"{UpdateGoldenVariable}=1 and review the diff");

        var expected = File.ReadAllText(path, Encoding.UTF8);
        actual.Should().Be(
            expected,
            $"the portable output pinned in {relativePath} is an on-disk format and must not drift");
    }

    private static string GoldenRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null
                && !File.Exists(Path.Combine(directory.FullName, "Nostos.sln")))
            {
                directory = directory.Parent;
            }

            if (directory is null)
                throw new InvalidOperationException("Could not locate the repository root.");

            return Path.Combine(
                directory.FullName,
                "Nostos.Backend.Tests",
                "Portability",
                "Golden",
                "PortableSnapshot");
        }
    }
}

/// <summary>
/// A deterministic library covering every shape the snapshot projection
/// serializes: works with their book values, two editions of one work, a
/// single-file ebook, a physical book, a single-file audiobook, a multi-track
/// audiobook, nested collections, notes (including a passage), topics,
/// writings, an acquisition, assistant settings, note import book links and the
/// reading list.
/// </summary>
internal static class PortableSnapshotGoldenLibrary
{
    private static readonly Guid WorkA = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid WorkB = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid WorkC = Guid.Parse("10000000-0000-0000-0000-000000000003");
    private static readonly Guid WorkD = Guid.Parse("10000000-0000-0000-0000-000000000004");

    private static readonly Guid EpubId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid PdfId = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly Guid AudioId = Guid.Parse("20000000-0000-0000-0000-000000000003");
    private static readonly Guid PhysicalId = Guid.Parse("20000000-0000-0000-0000-000000000004");
    private static readonly Guid MultiTrackId = Guid.Parse("20000000-0000-0000-0000-000000000005");

    private static readonly Guid ReadingId = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid PhilosophyId = Guid.Parse("30000000-0000-0000-0000-000000000002");

    private static readonly Guid EpubNoteId = Guid.Parse("40000000-0000-0000-0000-000000000001");
    private static readonly Guid AudioNoteId = Guid.Parse("40000000-0000-0000-0000-000000000002");
    private static readonly Guid TopicOneId = Guid.Parse("50000000-0000-0000-0000-000000000001");
    private static readonly Guid TopicTwoId = Guid.Parse("50000000-0000-0000-0000-000000000002");

    private static readonly Guid FolderId = Guid.Parse("60000000-0000-0000-0000-000000000001");
    private static readonly Guid DocumentId = Guid.Parse("60000000-0000-0000-0000-000000000002");

    private static readonly Guid AcquisitionId = Guid.Parse("70000000-0000-0000-0000-000000000001");
    private static readonly Guid NoteImportLinkId = Guid.Parse("80000000-0000-0000-0000-000000000001");
    private static readonly Guid ReadingListFirstId = Guid.Parse("90000000-0000-0000-0000-000000000001");
    private static readonly Guid ReadingListSecondId = Guid.Parse("90000000-0000-0000-0000-000000000002");

    private static readonly DateTime BaseTime =
        new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private static readonly byte[] EpubBytes = Encoding.UTF8.GetBytes("GOLDEN-EPUB-CONTENT");
    private static readonly byte[] PdfBytes = Encoding.UTF8.GetBytes("GOLDEN-PDF-CONTENT");
    private static readonly byte[] AudioBytes = Encoding.UTF8.GetBytes("GOLDEN-AUDIO-CONTENT");
    private static readonly byte[] CoverBytes = Encoding.UTF8.GetBytes("GOLDEN-COVER-CONTENT");
    private static readonly byte[][] TrackBytes =
    [
        Enumerable.Repeat((byte)0xA1, 3000).ToArray(),
        Enumerable.Repeat((byte)0xB2, 17).ToArray(),
        Enumerable.Repeat((byte)0xC3, 9001).ToArray(),
    ];

    public static async Task PopulateAsync(LocalPortableTestLibrary library)
    {
        var workA = new WorkModel
        {
            Id = WorkA,
            Title = "Golden Work A",
            Author = "Example Author",
            NormalizedTitle = "GOLDEN WORK A",
            NormalizedAuthor = "EXAMPLE AUTHOR",
            CreatedAt = BaseTime,
            Rating = 5,
            IsFavorite = true,
            PersonalReview = "A review of the book, not the edition.",
        };
        var workB = new WorkModel
        {
            Id = WorkB,
            Title = "Golden Work B",
            Author = "Second Author",
            NormalizedTitle = "GOLDEN WORK B",
            NormalizedAuthor = "SECOND AUTHOR",
            CreatedAt = BaseTime.AddMinutes(1),
        };
        var workC = new WorkModel
        {
            Id = WorkC,
            Title = "Golden Work C",
            Author = null,
            NormalizedTitle = "GOLDEN WORK C",
            NormalizedAuthor = string.Empty,
            CreatedAt = BaseTime.AddMinutes(2),
        };
        var workD = new WorkModel
        {
            Id = WorkD,
            Title = "Golden Work D",
            Author = "A Reader",
            NormalizedTitle = "GOLDEN WORK D",
            NormalizedAuthor = "A READER",
            CreatedAt = BaseTime.AddMinutes(3),
        };

        var epub = new EBookModel
        {
            Id = EpubId,
            WorkId = workA.Id,
            Work = workA,
            Title = "Golden EPUB",
            Author = "Example Author",
            Status = BookStatus.Ready,
            StatusMessage = null,
            CreatedAt = BaseTime.AddHours(1),
            PageCount = 321,
            Isbn = "9780000000001",
            Metadata = new BookMetadata
            {
                Subtitle = "A portable edition",
                Description = "Description",
                Editor = "Editor",
                Translator = "Translator",
                Publisher = "Portable Press",
                PlaceOfPublication = "Lisbon",
                PublishedDate = "1965",
                Language = "en",
                Categories = "Fiction",
                Edition = "2",
                Series = "Golden",
                VolumeNumber = "1",
            },
            Progress = new ReadingProgress
            {
                LastLocation = "epubcfi(/6/4)",
                ProgressPercent = 42,
                // This edition's own exception to the book's review.
                PersonalReview = "This edition's own note.",
                LastReadAt = BaseTime.AddDays(3),
                FinishedAt = BaseTime.AddDays(5),
            },
            FileDetails = new FileInfoDetails
            {
                HasFile = true,
                FileName = @"C:\source-machine\private\reader.epub",
                CoverFileName = "/srv/private/cover.jpg",
                ChaptersJson = "[{\"generated\":true}]",
                LocationsJson = "{\"cache\":\"must-not-cross\"}",
            },
        };

        var pdf = new EBookModel
        {
            Id = PdfId,
            WorkId = workA.Id,
            Work = workA,
            Title = "Golden PDF Edition",
            Author = "Example Author",
            CreatedAt = BaseTime.AddHours(2),
            PageCount = 280,
            Isbn = "9780000000002",
            Progress = new ReadingProgress
            {
                LastLocation = "page:73",
                ProgressPercent = 26,
                // This edition's own exception to the book's rating.
                Rating = 4,
            },
            FileDetails = new FileInfoDetails
            {
                HasFile = true,
                FileName = "/mnt/library/original.pdf",
            },
        };

        var audio = new AudioBookModel
        {
            Id = AudioId,
            WorkId = workB.Id,
            Work = workB,
            Title = "Golden Audio",
            Author = "Second Author",
            CreatedAt = BaseTime.AddHours(3),
            Asin = "B000000001",
            Duration = "10:11:12",
            Narrator = "Narrator",
            Progress = new ReadingProgress
            {
                LastLocation = "3721.5",
                ProgressPercent = 37,
                LastReadAt = BaseTime.AddDays(4),
            },
            FileDetails = new FileInfoDetails
            {
                HasFile = true,
                FileName = "/another-machine/audio.m4b",
                CoverFileName = "/another-machine/audio-cover.png",
            },
        };

        var physical = new PhysicalBookModel
        {
            Id = PhysicalId,
            WorkId = workC.Id,
            Work = workC,
            Title = "Golden Physical",
            CreatedAt = BaseTime.AddHours(4),
            PageCount = 199,
            Isbn = "9780000000003",
            Progress = new ReadingProgress
            {
                LastLocation = "page:12",
                ProgressPercent = 6,
            },
        };

        var tracks = new List<BookTrack>();
        for (var number = 1; number <= TrackBytes.Length; number++)
        {
            var bytes = TrackBytes[number - 1];
            var storedName = await library.Storage.SaveTrackAsync(
                MultiTrackId,
                number,
                new MemoryStream(bytes),
                "part.mp3");
            tracks.Add(new BookTrack(
                number,
                storedName,
                "audio/mpeg",
                number * 1000_000L,
                bytes.Length,
                Crc32.Compute(bytes),
                $"Part {number}"));
        }

        var tracksJson = BookTrackList.Serialize(tracks);

        var multiTrack = new AudioBookModel
        {
            Id = MultiTrackId,
            WorkId = workD.Id,
            Work = workD,
            Title = "Golden Multi Track",
            Author = "A Reader",
            Narrator = "A Volunteer",
            Duration = "01:40:00",
            CreatedAt = BaseTime.AddHours(5),
            Progress = new ReadingProgress
            {
                LastLocation = "3100.5",
                ProgressPercent = 51,
            },
            FileDetails = new FileInfoDetails
            {
                HasFile = true,
                FileName = null,
                CoverFileName = "cover.jpg",
                TracksJson = tracksJson,
                ChaptersJson = "[{\"title\":\"Part 1\",\"startTime\":0}]",
            },
        };

        var reading = new CollectionModel { Id = ReadingId, Name = "Reading" };
        var philosophy = new CollectionModel
        {
            Id = PhilosophyId,
            Name = "Philosophy",
            ParentId = reading.Id,
            Parent = reading,
        };

        var epubNote = new NoteModel
        {
            Id = EpubNoteId,
            BookId = epub.Id,
            Book = epub,
            Content = "Processed thought",
            RawContent = "raw thought",
            SelectedText = "selected passage",
            CfiRange = "epubcfi(/6/4!/4/2)",
            HighlightColour = "sage",
            Kind = "passage",
            PassageTitle = "The Grand Inquisitor",
            PassageEnd = "epubcfi(/6/8!/4/20/1:12)",
            PassageHtml = "<p>selected <em>passage</em></p>",
            CaptureSource = "voice",
            ProcessingMode = "light_polish",
            SourceAnchorKind = "epub_cfi",
            SourceAnchorValue = "epubcfi(/6/4!/4/2)",
            AnchorVerified = true,
            CreatedAt = BaseTime.AddDays(2),
        };
        var audioNote = new NoteModel
        {
            Id = AudioNoteId,
            BookId = audio.Id,
            Book = audio,
            Content = "An audio thought",
            CaptureSource = "paste",
            ProcessingMode = "verbatim",
            SourceAnchorKind = "timestamp",
            SourceAnchorValue = "3721.5",
            AnchorVerified = false,
            CreatedAt = BaseTime.AddDays(2).AddMinutes(30),
        };

        var topicOne = new TopicModel
        {
            Id = TopicOneId,
            Topic = "Portability",
            Description = "Moving a library between hosts without losing anything.",
        };
        var topicTwo = new TopicModel
        {
            Id = TopicTwoId,
            Topic = "Golden",
            Description = null,
        };

        var folder = new WritingModel
        {
            Id = FolderId,
            Name = "Essays",
            Type = WritingType.Folder,
            CreatedAt = BaseTime,
            UpdatedAt = BaseTime.AddDays(1),
        };
        var document = new WritingModel
        {
            Id = DocumentId,
            Name = "Golden draft",
            Type = WritingType.Document,
            Content = "<p>User-owned studio prose.</p>",
            PageSetup = "{\"paper\":\"letter\",\"marginMm\":25.4,\"font\":\"newsreader\",\"lineSpacing\":1.5,\"citationStyle\":\"apa\"}",
            ParentId = folder.Id,
            Parent = folder,
            CreatedAt = BaseTime.AddHours(1),
            UpdatedAt = BaseTime.AddDays(5),
        };

        library.Db.Works.AddRange(workA, workB, workC, workD);
        library.Db.Books.AddRange(epub, pdf, audio, physical, multiTrack);
        library.Db.Collections.AddRange(reading, philosophy);
        library.Db.BookCollections.AddRange(
            new BookCollectionModel
            {
                BookId = epub.Id,
                Book = epub,
                CollectionId = reading.Id,
                Collection = reading,
                AddedAt = BaseTime.AddDays(1),
            },
            new BookCollectionModel
            {
                BookId = epub.Id,
                Book = epub,
                CollectionId = philosophy.Id,
                Collection = philosophy,
                AddedAt = BaseTime.AddDays(2),
            },
            new BookCollectionModel
            {
                BookId = audio.Id,
                Book = audio,
                CollectionId = reading.Id,
                Collection = reading,
                AddedAt = BaseTime.AddDays(3),
            });
        library.Db.Notes.AddRange(epubNote, audioNote);
        library.Db.Topics.AddRange(topicOne, topicTwo);
        library.Db.NoteTopics.AddRange(
            new NoteTopicModel
            {
                NoteId = epubNote.Id,
                Note = epubNote,
                TopicId = topicOne.Id,
                Topic = topicOne,
            },
            new NoteTopicModel
            {
                NoteId = epubNote.Id,
                Note = epubNote,
                TopicId = topicTwo.Id,
                Topic = topicTwo,
            },
            new NoteTopicModel
            {
                NoteId = audioNote.Id,
                Note = audioNote,
                TopicId = topicTwo.Id,
                Topic = topicTwo,
            });
        library.Db.Writings.AddRange(folder, document);
        library.Db.WritingNotes.Add(new WritingNoteModel
        {
            WritingId = document.Id,
            Writing = document,
            NoteId = epubNote.Id,
            Note = epubNote,
            AddedAt = BaseTime.AddDays(3),
        });
        library.Db.BookAcquisitions.Add(new BookAcquisitionModel
        {
            Id = AcquisitionId,
            BookId = epub.Id,
            Book = epub,
            ProviderId = "gutenberg",
            ProviderDisplayName = "Project Gutenberg",
            ExternalId = "12345",
            AssetId = "epub3-images",
            AssetFormat = "epub3-images",
            ImportedExtension = ".epub",
            SourceUrl = "https://www.gutenberg.org/ebooks/12345",
            RightsStatement = "Public domain in the USA.",
            AcquiredAt = BaseTime.AddDays(1),
        });
        library.Db.NoteImportBookLinks.Add(new NoteImportBookLink
        {
            Id = NoteImportLinkId,
            Source = "kobo",
            SourceKey = "kobo-book-1",
            BookId = epub.Id,
            Book = epub,
            CreatedAtUtc = BaseTime.AddDays(4),
        });
        library.Db.AssistantSettings.Add(new AssistantSettingsModel
        {
            CaptureProcessingMode = "light_polish",
            UpdatedAtUtc = BaseTime.AddDays(4),
        });
        library.Db.ReadingListEntries.AddRange(
            new ReadingListEntryModel
            {
                Id = ReadingListFirstId,
                Title = "Wanted Next",
                Author = "Wished Author",
                Isbn = "9781590171998",
                PublishedDate = "1965",
                Why = "A reason worth keeping.",
                RecommendedBy = "A friend",
                NextPosition = 0,
                CreatedAt = BaseTime.AddDays(1),
            },
            new ReadingListEntryModel
            {
                Id = ReadingListSecondId,
                Title = "Wanted Someday",
                CreatedAt = BaseTime.AddDays(2),
            });

        await library.Db.SaveChangesAsync();

        await SaveAsync(library.Storage, EpubId, "source.epub", EpubBytes);
        await SaveAsync(library.Storage, PdfId, "source.pdf", PdfBytes);
        await SaveAsync(library.Storage, AudioId, "source.m4b", AudioBytes);
        await SaveCoverAsync(library.Storage, EpubId, "source.jpg", CoverBytes);
        await SaveCoverAsync(library.Storage, AudioId, "source.png", CoverBytes);
        await SaveCoverAsync(library.Storage, MultiTrackId, "source.jpg", CoverBytes);

        library.Db.ChangeTracker.Clear();
    }

    /// <summary>
    /// The retained-media descriptors a recovery host would hold for the seeded
    /// library: one book/cover pair per single-file edition, the PDF's file, and
    /// the multi-track audiobook's three tracks plus its cover.
    /// </summary>
    public static IReadOnlyList<PortableRecoveryMedia> RetainedMedia() =>
    [
        Media(EpubId, PortableArchiveFormat.BookMediaKind, "book.epub", EpubBytes),
        Media(EpubId, PortableArchiveFormat.CoverMediaKind, "cover.jpg", CoverBytes),
        Media(PdfId, PortableArchiveFormat.BookMediaKind, "book.pdf", PdfBytes),
        Media(AudioId, PortableArchiveFormat.BookMediaKind, "book.m4b", AudioBytes),
        Media(AudioId, PortableArchiveFormat.CoverMediaKind, "cover.png", CoverBytes),
        Media(MultiTrackId, PortableArchiveFormat.TrackMediaKind, "track-0001.mp3", TrackBytes[0]),
        Media(MultiTrackId, PortableArchiveFormat.TrackMediaKind, "track-0002.mp3", TrackBytes[1]),
        Media(MultiTrackId, PortableArchiveFormat.TrackMediaKind, "track-0003.mp3", TrackBytes[2]),
        Media(MultiTrackId, PortableArchiveFormat.CoverMediaKind, "cover.jpg", CoverBytes),
    ];

    private static PortableRecoveryMedia Media(
        Guid bookId,
        string kind,
        string fileName,
        byte[] bytes) =>
        new(
            bookId,
            kind,
            fileName,
            bytes.LongLength,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
                .ToLowerInvariant());

    private static async Task SaveAsync(
        IBookAssetStorage storage,
        Guid bookId,
        string fileName,
        byte[] content)
    {
        await using var stream = new MemoryStream(content, writable: false);
        await storage.SaveBookFileAsync(bookId, stream, fileName);
    }

    private static async Task SaveCoverAsync(
        IBookAssetStorage storage,
        Guid bookId,
        string fileName,
        byte[] content)
    {
        await using var stream = new MemoryStream(content, writable: false);
        await storage.SaveBookCoverAsync(bookId, stream, fileName);
    }
}

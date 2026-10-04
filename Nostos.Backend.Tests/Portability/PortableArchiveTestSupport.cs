using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nostos.Backend.Configuration;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Portability;

namespace Nostos.Backend.Tests.Portability;

internal sealed record PortableFixtureIds(
    Guid EpubBookId,
    Guid PdfBookId,
    Guid AudioBookId,
    Guid PhysicalBookId,
    Guid ReadingCollectionId,
    Guid NestedCollectionId,
    Guid NoteId,
    Guid TopicId,
    Guid WritingDocumentId);

internal sealed class LocalPortableTestLibrary : IAsyncDisposable
{
    private LocalPortableTestLibrary(
        string root,
        NostosDbContext db,
        FileStorageService storage)
    {
        Root = root;
        Db = db;
        Storage = storage;
    }

    public string Root { get; }
    public NostosDbContext Db { get; }
    public FileStorageService Storage { get; }

    public static async Task<LocalPortableTestLibrary> CreateAsync(
        Action<DbContextOptionsBuilder<NostosDbContext>>? configureOptions = null)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"nostos-portability-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        var dbPath = Path.Combine(root, "nostos.db");
        var optionsBuilder = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={dbPath}");
        configureOptions?.Invoke(optionsBuilder);
        var db = new NostosDbContext(optionsBuilder.Options);
        await db.Database.EnsureCreatedAsync();

        var env = new PortableTestWebHostEnvironment(root);
        var storage = new FileStorageService(
            env,
            Options.Create(new FileStorageOptions
            {
                BooksRoot = Path.Combine(root, "books"),
            }),
            NullLogger<FileStorageService>.Instance);

        return new LocalPortableTestLibrary(root, db, storage);
    }

    public PortableArchiveService Portability() =>
        new(Db, Storage, NullLogger<PortableArchiveService>.Instance);

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // Test cleanup only.
        }
    }
}

internal static class PortableArchiveTestSupport
{
    public const string SecretMarker = "THIS-SECRET-MUST-NEVER-BE-EXPORTED";

    public static async Task<PortableFixtureIds> PopulateRepresentativeAsync(
        NostosDbContext db,
        IBookAssetStorage storage)
    {
        var now = DateTime.UtcNow.AddDays(-10);

        var workA = new WorkModel
        {
            Id = Guid.NewGuid(),
            Title = "Portable Work A",
            Author = "Example Author",
            NormalizedTitle = "PORTABLE WORK A",
            NormalizedAuthor = "EXAMPLE AUTHOR",
            CreatedAt = now,
        };
        var workB = new WorkModel
        {
            Id = Guid.NewGuid(),
            Title = "Portable Work B",
            Author = "Second Author",
            NormalizedTitle = "PORTABLE WORK B",
            NormalizedAuthor = "SECOND AUTHOR",
            CreatedAt = now.AddMinutes(1),
        };
        var workC = new WorkModel
        {
            Id = Guid.NewGuid(),
            Title = "Portable Work C",
            Author = null,
            NormalizedTitle = "PORTABLE WORK C",
            NormalizedAuthor = string.Empty,
            CreatedAt = now.AddMinutes(2),
        };

        var epub = new EBookModel
        {
            Id = Guid.NewGuid(),
            WorkId = workA.Id,
            Work = workA,
            Title = "Portable EPUB",
            Author = "Example Author",
            CreatedAt = now.AddHours(1),
            PageCount = 321,
            Metadata = new BookMetadata
            {
                Subtitle = "A portable edition",
                Translator = "Translator",
                Publisher = "Portable Press",
                Language = "en",
                Edition = "2",
            },
            Progress = new ReadingProgress
            {
                LastLocation = "epubcfi(/6/4)",
                ProgressPercent = 42,
                Rating = 5,
                IsFavorite = true,
                PersonalReview = "Important review",
                LastReadAt = now.AddDays(3),
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
            Id = Guid.NewGuid(),
            WorkId = workA.Id,
            Work = workA,
            Title = "Portable PDF Edition",
            Author = "Example Author",
            CreatedAt = now.AddHours(2),
            PageCount = 280,
            Progress = new ReadingProgress
            {
                LastLocation = "page:73",
                ProgressPercent = 26,
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
            Id = Guid.NewGuid(),
            WorkId = workB.Id,
            Work = workB,
            Title = "Portable Audio",
            Author = "Second Author",
            CreatedAt = now.AddHours(3),
            Asin = "B000000001",
            Duration = "10:11:12",
            Narrator = "Narrator",
            Progress = new ReadingProgress
            {
                LastLocation = "3721.5",
                ProgressPercent = 37,
                LastReadAt = now.AddDays(4),
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
            Id = Guid.NewGuid(),
            WorkId = workC.Id,
            Work = workC,
            Title = "Portable Physical",
            CreatedAt = now.AddHours(4),
            PageCount = 199,
            Progress = new ReadingProgress
            {
                LastLocation = "page:12",
                ProgressPercent = 6,
            },
        };

        var reading = new CollectionModel
        {
            Id = Guid.NewGuid(),
            Name = "Reading",
        };
        var philosophy = new CollectionModel
        {
            Id = Guid.NewGuid(),
            Name = "Philosophy",
            ParentId = reading.Id,
            Parent = reading,
        };

        var note = new NoteModel
        {
            Id = Guid.NewGuid(),
            BookId = epub.Id,
            Book = epub,
            Content = "Processed thought",
            RawContent = "raw thought",
            SelectedText = "selected passage",
            CfiRange = "epubcfi(/6/4!/4/2)",
            CaptureSource = "voice",
            ProcessingMode = "light_polish",
            SourceAnchorKind = "epub_cfi",
            SourceAnchorValue = "epubcfi(/6/4!/4/2)",
            AnchorVerified = true,
            CreatedAt = now.AddDays(2),
        };
        var topic = new TopicModel
        {
            Id = Guid.NewGuid(),
            Topic = "Portability",
        };

        var studioFolder = new WritingModel
        {
            Id = Guid.NewGuid(),
            Name = "Essays",
            Type = WritingType.Folder,
            CreatedAt = now,
            UpdatedAt = now.AddDays(1),
        };
        var studioDocument = new WritingModel
        {
            Id = Guid.NewGuid(),
            Name = "Portable draft",
            Type = WritingType.Document,
            Content = "<p>User-owned studio prose.</p>",
            ParentId = studioFolder.Id,
            Parent = studioFolder,
            CreatedAt = now.AddHours(1),
            UpdatedAt = now.AddDays(5),
        };

        db.Works.AddRange(workA, workB, workC);
        db.Books.AddRange(epub, pdf, audio, physical);
        db.Collections.AddRange(reading, philosophy);
        db.BookCollections.AddRange(
            new BookCollectionModel
            {
                BookId = epub.Id,
                Book = epub,
                CollectionId = reading.Id,
                Collection = reading,
                AddedAt = now.AddDays(1),
            },
            new BookCollectionModel
            {
                BookId = epub.Id,
                Book = epub,
                CollectionId = philosophy.Id,
                Collection = philosophy,
                AddedAt = now.AddDays(2),
            },
            new BookCollectionModel
            {
                BookId = audio.Id,
                Book = audio,
                CollectionId = reading.Id,
                Collection = reading,
                AddedAt = now.AddDays(3),
            });
        db.Notes.Add(note);
        db.Topics.Add(topic);
        db.NoteTopics.Add(new NoteTopicModel
        {
            NoteId = note.Id,
            Note = note,
            TopicId = topic.Id,
            Topic = topic,
        });
        db.Writings.AddRange(studioFolder, studioDocument);
        db.WritingNotes.Add(new WritingNoteModel
        {
            WritingId = studioDocument.Id,
            Writing = studioDocument,
            NoteId = note.Id,
            Note = note,
            AddedAt = now.AddDays(3),
        });
        db.BookAcquisitions.Add(new BookAcquisitionModel
        {
            Id = Guid.NewGuid(),
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
            AcquiredAt = now.AddDays(1),
        });
        db.AssistantSettings.Add(new AssistantSettingsModel
        {
            CaptureProcessingMode = "light_polish",
            UpdatedAtUtc = now.AddDays(4),
        });
        db.AiProviderSettings.Add(new AiProviderSettingsModel
        {
            LlmEnabled = true,
            LlmBaseUrl = "https://provider.example.test",
            LlmModel = "private-model",
            LlmApiKeyEncrypted = SecretMarker,
            SttApiKeyEncrypted = SecretMarker + "-STT",
            UpdatedAtUtc = now,
        });

        await db.SaveChangesAsync();

        await SaveAsync(storage, epub.Id, "source.epub", "EPUB-CONTENT-PORTABLE");
        await SaveAsync(storage, pdf.Id, "source.pdf", "PDF-CONTENT-PORTABLE");
        await SaveAsync(storage, audio.Id, "source.m4b", "AUDIO-CONTENT-PORTABLE");
        await SaveCoverAsync(storage, epub.Id, "source.jpg", "EPUB-COVER-PORTABLE");
        await SaveCoverAsync(storage, audio.Id, "source.png", "AUDIO-COVER-PORTABLE");

        return new PortableFixtureIds(
            epub.Id,
            pdf.Id,
            audio.Id,
            physical.Id,
            reading.Id,
            philosophy.Id,
            note.Id,
            topic.Id,
            studioDocument.Id);
    }

    public static async Task<byte[]> ReadBookAsync(
        IBookAssetStorage storage,
        Guid bookId)
    {
        await using var opened = await storage.OpenBookFileAsync(bookId);
        if (opened is null)
            throw new InvalidOperationException($"Book {bookId} has no stored file.");

        using var output = new MemoryStream();
        await opened.Content.CopyToAsync(output);
        return output.ToArray();
    }

    private static async Task SaveAsync(
        IBookAssetStorage storage,
        Guid bookId,
        string fileName,
        string content)
    {
        await using var stream = new MemoryStream(
            System.Text.Encoding.UTF8.GetBytes(content));
        await storage.SaveBookFileAsync(bookId, stream, fileName);
    }

    private static async Task SaveCoverAsync(
        IBookAssetStorage storage,
        Guid bookId,
        string fileName,
        string content)
    {
        await using var stream = new MemoryStream(
            System.Text.Encoding.UTF8.GetBytes(content));
        await storage.SaveBookCoverAsync(bookId, stream, fileName);
    }
}

internal sealed class PortableTestWebHostEnvironment(string root)
    : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "Nostos.Backend.Tests";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = root;
    public string EnvironmentName { get; set; } = "Testing";
    public string ContentRootPath { get; set; } = root;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

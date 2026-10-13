using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nostos.Backend.Configuration;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;
using Nostos.Backend.Services.ExternalMedia;
using Nostos.Backend.Tests.Support;

namespace Nostos.Backend.Tests.ExternalMedia;

/// <summary>
/// A real books directory, a real external folder and a real SQLite file,
/// wired the way the SelfHosted host wires them.
/// </summary>
internal sealed class ExternalMediaTestBed : IDisposable
{
    private readonly SqliteTestFixture _sqlite = new();
    private readonly string _temp;
    private readonly string _databasePath;

    public ExternalMediaTestBed(bool configureRoot = true)
    {
        _temp = Path.Combine(Path.GetTempPath(), "nostos-external-media-" + Guid.NewGuid().ToString("N"));
        BooksRoot = Path.Combine(_temp, "books");
        ExternalRoot = Path.Combine(_temp, "external");
        Outside = Path.Combine(_temp, "outside");
        Directory.CreateDirectory(BooksRoot);
        Directory.CreateDirectory(ExternalRoot);
        Directory.CreateDirectory(Outside);

        _databasePath = _sqlite.CreateDatabasePath();
        using (_sqlite.CreateContext(_databasePath))
        {
        }

        Contexts = new ContextFactory(_databasePath);
        Owned = new FileStorageService(
            new TestEnvironment { ContentRootPath = _temp },
            Options.Create(new FileStorageOptions { BooksRoot = BooksRoot }),
            NullLogger<FileStorageService>.Instance);
        Root = new ExternalMediaRoot(Options.Create(new ExternalMediaOptions
        {
            Root = configureRoot ? ExternalRoot : null,
        }));
        Links = new ExternalMediaLinkStore(Contexts);
        Storage = new ExternalMediaBookStorage(Owned, Owned, Root, Links);
    }

    public string BooksRoot { get; }
    public string ExternalRoot { get; }

    /// <summary>A directory beside the external root that no reference may reach.</summary>
    public string Outside { get; }

    public IDbContextFactory<NostosDbContext> Contexts { get; }
    public FileStorageService Owned { get; }
    public ExternalMediaRoot Root { get; }
    public ExternalMediaLinkStore Links { get; }
    public ExternalMediaBookStorage Storage { get; }

    public string WriteSource(string relativePath, string content)
    {
        var path = Path.Combine(ExternalRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, new DateTime(2024, 3, 2, 10, 0, 0, DateTimeKind.Utc));
        return path;
    }

    public async Task<Guid> AddBookAsync()
    {
        await using var db = await Contexts.CreateDbContextAsync();
        var book = new AudioBookModel { Title = "Linked", Author = "Author" };
        db.Books.Add(book);
        await db.SaveChangesAsync();
        return book.Id;
    }

    public async Task LinkAsync(Guid bookId, string relativePath, int trackNumber = ExternalMediaLinkModel.PrimaryFile)
    {
        await using var db = await Contexts.CreateDbContextAsync();
        db.ExternalMediaLinks.Add(new ExternalMediaLinkModel
        {
            BookId = bookId,
            TrackNumber = trackNumber,
            RootId = Root.RootId,
            RelativePath = relativePath,
        });
        await db.SaveChangesAsync();
    }

    public async Task<int> CountLinksAsync(Guid bookId)
    {
        await using var db = await Contexts.CreateDbContextAsync();
        return await db.ExternalMediaLinks.CountAsync(l => l.BookId == bookId);
    }

    public void Dispose()
    {
        _sqlite.Dispose();
        try
        {
            if (Directory.Exists(_temp))
                Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup only.
        }
    }

    private sealed class ContextFactory(string path) : IDbContextFactory<NostosDbContext>
    {
        public NostosDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<NostosDbContext>()
                .UseSqlite($"Data Source={path}")
                .Options);
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Nostos.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = "Testing";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
    }
}

/// <summary>What a source file looked like, to prove it was not touched.</summary>
internal sealed record SourceSnapshot(string Path, byte[] Bytes, DateTime LastWriteTimeUtc)
{
    public static SourceSnapshot Take(string path) =>
        new(path, File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path));

    public bool IsUnchanged() =>
        File.Exists(Path)
        && File.ReadAllBytes(Path).AsSpan().SequenceEqual(Bytes)
        && File.GetLastWriteTimeUtc(Path) == LastWriteTimeUtc;
}

using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.ExternalMedia;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Tests.Portability;
using Xunit;

namespace Nostos.Backend.Tests.ExternalMedia;

/// <summary>
/// What a full portable export means for a linked book: the archive carries
/// the media itself, so the library it is imported into owns its files and
/// depends on no folder. An export that cannot read a linked file refuses
/// rather than produce an archive that silently lacks it.
/// </summary>
public sealed class ExternalMediaPortableExportTests : IDisposable
{
    private readonly string _external =
        Path.Combine(Path.GetTempPath(), "nostos-external-export-" + Guid.NewGuid().ToString("N"));

    public ExternalMediaPortableExportTests() => Directory.CreateDirectory(_external);

    public void Dispose()
    {
        if (Directory.Exists(_external))
            Directory.Delete(_external, recursive: true);
    }

    [Fact]
    public async Task An_export_carries_linked_media_as_bytes_and_the_imported_library_owns_them()
    {
        var sourceBytes = SyntheticMedia.Epub("Linked Essay", "Sam Writer");
        var sourcePath = Path.Combine(_external, "Shelf", "Linked Essay.epub");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        await File.WriteAllBytesAsync(sourcePath, sourceBytes);
        var snapshot = SourceSnapshot.Take(sourcePath);
        await using var source = await LocalPortableTestLibrary.CreateAsync();
        var bookId = await AddLinkedBookAsync(source.Db, "Shelf/Linked Essay.epub");

        using var archive = new MemoryStream();
        var exported = await Portability(source).ExportAsync(archive);

        exported.MediaFiles.Should().Be(1);
        snapshot.IsUnchanged().Should().BeTrue();
        var raw = Encoding.Latin1.GetString(archive.ToArray());
        raw.Should().NotContain(_external).And.NotContain("Shelf/Linked Essay.epub");

        await using var destination = await LocalPortableTestLibrary.CreateAsync();
        archive.Position = 0;
        await destination.Portability().ImportAsync(archive);

        (await destination.Db.ExternalMediaLinks.CountAsync()).Should().Be(0);
        var owned = Path.Combine(destination.Root, "books", bookId.ToString(), "book.epub");
        (await File.ReadAllBytesAsync(owned)).Should().Equal(sourceBytes);
    }

    [Fact]
    public async Task An_export_refuses_when_a_linked_file_is_unavailable()
    {
        await using var source = await LocalPortableTestLibrary.CreateAsync();
        await AddLinkedBookAsync(source.Db, "Shelf/Not There.epub");

        using var archive = new MemoryStream();
        var act = () => Portability(source).ExportAsync(archive);

        (await act.Should().ThrowAsync<PortableArchiveException>())
            .Which.Code.Should().Be("source_media_missing");
    }

    private PortableArchiveService Portability(LocalPortableTestLibrary library)
    {
        var root = new ExternalMediaRoot(Options.Create(new ExternalMediaOptions { Root = _external }));
        var storage = new ExternalMediaBookStorage(
            library.Storage,
            library.Storage,
            root,
            new ExternalMediaLinkStore(new SameDatabase(library.Db)));

        return new PortableArchiveService(
            library.Db,
            storage,
            NullLogger<PortableArchiveService>.Instance,
            bookTextScheduler: null,
            timeProvider: null,
            trackStorage: storage);
    }

    private static async Task<Guid> AddLinkedBookAsync(NostosDbContext db, string relativePath)
    {
        var book = new EBookModel { Title = "Linked Essay", Author = "Sam Writer" };
        book.FileDetails.HasFile = true;
        book.FileDetails.FileName = "book.epub";
        db.Books.Add(book);
        db.ExternalMediaLinks.Add(new ExternalMediaLinkModel
        {
            BookId = book.Id,
            TrackNumber = ExternalMediaLinkModel.PrimaryFile,
            RootId = ExternalMediaOptions.DefaultRootId,
            RelativePath = relativePath,
        });
        await db.SaveChangesAsync();
        return book.Id;
    }

    private sealed class SameDatabase(NostosDbContext db) : IDbContextFactory<NostosDbContext>
    {
        private readonly string _connectionString = db.Database.GetConnectionString()!;

        public NostosDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<NostosDbContext>().UseSqlite(_connectionString).Options);
    }
}

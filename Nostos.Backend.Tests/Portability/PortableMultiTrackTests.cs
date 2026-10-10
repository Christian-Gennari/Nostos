using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Portability;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// A multi-track audiobook must survive export and import whole (issue #835).
/// The failure these guard against is quiet: a book that arrives with its
/// title, cover and progress, and no audio.
/// </summary>
public sealed class PortableMultiTrackTests
{
    private static readonly byte[][] TrackBytes =
    [
        Enumerable.Repeat((byte)0xA1, 3000).ToArray(),
        Enumerable.Repeat((byte)0xB2, 17).ToArray(),
        Enumerable.Repeat((byte)0xC3, 9001).ToArray(),
    ];

    [Fact]
    public async Task Multi_track_audiobook_round_trips_with_every_track()
    {
        await using var source = await LocalPortableTestLibrary.CreateAsync();
        var bookId = await SeedMultiTrackBookAsync(source);
        var sourceTracksJson = (await source.Db.Books.AsNoTracking().SingleAsync()).FileDetails.TracksJson;

        using var archive = new MemoryStream();
        var exported = await source.Portability().ExportAsync(archive);

        var entries = PortableArchiveTestSupport.ReadEntries(archive.ToArray());
        entries.Select(e => e.Name).Should().Contain(
        [
            $"media/books/{bookId:N}/track-0001.mp3",
            $"media/books/{bookId:N}/track-0002.mp3",
            $"media/books/{bookId:N}/track-0003.mp3",
            $"media/books/{bookId:N}/cover.jpg",
        ]);
        entries.Select(e => e.Name).Should().NotContain(name => name.Contains("/book."));

        using var manifest = JsonDocument.Parse(entries.Single(e => e.Name == "manifest.json").Bytes);
        manifest.RootElement.GetProperty("dataVersion").GetInt32().Should().Be(4);
        manifest.RootElement.GetProperty("media").EnumerateArray()
            .Count(media => media.GetProperty("kind").GetString() == "track")
            .Should().Be(3);

        await using var destination = await LocalPortableTestLibrary.CreateAsync();
        archive.Position = 0;
        var imported = await destination.Portability().ImportAsync(archive);
        imported.IntegrityVerified.Should().BeTrue();

        var book = await destination.Db.Books.AsNoTracking().SingleAsync();
        book.Should().BeOfType<AudioBookModel>();
        book.FileDetails.HasFile.Should().BeTrue();
        book.FileDetails.FileName.Should().BeNull();
        book.FileDetails.CoverFileName.Should().Be("cover.jpg");
        book.FileDetails.TracksJson.Should().Be(sourceTracksJson);
        book.Progress.LastLocation.Should().Be("3100.5", "a position across the whole book still means the same thing");

        for (var number = 1; number <= 3; number++)
        {
            await using var stored = await destination.Storage.OpenTrackAsync(bookId, number);
            using var buffer = new MemoryStream();
            await stored!.Content.CopyToAsync(buffer);
            buffer.ToArray().Should().Equal(TrackBytes[number - 1]);
        }

        destination.Storage.GetBookFileName(bookId).Should().BeNull();

        // And it exports again to the same media.
        using var second = new MemoryStream();
        var reexported = await destination.Portability().ExportAsync(second);
        reexported.Counts.Should().Be(exported.Counts);
    }

    [Fact]
    public async Task Archive_missing_one_listed_track_is_rejected_and_imports_nothing()
    {
        await using var source = await LocalPortableTestLibrary.CreateAsync();
        var bookId = await SeedMultiTrackBookAsync(source);
        using var archive = new MemoryStream();
        await source.Portability().ExportAsync(archive);

        // Drop track 2 from both the manifest and the archive, leaving the
        // book's track list claiming three.
        var entries = PortableArchiveTestSupport.ReadEntries(archive.ToArray());
        var dropped = $"media/books/{bookId:N}/track-0002.mp3";
        entries.RemoveAll(e => e.Name == dropped);
        PortableArchiveTestSupport.MutateJsonEntry(entries, "manifest.json", root =>
        {
            var media = root["media"]!.AsArray();
            var index = media.Select((node, i) => (node, i))
                .Single(x => x.node!["path"]!.GetValue<string>() == dropped).i;
            media.RemoveAt(index);
        });

        await using var destination = await LocalPortableTestLibrary.CreateAsync();
        using var tampered = new MemoryStream(PortableArchiveTestSupport.BuildArchive(entries));
        var act = () => destination.Portability().ImportAsync(tampered);

        (await act.Should().ThrowAsync<PortableArchiveException>())
            .Which.Code.Should().Be("missing_referenced_media");
        (await destination.Db.Books.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Track_media_without_a_track_list_is_rejected()
    {
        await using var source = await LocalPortableTestLibrary.CreateAsync();
        await SeedMultiTrackBookAsync(source);
        using var archive = new MemoryStream();
        await source.Portability().ExportAsync(archive);

        var entries = PortableArchiveTestSupport.ReadEntries(archive.ToArray());
        PortableArchiveTestSupport.MutateJsonEntry(entries, "data/library.json", root =>
            root["books"]!.AsArray()[0]!.AsObject().Remove("tracksJson"));
        PortableArchiveTestSupport.RehashDataDescriptor(entries);

        await using var destination = await LocalPortableTestLibrary.CreateAsync();
        using var tampered = new MemoryStream(PortableArchiveTestSupport.BuildArchive(entries));
        var act = () => destination.Portability().ImportAsync(tampered);

        (await act.Should().ThrowAsync<PortableArchiveException>())
            .Which.Code.Should().Be("missing_referenced_media");
    }

    [Fact]
    public async Task A_host_without_track_storage_fails_the_import_instead_of_dropping_the_audio()
    {
        await using var source = await LocalPortableTestLibrary.CreateAsync();
        await SeedMultiTrackBookAsync(source);
        using var archive = new MemoryStream();
        await source.Portability().ExportAsync(archive);

        await using var destination = await LocalPortableTestLibrary.CreateAsync();
        var importer = new PortableArchiveService(
            destination.Db,
            destination.Storage,
            NullLogger<PortableArchiveService>.Instance);

        archive.Position = 0;
        var act = () => importer.ImportAsync(archive);

        (await act.Should().ThrowAsync<PortableArchiveException>())
            .Which.Code.Should().Be("track_storage_unavailable");
        destination.Db.ChangeTracker.Clear();
        (await destination.Db.Books.CountAsync()).Should().Be(0, "the import transaction is rolled back");
    }

    [Fact]
    public async Task Verifier_checks_stored_tracks_and_never_passes_them_unseen()
    {
        await using var library = await LocalPortableTestLibrary.CreateAsync();
        var bookId = await SeedMultiTrackBookAsync(library);
        using var archive = new MemoryStream();
        await library.Portability().ExportAsync(archive);
        using var manifestJson = JsonDocument.Parse(
            PortableArchiveTestSupport.ReadEntries(archive.ToArray()).Single(e => e.Name == "manifest.json").Bytes);
        var expected = manifestJson.RootElement.GetProperty("media").EnumerateArray()
            .Select(media => new PortableArchiveMediaEntry(
                media.GetProperty("bookId").GetGuid(),
                media.GetProperty("kind").GetString()!,
                media.GetProperty("path").GetString()!,
                media.GetProperty("fileName").GetString()!,
                media.GetProperty("contentType").GetString()!,
                media.GetProperty("length").GetInt64(),
                media.GetProperty("sha256").GetString()!))
            .ToList();
        IPortableLibraryVerifier verifier = new PortableLibraryVerifier();

        var withTracks = await verifier.VerifyMediaAsync(library.Storage, library.Storage, expected);
        withTracks.Passed.Should().BeTrue();
        withTracks.MediaFilesVerified.Should().Be(4);

        // The overload hosts used before tracks existed must fail on a track
        // it cannot look up, not report success.
        var withoutTracks = await verifier.VerifyMediaAsync(library.Storage, expected);
        withoutTracks.Passed.Should().BeFalse();
        withoutTracks.Failures.Should().HaveCount(3);

        // A track whose stored bytes changed is caught.
        await library.Storage.SaveTrackAsync(
            bookId, 2, new MemoryStream(Enumerable.Repeat((byte)0xFF, 17).ToArray()), "track.mp3");
        var corrupted = await verifier.VerifyMediaAsync(library.Storage, library.Storage, expected);
        corrupted.Passed.Should().BeFalse();
        corrupted.Failures.Should().ContainSingle();
    }

    private static async Task<Guid> SeedMultiTrackBookAsync(LocalPortableTestLibrary library)
    {
        var now = DateTime.UtcNow.AddDays(-3);
        var work = new WorkModel
        {
            Id = Guid.NewGuid(),
            Title = "Recorded Work",
            Author = "A Reader",
            NormalizedTitle = "RECORDED WORK",
            NormalizedAuthor = "A READER",
            CreatedAt = now,
        };

        var tracks = new List<BookTrack>();
        var bookId = Guid.NewGuid();
        for (var number = 1; number <= TrackBytes.Length; number++)
        {
            var bytes = TrackBytes[number - 1];
            var stored = await library.Storage.SaveTrackAsync(bookId, number, new MemoryStream(bytes), "part.mp3");
            tracks.Add(new BookTrack(
                number, stored, "audio/mpeg", number * 1000_000L, bytes.Length, Crc32.Compute(bytes), $"Part {number}"));
        }

        await library.Storage.SaveBookCoverAsync(bookId, new MemoryStream([0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3]), "cover.jpg");

        library.Db.Works.Add(work);
        library.Db.Books.Add(new AudioBookModel
        {
            Id = bookId,
            WorkId = work.Id,
            Work = work,
            Title = "Recorded Work",
            Author = "A Reader",
            Narrator = "A Volunteer",
            Duration = "01:40:00",
            CreatedAt = now,
            Progress = new ReadingProgress { LastLocation = "3100.5", ProgressPercent = 51 },
            FileDetails = new FileInfoDetails
            {
                HasFile = true,
                FileName = null,
                CoverFileName = "cover.jpg",
                TracksJson = BookTrackList.Serialize(tracks),
                ChaptersJson = "[{\"title\":\"Part 1\",\"startTime\":0}]",
            },
        });
        await library.Db.SaveChangesAsync();
        library.Db.ChangeTracker.Clear();
        return bookId;
    }
}

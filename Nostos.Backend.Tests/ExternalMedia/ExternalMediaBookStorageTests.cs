using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;
using Xunit;

namespace Nostos.Backend.Tests.ExternalMedia;

public sealed class ExternalMediaBookStorageTests : IDisposable
{
    private readonly ExternalMediaTestBed _bed = new();

    public void Dispose() => _bed.Dispose();

    [Fact]
    public async Task A_linked_file_is_read_in_place_and_nothing_is_copied_into_the_books_directory()
    {
        _bed.WriteSource("Author/Title/Original Name.m4b", "0123456789");
        var bookId = await _bed.AddBookAsync();
        await _bed.LinkAsync(bookId, "Author/Title/Original Name.m4b");

        await using var read = await _bed.Storage.OpenBookFileAsync(bookId);

        (await ReadAllAsync(read!)).Should().Be("0123456789");
        Directory.EnumerateFileSystemEntries(_bed.BooksRoot).Should().BeEmpty();
    }

    [Fact]
    public async Task A_linked_file_is_presented_under_the_owned_name_not_its_source_name()
    {
        _bed.WriteSource("Author/Title/Original Name.M4B", "0123456789");
        var bookId = await _bed.AddBookAsync();
        await _bed.LinkAsync(bookId, "Author/Title/Original Name.M4B");

        var info = await _bed.Storage.GetBookFileInfoAsync(bookId);

        info!.FileName.Should().Be("book.m4b");
        info.ContentType.Should().Be(MediaTypeMap.ForBookFile("book.m4b"));
        info.Length.Should().Be(10);
    }

    [Fact]
    public async Task A_byte_range_of_a_linked_file_is_served_from_the_source()
    {
        _bed.WriteSource("book.mp3", "0123456789");
        var bookId = await _bed.AddBookAsync();
        await _bed.LinkAsync(bookId, "book.mp3");

        await using var read = await _bed.Storage.OpenBookFileAsync(bookId, new StorageByteRange(3, 6));

        var buffer = new byte[4];
        await read!.Content.ReadExactlyAsync(buffer);
        Encoding.ASCII.GetString(buffer).Should().Be("3456");
        read.Info.Length.Should().Be(10);
    }

    [Fact]
    public async Task A_range_past_the_end_of_a_linked_file_is_refused()
    {
        _bed.WriteSource("book.mp3", "0123456789");
        var bookId = await _bed.AddBookAsync();
        await _bed.LinkAsync(bookId, "book.mp3");

        var act = () => _bed.Storage.OpenBookFileAsync(bookId, new StorageByteRange(3, 10));

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task A_missing_source_reads_as_no_file_and_keeps_the_reference()
    {
        var source = _bed.WriteSource("book.epub", "epub");
        var bookId = await _bed.AddBookAsync();
        await _bed.LinkAsync(bookId, "book.epub");
        File.Delete(source);

        (await _bed.Storage.GetBookFileInfoAsync(bookId)).Should().BeNull();
        (await _bed.Storage.OpenBookFileAsync(bookId)).Should().BeNull();
        (await _bed.CountLinksAsync(bookId)).Should().Be(1);
    }

    [Fact]
    public async Task A_source_swapped_for_a_symlink_after_linking_is_refused_when_opened()
    {
        var source = _bed.WriteSource("book.epub", "epub");
        var bookId = await _bed.AddBookAsync();
        await _bed.LinkAsync(bookId, "book.epub");
        var secret = Path.Combine(_bed.Outside, "secret.epub");
        File.WriteAllText(secret, "secret");
        File.Delete(source);
        File.CreateSymbolicLink(source, secret);

        (await _bed.Storage.OpenBookFileAsync(bookId)).Should().BeNull();
    }

    [Fact]
    public async Task A_stored_reference_that_points_outside_the_root_reads_nothing()
    {
        File.WriteAllText(Path.Combine(_bed.Outside, "secret.epub"), "secret");
        var bookId = await _bed.AddBookAsync();
        await _bed.LinkAsync(bookId, "../outside/secret.epub");

        (await _bed.Storage.OpenBookFileAsync(bookId)).Should().BeNull();
    }

    [Fact]
    public async Task Removing_a_book_s_files_never_touches_the_source()
    {
        var source = SourceSnapshot.Take(_bed.WriteSource("Author/book.m4b", "audio"));
        var bookId = await _bed.AddBookAsync();
        await _bed.LinkAsync(bookId, "Author/book.m4b");
        await using (var cover = new MemoryStream([1, 2, 3]))
            await _bed.Storage.SaveBookCoverAsync(bookId, cover, "cover.png");

        await _bed.Storage.DeleteBookFilesAsync(bookId);

        source.IsUnchanged().Should().BeTrue();
        (await _bed.CountLinksAsync(bookId)).Should().Be(0);
        Directory.Exists(Path.Combine(_bed.BooksRoot, bookId.ToString())).Should().BeFalse();
    }

    [Fact]
    public async Task Removing_a_linked_primary_file_forgets_the_reference_and_keeps_the_source()
    {
        var source = SourceSnapshot.Take(_bed.WriteSource("book.m4b", "audio"));
        var bookId = await _bed.AddBookAsync();
        await _bed.LinkAsync(bookId, "book.m4b");

        var removed = await _bed.Storage.DeleteBookFileAsync(bookId);

        removed.Should().BeTrue();
        source.IsUnchanged().Should().BeTrue();
        (await _bed.Storage.GetBookFileInfoAsync(bookId)).Should().BeNull();
    }

    [Fact]
    public async Task An_uploaded_replacement_becomes_owned_media_and_leaves_the_source_as_it_was()
    {
        var source = SourceSnapshot.Take(_bed.WriteSource("book.m4b", "source audio"));
        var bookId = await _bed.AddBookAsync();
        await _bed.LinkAsync(bookId, "book.m4b");

        await using (var upload = new MemoryStream(Encoding.ASCII.GetBytes("uploaded audio")))
            await _bed.Storage.SaveBookFileAsync(bookId, upload, "replacement.m4b");

        source.IsUnchanged().Should().BeTrue();
        (await _bed.CountLinksAsync(bookId)).Should().Be(0);
        await using var read = await _bed.Storage.OpenBookFileAsync(bookId);
        (await ReadAllAsync(read!)).Should().Be("uploaded audio");
        File.Exists(Path.Combine(_bed.BooksRoot, bookId.ToString(), "book.m4b")).Should().BeTrue();
    }

    [Fact]
    public async Task A_failed_replacement_keeps_the_reference()
    {
        _bed.WriteSource("book.m4b", "source audio");
        var bookId = await _bed.AddBookAsync();
        await _bed.LinkAsync(bookId, "book.m4b");

        var act = async () =>
        {
            await using var upload = new MemoryStream([1]);
            await _bed.Storage.SaveBookFileAsync(bookId, upload, "replacement.exe");
        };

        await act.Should().ThrowAsync<Exception>();
        await using var read = await _bed.Storage.OpenBookFileAsync(bookId);
        (await ReadAllAsync(read!)).Should().Be("source audio");
    }

    [Fact]
    public async Task An_owned_file_is_served_ahead_of_a_leftover_reference()
    {
        _bed.WriteSource("book.m4b", "source audio");
        var bookId = await _bed.AddBookAsync();
        await using (var upload = new MemoryStream(Encoding.ASCII.GetBytes("owned audio")))
            await _bed.Owned.SaveBookFileAsync(bookId, upload, "book.m4b");
        await _bed.LinkAsync(bookId, "book.m4b");

        await using var read = await _bed.Storage.OpenBookFileAsync(bookId);

        (await ReadAllAsync(read!)).Should().Be("owned audio");
    }

    [Fact]
    public async Task Linked_tracks_are_read_in_place_under_canonical_track_names()
    {
        _bed.WriteSource("Title/01 - One.mp3", "first");
        _bed.WriteSource("Title/02 - Two.mp3", "second");
        var bookId = await _bed.AddBookAsync();
        await _bed.LinkAsync(bookId, "Title/01 - One.mp3", trackNumber: 1);
        await _bed.LinkAsync(bookId, "Title/02 - Two.mp3", trackNumber: 2);

        var info = await _bed.Storage.GetTrackInfoAsync(bookId, 2);
        await using var read = await _bed.Storage.OpenTrackAsync(bookId, 2);

        info!.FileName.Should().Be("track-0002.mp3");
        (await ReadAllAsync(read!)).Should().Be("second");
        (await _bed.Storage.GetTrackInfoAsync(bookId, 3)).Should().BeNull();
        (await _bed.Storage.GetBookFileInfoAsync(bookId)).Should().BeNull();
    }

    [Fact]
    public async Task Removing_tracks_forgets_their_references_and_keeps_every_source()
    {
        var first = SourceSnapshot.Take(_bed.WriteSource("Title/01.mp3", "first"));
        var second = SourceSnapshot.Take(_bed.WriteSource("Title/02.mp3", "second"));
        var bookId = await _bed.AddBookAsync();
        await _bed.LinkAsync(bookId, "Title/01.mp3", trackNumber: 1);
        await _bed.LinkAsync(bookId, "Title/02.mp3", trackNumber: 2);

        await _bed.Storage.DeleteTracksAsync(bookId);

        first.IsUnchanged().Should().BeTrue();
        second.IsUnchanged().Should().BeTrue();
        (await _bed.CountLinksAsync(bookId)).Should().Be(0);
    }

    [Fact]
    public async Task Deleting_the_book_row_deletes_its_references_and_keeps_the_source()
    {
        var source = SourceSnapshot.Take(_bed.WriteSource("book.m4b", "audio"));
        var bookId = await _bed.AddBookAsync();
        await _bed.LinkAsync(bookId, "book.m4b");

        await using (var db = await _bed.Contexts.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON");
            db.Books.Remove(await db.Books.SingleAsync(b => b.Id == bookId));
            await db.SaveChangesAsync();
        }

        (await _bed.CountLinksAsync(bookId)).Should().Be(0);
        source.IsUnchanged().Should().BeTrue();
    }

    [Fact]
    public async Task One_source_file_cannot_be_linked_to_two_books()
    {
        _bed.WriteSource("book.m4b", "audio");
        var first = await _bed.AddBookAsync();
        var second = await _bed.AddBookAsync();
        await _bed.LinkAsync(first, "book.m4b");

        var act = () => _bed.LinkAsync(second, "book.m4b");

        (await act.Should().ThrowAsync<DbUpdateException>())
            .WithInnerException<SqliteException>();
    }

    [Fact]
    public async Task With_no_root_configured_a_reference_reads_nothing_but_is_still_forgotten_on_replacement()
    {
        using var bed = new ExternalMediaTestBed(configureRoot: false);
        var bookId = await bed.AddBookAsync();
        await bed.LinkAsync(bookId, "book.m4b");

        (await bed.Storage.OpenBookFileAsync(bookId)).Should().BeNull();
        await using (var upload = new MemoryStream([1, 2]))
            await bed.Storage.SaveBookFileAsync(bookId, upload, "book.m4b");

        (await bed.CountLinksAsync(bookId)).Should().Be(0);
    }

    private static async Task<string> ReadAllAsync(StoredAssetRead read)
    {
        using var reader = new StreamReader(read.Content, Encoding.ASCII);
        return await reader.ReadToEndAsync();
    }
}

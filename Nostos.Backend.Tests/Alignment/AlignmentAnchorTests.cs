using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;
using Nostos.Product.Alignment;
using Xunit;

namespace Nostos.Backend.Tests.Alignment;

public sealed class AlignmentAnchorTests
{
    [Fact]
    public void Single_file_revision_is_stable_and_detects_changes()
    {
        var now = DateTimeOffset.UtcNow;

        var first = new StoredAssetInfo(
            "book.m4b", "audio/mp4", 5000, "etag-a", now);

        AlignmentRevisions.SingleFile(first)
            .Should().Be(AlignmentRevisions.SingleFile(first));

        AlignmentRevisions.SingleFile(first)
            .Should().NotBe(AlignmentRevisions.SingleFile(
                first with { EntityTag = "etag-b" }));

        AlignmentRevisions.SingleFile(first)
            .Should().NotBe(AlignmentRevisions.SingleFile(
                first with { Length = 5001 }));

        AlignmentRevisions.SingleFile(
            first with { EntityTag = "" })
            .Should().BeNull();
    }

    [Fact]
    public void Text_revision_matches_cache_identity_inputs()
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var asset = new StoredAssetInfo(
            "book.epub",
            "application/epub+zip",
            10000,
            "text-etag",
            now);

        var revision = AlignmentRevisions.Text(id, asset);

        revision.Should().NotBeNull();
        revision.Should().Be(AlignmentRevisions.Text(id, asset));

        AlignmentRevisions.Text(
            id, asset with { Length = 10001 })
            .Should().NotBe(revision);

        AlignmentRevisions.Text(
            id, asset with { LastModified = now.AddSeconds(1) })
            .Should().NotBe(revision);

        AlignmentRevisions.Text(
            Guid.NewGuid(), asset)
            .Should().NotBe(revision);

        AlignmentRevisions.Text(
            id, asset with { EntityTag = "" })
            .Should().BeNull();
    }

    [Fact]
    public void Multi_track_revision_changes_with_every_identity_component()
    {
        var tracks = new[]
        {
            Track(1, "etag-1", 100, 5000),
            Track(2, "etag-2", 200, 6000),
        };

        var original = AlignmentRevisions.MultiTrack(tracks);

        original.Should().NotBeNull();
        AlignmentRevisions.MultiTrack(tracks).Should().Be(original);

        AlignmentRevisions.MultiTrack(
            [tracks[0] with { EntityTag = "changed" }, tracks[1]])
            .Should().NotBe(original);

        AlignmentRevisions.MultiTrack(
            [tracks[0] with { Length = 101 }, tracks[1]])
            .Should().NotBe(original);

        AlignmentRevisions.MultiTrack(
            [tracks[0] with { DurationMs = 5001 }, tracks[1]])
            .Should().NotBe(original);

        AlignmentRevisions.MultiTrack(
            [tracks[0], tracks[1] with { EntityTag = "changed" }])
            .Should().NotBe(original);

        AlignmentRevisions.MultiTrack([tracks[0]])
            .Should().NotBe(original);

        AlignmentRevisions.MultiTrack(
            [tracks[1], tracks[0]])
            .Should().BeNull();

        AlignmentRevisions.MultiTrack([])
            .Should().BeNull();
    }

    [Fact]
    public async Task Save_and_near_duplicate_update_keep_one_row()
    {
        await using var fixture = new DatabaseFixture();
        var (audio, text) = await fixture.SeedAsync();
        var store = new EfAlignmentAnchorStore(fixture.Db);

        (await store.SaveAsync(
            Candidate(audio, text, 10000, 50))).Should().BeTrue();

        (await store.SaveAsync(
            Candidate(audio, text, 11900, 80) with
            {
                Score = 0.99,
                ResourceHref = "chapter-2.xhtml",
            })).Should().BeTrue();

        var rows = await fixture.Db.AlignmentAnchors
            .AsNoTracking()
            .ToListAsync();

        rows.Should().ContainSingle();
        rows[0].AudioPositionMs.Should().Be(11900);
        rows[0].TextGlobalOffset.Should().Be(80);
        rows[0].Score.Should().Be(0.99);
        rows[0].ResourceHref.Should().Be("chapter-2.xhtml");
    }

    [Fact]
    public async Task Save_removes_stale_revisions_for_same_pair_only()
    {
        await using var fixture = new DatabaseFixture();
        var (audio, text) = await fixture.SeedAsync();
        var (_, otherText) = await fixture.SeedAsync();

        var store = new EfAlignmentAnchorStore(fixture.Db);

        await store.SaveAsync(Candidate(audio, text, 10000, 50));
        await store.SaveAsync(Candidate(audio, otherText, 10000, 50));

        await store.SaveAsync(
            Candidate(audio, text, 15000, 70) with
            {
                AudioRevision = "audio-v2",
            });

        var oldRows = await fixture.Db.AlignmentAnchors
            .AsNoTracking()
            .Where(a =>
                a.AudioBookId == audio &&
                a.TextBookId == text)
            .ToListAsync();

        oldRows.Should().ContainSingle();
        oldRows[0].AudioRevision.Should().Be("audio-v2");

        (await fixture.Db.AlignmentAnchors.CountAsync(a =>
            a.AudioBookId == audio &&
            a.TextBookId == otherText))
            .Should().Be(1);

        (await store.GetNearbyAsync(
            audio, text, "audio-v1", "text-v1",
            15000, 10000))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Lookups_use_exact_revisions_and_both_axes()
    {
        await using var fixture = new DatabaseFixture();
        var (audio, text) = await fixture.SeedAsync();

        var store = new EfAlignmentAnchorStore(fixture.Db);

        await store.SaveAsync(Candidate(audio, text, 10000, 100));
        await store.SaveAsync(Candidate(audio, text, 20000, 200));
        await store.SaveAsync(Candidate(audio, text, 30000, 300));

        var nearby = await store.GetNearbyAsync(
            audio, text, "audio-v1", "text-v1",
            20000, 15000);

        nearby.Select(a => a.AudioPositionMs)
            .Should().Equal(10000, 20000, 30000);

        (await store.AtOrBeforeTextAsync(
            audio, text, "audio-v1", "text-v1", 250))!
            .TextGlobalOffset.Should().Be(200);

        (await store.AtOrAfterTextAsync(
            audio, text, "audio-v1", "text-v1", 250))!
            .TextGlobalOffset.Should().Be(300);

        (await store.AtOrBeforeAudioAsync(
            audio, text, "audio-v1", "text-v1", 25000))!
            .AudioPositionMs.Should().Be(20000);

        (await store.AtOrAfterAudioAsync(
            audio, text, "audio-v1", "text-v1", 25000))!
            .AudioPositionMs.Should().Be(30000);

        (await store.AtOrAfterTextAsync(
            audio, text, "wrong-revision", "text-v1", 0))
            .Should().BeNull();

        (await store.AtOrBeforeAudioAsync(
            audio, text, "audio-v1", "wrong-revision", 40000))
            .Should().BeNull();
    }

    [Fact]
    public async Task Retention_keeps_at_most_two_thousand()
    {
        await using var fixture = new DatabaseFixture();
        var (audio, text) = await fixture.SeedAsync();

        var store = new EfAlignmentAnchorStore(fixture.Db);

        for (var i = 0; i < 2005; i++)
        {
            await store.SaveAsync(
                Candidate(audio, text, i * 3000L, i * 10));
        }

        var rows = await fixture.Db.AlignmentAnchors
            .AsNoTracking()
            .Where(a =>
                a.AudioBookId == audio &&
                a.TextBookId == text)
            .OrderBy(a => a.AudioPositionMs)
            .ToListAsync();

        rows.Should().HaveCount(2000);
        rows.Last().AudioPositionMs.Should().Be(2004 * 3000L);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Deleting_either_book_cascades_to_anchors(
        bool deleteAudio)
    {
        await using var fixture = new DatabaseFixture();
        var (audio, text) = await fixture.SeedAsync();

        var store = new EfAlignmentAnchorStore(fixture.Db);

        await store.SaveAsync(
            Candidate(audio, text, 10000, 100));

        // Delete by SQL so EF cannot simulate the cascade by tracking
        // or deleting dependents in memory.
        await fixture.Db.Books
            .Where(b => b.Id == (deleteAudio ? audio : text))
            .ExecuteDeleteAsync();

        (await fixture.Db.AlignmentAnchors.CountAsync())
            .Should().Be(0);
    }

    private static AlignmentRevisions.TrackIdentity Track(
        int number,
        string tag,
        long length,
        long duration) =>
        new(
            number,
            $"track-{number:D4}.mp3",
            tag,
            length,
            duration);

    private static AlignmentAnchorInput Candidate(
        Guid audio,
        Guid text,
        long position,
        int offset) =>
        new(
            audio,
            text,
            "audio-v1",
            "text-v1",
            position,
            offset,
            0,
            "chapter-1.xhtml",
            null,
            offset,
            0.95);

    private sealed class DatabaseFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection = new(
            "Data Source=:memory:");

        public NostosDbContext Db { get; }

        public DatabaseFixture()
        {
            connection.Open();

            var options = new DbContextOptionsBuilder<NostosDbContext>()
                .UseSqlite(connection)
                .Options;

            Db = new NostosDbContext(options);
            Db.Database.EnsureCreated();
        }

        public async Task<(Guid Audio, Guid Text)> SeedAsync()
        {
            var work = new WorkModel
            {
                Title = "Alignment Test Work",
                NormalizedTitle = "alignment test work",
            };

            var audio = new AudioBookModel
            {
                Title = "Audio Edition",
                Work = work,
            };

            var text = new EBookModel
            {
                Title = "EPUB Edition",
                Work = work,
            };

            Db.Books.AddRange(audio, text);
            await Db.SaveChangesAsync();

            return (audio.Id, text.Id);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}

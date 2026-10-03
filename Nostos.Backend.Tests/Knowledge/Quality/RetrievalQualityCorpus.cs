using Nostos.Backend.Data.Models;
using Nostos.Product.BookText;

namespace Nostos.Backend.Tests.Knowledge.Quality;

/// <summary>
/// Synthetic, redistributable corpus for the retrieval comparison (issue #566).
/// Every passage below is original fixture prose written for these tests: never
/// maintainer library data, never copied text. Book titles are deliberately
/// neutral because note search matches book titles — a title carrying a query
/// term would hand one side an artificial match.
/// </summary>
internal static class RetrievalQualityCorpus
{
    // Fixed base timestamp; each seed takes base + N seconds so cross-run
    // ordering never depends on wall-clock granularity or random Guids.
    public static readonly DateTime BaseTime =
        new(2026, 1, 2, 10, 0, 0, DateTimeKind.Utc);

    public const string GreenLedger = "Green Ledger";
    public const string BlueLedger = "Blue Ledger";
    public const string GrayLedger = "Gray Ledger";
    public const string RedLedger = "Red Ledger";

    // C7 — low lexical overlap. The question "reading replaces thinking" shares
    // exactly one content token ("thinking") with the gold note; the meaning
    // ("borrowed opinions doing your thinking") uses different words.
    public const string G7Text =
        "She kept borrowed opinions on every shelf and let the volumes do her thinking for her; no judgment of her own remained.";
    public const string D7Text =
        "His reading nook faces the garden; he sits there every morning.";

    // C8 — distractor retrieval. The gold note carries all three query terms;
    // each distractor shares a strict subset of the surface terms.
    public const string G8Text =
        "The harbor lighthouse held through the storm; its lamp guided every boat home.";
    public const string D8aText =
        "The harbor market sells fish beside the lighthouse postcard stand.";
    public const string D8bText =
        "A storm flooded the harbor parking lot overnight.";

    // C9/C11 — cross-book evidence with a qualification/tension. G9a states the
    // practice; G9b (other book) qualifies it. C11 follows up on the
    // qualification and must stay tied to G9b exactly.
    public const string G9aText =
        "Dawn launching saves an hour of rowing; the early tides lift every hull.";
    public const string G9bText =
        "Dawn launching fails on neap tides; the morning lift is too weak to clear the bar.";
    public const string D9Text =
        "Dawn choruses wake the vineyard before harvest.";

    // Imported book text (indexed chunks). Fillers carry none of the query
    // terms so passage metrics isolate the gold passages.
    public const string P3aText =
        "The early tides lift every hull at dawn, and launching then saves an hour of rowing.";
    public const string P3FillerText =
        "Rain kept the crew mending nets in the gray shed all afternoon.";
    public const string P4bText =
        "On neap tides the morning lift is too weak, and dawn launching cannot clear the bar.";
    public const string P4FillerText =
        "The red shed stores spare oars, rope, and tar for winter repairs.";

    public sealed record CorpusIds(
        Guid GreenBook,
        Guid BlueBook,
        Guid GrayBook,
        Guid RedBook,
        Guid G7,
        Guid D7,
        Guid G8,
        Guid D8a,
        Guid D8b,
        Guid G9a,
        Guid G9b,
        Guid D9,
        Guid BorrowedJudgment,
        Guid SafePassage,
        Guid MarketDay,
        Guid EarlyWater,
        Guid NarrowMargin,
        Guid VineyardHours);

    public static async Task<CorpusIds> SeedAsync(RetrievalQualityHarness h)
    {
        var tick = 0;
        DateTime NextTime() => BaseTime.AddSeconds(++tick);

        async Task<PhysicalBookModel> BookAsync(string title)
        {
            var book = new PhysicalBookModel
            {
                Id = Guid.NewGuid(),
                Title = title,
                Author = "Fixture Author",
            };
            h.Db.Books.Add(book);
            await h.Db.SaveChangesAsync();
            return book;
        }

        async Task<NoteModel> NoteAsync(Guid bookId, string content)
        {
            var note = new NoteModel
            {
                Id = Guid.NewGuid(),
                BookId = bookId,
                Content = content,
                SourceAnchorKind = "unknown",
                AnchorVerified = false,
                CreatedAt = NextTime(),
            };
            h.Db.Notes.Add(note);
            await h.Db.SaveChangesAsync();
            return note;
        }

        async Task<TopicModel> TopicAsync(string name, params Guid[] noteIds)
        {
            var topic = new TopicModel
            {
                Id = Guid.NewGuid(),
                Topic = name,
            };
            h.Db.Topics.Add(topic);
            foreach (var noteId in noteIds)
            {
                h.Db.NoteTopics.Add(new NoteTopicModel
                {
                    NoteId = noteId,
                    TopicId = topic.Id,
                });
            }
            await h.Db.SaveChangesAsync();
            return topic;
        }

        var green = await BookAsync(GreenLedger);
        var blue = await BookAsync(BlueLedger);
        var gray = await BookAsync(GrayLedger);
        var red = await BookAsync(RedLedger);

        // Creation order sets CreatedAt ties-breaks: distractors first so a
        // genuine score tie always resolves toward the newer gold note.
        var d7 = await NoteAsync(green.Id, D7Text);
        var g7 = await NoteAsync(green.Id, G7Text);

        var d8b = await NoteAsync(blue.Id, D8bText);
        var d8a = await NoteAsync(blue.Id, D8aText);
        var g8 = await NoteAsync(blue.Id, G8Text);

        var d9 = await NoteAsync(gray.Id, D9Text);
        var g9a = await NoteAsync(gray.Id, G9aText);
        var g9b = await NoteAsync(red.Id, G9bText);

        var borrowedJudgment = await TopicAsync("Borrowed Judgment", g7.Id);
        var safePassage = await TopicAsync("Safe Passage", g8.Id);
        var marketDay = await TopicAsync("Market Day", d8a.Id);
        var earlyWater = await TopicAsync("Early Water", g9a.Id, g9b.Id);
        var narrowMargin = await TopicAsync("Narrow Margin", g9b.Id);
        var vineyardHours = await TopicAsync("Vineyard Hours", d9.Id);

        await SeedBookTextAsync(h, gray.Id, "gray-ledger.pdf", P3aText, P3FillerText);
        await SeedBookTextAsync(h, red.Id, "red-ledger.pdf", P4FillerText, P4bText);

        return new CorpusIds(
            green.Id, blue.Id, gray.Id, red.Id,
            g7.Id, d7.Id,
            g8.Id, d8a.Id, d8b.Id,
            g9a.Id, g9b.Id, d9.Id,
            borrowedJudgment.Id, safePassage.Id, marketDay.Id,
            earlyWater.Id, narrowMargin.Id, vineyardHours.Id);
    }

    private static async Task SeedBookTextAsync(
        RetrievalQualityHarness h,
        Guid bookId,
        string fileName,
        string chunk0Text,
        string chunk1Text)
    {
        await h.BookTextIndex.ScheduleAsync(bookId, fileName, BookTextSourceFormat.Pdf);
        var work = await h.BookTextIndex.TryClaimNextAsync(TimeSpan.FromMinutes(15));
        if (work is null)
            throw new InvalidOperationException($"No ingestion work claimed for book {bookId}.");

        // Distinct revision hash per book; any 64-char string is accepted.
        var hash = new string(bookId.ToString("N")[0], 64);
        var revision = new BookTextSourceRevision(
            bookId,
            hash,
            BookTextArtifactSchema.CurrentExtractorVersion,
            BookTextSourceFormat.Pdf);

        var chunks = new[]
        {
            new BookTextIndexedChunk(
                BookTextIdentity.ChunkId(revision, 0),
                bookId,
                hash,
                revision.ExtractorVersion,
                BookTextSourceFormat.Pdf,
                0,
                chunk0Text,
                ["Ledger"],
                []),
            new BookTextIndexedChunk(
                BookTextIdentity.ChunkId(revision, 1),
                bookId,
                hash,
                revision.ExtractorVersion,
                BookTextSourceFormat.Pdf,
                1,
                chunk1Text,
                ["Ledger"],
                []),
        };

        var ok = await h.BookTextIndex.ReplaceReadyAsync(
            revision,
            chunks,
            chunks.Sum(chunk => chunk.Text.Length),
            work.Attempt);
        if (!ok)
            throw new InvalidOperationException($"ReplaceReady failed for book {bookId}.");
    }
}

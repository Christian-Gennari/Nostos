using FluentAssertions;
using Nostos.Backend.Services.Knowledge;
using Nostos.Backend.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Nostos.Backend.Tests.Knowledge.Quality;

/// <summary>
/// Deterministic retrieval comparison on predefined fixtures (issue #566):
/// the #562 multi-query lexical baseline
/// (<see cref="KnowledgeRetrievalService.SearchAsync"/> full path) against a
/// faithful reproduction of the pre-#562 single-phrase literal behaviour
/// (<see cref="LegacyLiteralRetrieval"/>). No model calls, no network, no
/// maintainer data — every number below comes from the synthetic corpus in
/// <see cref="RetrievalQualityCorpus"/>. Each test prints its machine-readable
/// report (JSON block) and writes it to a temp run dir; results are never
/// committed.
/// </summary>
public sealed class RetrievalQualityComparisonTests : IClassFixture<SqliteTestFixture>
{
    private const int MaxPerSource = 6;

    private readonly SqliteTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public RetrievalQualityComparisonTests(
        SqliteTestFixture fixture,
        ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task C7_low_overlap_is_recovered_only_by_multi_query()
    {
        var report = await MeasureAsync(RetrievalQualityCases.C7LowOverlap);

        report.MultiQuery.Notes.RecallAt6.Should().Be(1d);
        report.MultiQuery.Notes.FirstGoldRank.Should().Be(1);
        report.QueryVariants.Should().Contain("thinking");
        report.LegacyLiteral.Notes.RecallAt6.Should().Be(0d);
        report.LegacyLiteral.Notes.Order.Should().BeEmpty();

        report.MultiQuery.Topics.FirstGoldRank.Should().Be(1);
        report.LegacyLiteral.Topics.Order.Should().BeEmpty();
    }

    [Fact]
    public async Task C8_gold_outranks_lexical_distractors_only_under_multi_query()
    {
        var report = await MeasureAsync(RetrievalQualityCases.C8Distractors);

        report.MultiQuery.Notes.RecallAt1.Should().Be(1d);
        report.MultiQuery.Notes.RecallAt3.Should().Be(1d);
        report.MultiQuery.Notes.FirstGoldRank.Should().Be(1);
        report.MultiQuery.Notes.Order[0].Should().Be("G8");
        report.MultiQuery.Notes.ContaminationAt3.Should().Be(2);

        report.LegacyLiteral.Notes.RecallAt6.Should().Be(0d);
        report.LegacyLiteral.Notes.Order.Should().BeEmpty();

        report.MultiQuery.Topics.FirstGoldRank.Should().Be(1);
    }

    [Fact]
    public async Task C9_cross_book_evidence_is_retrievable_in_notes_and_passages()
    {
        var report = await MeasureAsync(RetrievalQualityCases.C9CrossBook);

        report.MultiQuery.Notes.RecallAt6.Should().Be(1d);
        report.MultiQuery.Notes.FirstGoldRank.Should().Be(1);
        report.LegacyLiteral.Notes.RecallAt6.Should().Be(0d);

        // The book-text FTS path is unchanged by #562, so both strategies
        // agree here; the assertion pins the cross-book recall itself.
        report.MultiQuery.Passages.RecallAt6.Should().Be(1d);
        report.LegacyLiteral.Passages.RecallAt6.Should().Be(1d);
        report.MultiQuery.Passages.Order.Should().HaveCount(2);

        report.MultiQuery.Topics.Order.Take(3).Should().Contain("EarlyWater");
    }

    [Fact]
    public async Task C11_follow_up_stays_tied_to_the_exact_qualifying_note_and_passage()
    {
        await using var h = await RetrievalQualityHarness.CreateAsync(_fixture);
        var ids = await RetrievalQualityCorpus.SeedAsync(h);
        var kase = RetrievalQualityCases.C11FollowUp;

        var response = await h.Knowledge.SearchAsync(
            new KnowledgeSearchRequest(kase.Query, MaxPerSource: MaxPerSource));

        var report = await MeasureAsync(kase, h, ids, response);

        report.MultiQuery.Notes.Order[0].Should().Be("G9b");
        report.MultiQuery.Notes.RecallAt6.Should().Be(1d);
        report.LegacyLiteral.Notes.Order.Should().BeEmpty();

        report.MultiQuery.Passages.Order[0].Should().Be("RedBook:1");

        // Exactness: the top handles reread to the canonical note/passage.
        var noteHandle = response.Notes[0].Handle;
        var reread = await h.Knowledge.ReadAsync(noteHandle);
        reread!.Note!.NoteId.Should().Be(ids.G9b);
        reread.Note.Content.Should().Be(RetrievalQualityCorpus.G9bText);

        var passageHandle = response.BookPassages[0].Handle;
        var repassage = await h.Knowledge.ReadAsync(passageHandle);
        repassage!.BookPassage!.Text.Should().Be(RetrievalQualityCorpus.P4bText);
        repassage.BookPassage.BookId.Should().Be(ids.RedBook);
    }

    [Fact]
    public async Task Rankings_are_deterministic_across_runs()
    {
        await using var h = await RetrievalQualityHarness.CreateAsync(_fixture);
        await RetrievalQualityCorpus.SeedAsync(h);

        foreach (var kase in RetrievalQualityCases.All)
        {
            var first = await h.Knowledge.SearchAsync(
                new KnowledgeSearchRequest(kase.Query, MaxPerSource: MaxPerSource));
            var second = await h.Knowledge.SearchAsync(
                new KnowledgeSearchRequest(kase.Query, MaxPerSource: MaxPerSource));

            first.Notes.Select(note => note.NoteId)
                .Should().Equal(second.Notes.Select(note => note.NoteId));
            first.Topics.Select(topic => topic.TopicId)
                .Should().Equal(second.Topics.Select(topic => topic.TopicId));
            first.BookPassages.Select(passage => passage.Handle)
                .Should().Equal(second.BookPassages.Select(passage => passage.Handle));
        }
    }

    private async Task<RetrievalQualityMetrics.CaseReport> MeasureAsync(
        RetrievalQualityCase kase)
    {
        await using var h = await RetrievalQualityHarness.CreateAsync(_fixture);
        var ids = await RetrievalQualityCorpus.SeedAsync(h);
        var response = await h.Knowledge.SearchAsync(
            new KnowledgeSearchRequest(kase.Query, MaxPerSource: MaxPerSource));
        return await MeasureAsync(kase, h, ids, response);
    }

    private async Task<RetrievalQualityMetrics.CaseReport> MeasureAsync(
        RetrievalQualityCase kase,
        RetrievalQualityHarness h,
        RetrievalQualityCorpus.CorpusIds ids,
        KnowledgeSearchResponse response)
    {
        var legacy = await LegacyLiteralRetrieval.SearchAsync(h, kase.Query, MaxPerSource);

        var noteLabels = new Dictionary<Guid, string>
        {
            [ids.G7] = "G7",
            [ids.D7] = "D7",
            [ids.G8] = "G8",
            [ids.D8a] = "D8a",
            [ids.D8b] = "D8b",
            [ids.G9a] = "G9a",
            [ids.G9b] = "G9b",
            [ids.D9] = "D9",
        };
        var topicLabels = new Dictionary<Guid, string>
        {
            [ids.BorrowedJudgment] = "BorrowedJudgment",
            [ids.SafePassage] = "SafePassage",
            [ids.MarketDay] = "MarketDay",
            [ids.EarlyWater] = "EarlyWater",
            [ids.NarrowMargin] = "NarrowMargin",
            [ids.VineyardHours] = "VineyardHours",
        };

        string NoteLabel(Guid id) => noteLabels.TryGetValue(id, out var label)
            ? label
            : $"other:{id:N}"[..14];
        string TopicLabel(Guid id) => topicLabels.TryGetValue(id, out var label)
            ? label
            : $"other:{id:N}"[..14];
        string BookLabel(Guid bookId) =>
            bookId == ids.GrayBook ? "GrayBook"
            : bookId == ids.RedBook ? "RedBook"
            : $"other:{bookId:N}"[..14];

        var goldNotes = kase.GoldNoteKeys
            .Select(key => noteLabels[RetrievalQualityCases.NoteKey(ids, key)])
            .ToHashSet();
        var distractorNotes = kase.DistractorNoteKeys
            .Select(key => noteLabels[RetrievalQualityCases.NoteKey(ids, key)])
            .ToHashSet();

        var goldTopics = kase.GoldTopicKey is null
            ? new HashSet<string>()
            : new HashSet<string>
            {
                topicLabels[RetrievalQualityCases.TopicKey(ids, kase.GoldTopicKey)],
            };
        var distractorTopics = topicLabels.Values
            .Where(label => !goldTopics.Contains(label))
            .ToHashSet();

        var goldPassages = kase.GoldPassages
            .Select(passage => $"{passage.BookKey}:{passage.Ordinal}")
            .ToHashSet();
        var allPassages = new HashSet<string>
        {
            $"GrayBook:0", $"GrayBook:1", $"RedBook:0", $"RedBook:1",
        };
        var distractorPassages = allPassages
            .Where(key => !goldPassages.Contains(key))
            .ToHashSet();

        var multiNotes = RetrievalQualityMetrics.Measure(
            response.Notes.ToList(), note => NoteLabel(note.NoteId), goldNotes, distractorNotes);
        var multiTopics = RetrievalQualityMetrics.Measure(
            response.Topics.ToList(), topic => TopicLabel(topic.TopicId), goldTopics, distractorTopics);
        var multiPassages = RetrievalQualityMetrics.Measure(
            response.BookPassages.ToList(),
            passage => $"{BookLabel(passage.BookId)}:{passage.Ordinal}",
            goldPassages,
            distractorPassages);

        var legacyNotes = RetrievalQualityMetrics.Measure(
            legacy.NoteIds.ToList(), NoteLabel, goldNotes, distractorNotes);
        var legacyTopics = RetrievalQualityMetrics.Measure(
            legacy.TopicIds.ToList(), TopicLabel, goldTopics, distractorTopics);
        var legacyPassages = RetrievalQualityMetrics.Measure(
            legacy.Passages.ToList(),
            passage => $"{BookLabel(passage.BookId)}:{passage.Ordinal}",
            goldPassages,
            distractorPassages);

        var report = new RetrievalQualityMetrics.CaseReport(
            kase.Id,
            kase.Query,
            kase.Description,
            response.QueryVariants,
            new RetrievalQualityMetrics.StrategyReport("multi-query-562", multiNotes, multiTopics, multiPassages),
            new RetrievalQualityMetrics.StrategyReport("legacy-literal", legacyNotes, legacyTopics, legacyPassages));

        var path = RetrievalQualityMetrics.WriteRunFile(report);
        _output.WriteLine($"[{kase.Id}] report written to {path}");
        _output.WriteLine(RetrievalQualityMetrics.ToJson(report));
        return report;
    }
}

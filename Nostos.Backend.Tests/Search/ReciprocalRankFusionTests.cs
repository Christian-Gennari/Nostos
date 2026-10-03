using FluentAssertions;
using Nostos.Backend.Search;
using Xunit;

namespace Nostos.Backend.Tests.Search;

public sealed class ReciprocalRankFusionTests
{
    [Fact]
    public void Scores_are_the_sum_of_weight_over_k_plus_rank()
    {
        var fused = ReciprocalRankFusion.Fuse<string>(
        [
            new(["a", "b", "c"]),
            new(["c", "a"]),
        ]);

        fused.Select(item => item.Key).Should().Equal("a", "c", "b");
        fused[0].Score.Should().BeApproximately(1d / 61 + 1d / 62, 1e-12);
        fused[1].Score.Should().BeApproximately(1d / 63 + 1d / 61, 1e-12);
        fused[2].Score.Should().BeApproximately(1d / 62, 1e-12);
    }

    [Fact]
    public void A_candidate_found_by_both_lists_outranks_each_lists_single_channel_leader()
    {
        var fused = ReciprocalRankFusion.Fuse<string>(
        [
            new(["lexical-only", "shared"]),
            new(["vector-only", "shared"]),
        ]);

        fused[0].Key.Should().Be("shared");
        fused[0].Score.Should().BeApproximately(2d / 62, 1e-12);
    }

    [Fact]
    public void Ties_keep_first_seen_order_so_the_earlier_list_wins()
    {
        var fused = ReciprocalRankFusion.Fuse<string>(
        [
            new(["lexical-1", "lexical-2"]),
            new(["vector-1", "vector-2"]),
        ]);

        fused.Select(item => item.Key)
            .Should().Equal("lexical-1", "vector-1", "lexical-2", "vector-2");
    }

    [Fact]
    public void Weight_scales_a_lists_contribution()
    {
        var fused = ReciprocalRankFusion.Fuse<string>(
        [
            new(["a"]),
            new(["b"], Weight: 2d),
        ]);

        fused.Select(item => item.Key).Should().Equal("b", "a");
        fused[0].Score.Should().BeApproximately(2d / 61, 1e-12);
    }

    [Fact]
    public void K_is_configurable()
    {
        var fused = ReciprocalRankFusion.Fuse<string>([new(["a", "b"])], k: 0);

        fused[0].Score.Should().BeApproximately(1d, 1e-12);
        fused[1].Score.Should().BeApproximately(0.5d, 1e-12);
    }

    [Fact]
    public void A_key_repeated_within_one_list_counts_once_at_its_best_rank()
    {
        var fused = ReciprocalRankFusion.Fuse<string>([new(["a", "a", "b"])]);

        fused.Should().HaveCount(2);
        fused[0].Score.Should().BeApproximately(1d / 61, 1e-12);
        // The duplicate does not consume a rank.
        fused[1].Score.Should().BeApproximately(1d / 62, 1e-12);
    }

    [Fact]
    public void Custom_comparer_decides_key_identity()
    {
        var fused = ReciprocalRankFusion.Fuse<string>(
            [new(["Chunk"]), new(["chunk"])],
            comparer: StringComparer.OrdinalIgnoreCase);

        fused.Should().ContainSingle();
        fused[0].Score.Should().BeApproximately(2d / 61, 1e-12);
    }

    [Fact]
    public void Empty_input_and_non_positive_weights_contribute_nothing()
    {
        ReciprocalRankFusion.Fuse<string>([]).Should().BeEmpty();
        ReciprocalRankFusion.Fuse<string>([new([]), new(["a"], Weight: 0d)]).Should().BeEmpty();
    }

    [Fact]
    public void Negative_k_is_rejected()
    {
        var act = () => ReciprocalRankFusion.Fuse<string>([new(["a"])], k: -1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}

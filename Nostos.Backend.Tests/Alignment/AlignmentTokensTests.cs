using FluentAssertions;
using Nostos.Product.Alignment;
using Xunit;

namespace Nostos.Backend.Tests.Alignment;

public sealed class AlignmentTokensTests
{
    private static string[] Values(string text) =>
        AlignmentTokens.Split(text).Select(token => token.Value).ToArray();

    [Fact]
    public void Words_are_lower_cased_runs_of_letters_and_digits()
    {
        Values("“Bah!” said Scrooge, in 1843 — Humbug.")
            .Should().Equal("bah", "said", "scrooge", "in", "1843", "humbug");
    }

    [Fact]
    public void Tokens_keep_their_position_in_the_source()
    {
        var tokens = AlignmentTokens.Split("  Old Marley");

        tokens.Should().Equal(
            new AlignmentToken("old", 2, 3),
            new AlignmentToken("marley", 6, 6));
    }

    [Theory]
    [InlineData("我要给阿Q做正传", "我", "要", "给", "阿", "q", "做", "正", "传")]
    [InlineData("吾輩は猫である。", "吾", "辈", "は", "猫", "で", "あ", "る")]
    [InlineData("สวัสดี", "ส", "ว", "ส", "ด")]
    public void Every_character_of_an_unspaced_script_is_a_token(
        string text,
        params string[] expected)
    {
        Values(text).Should().Equal(expected);
    }

    [Fact]
    public void Traditional_and_simplified_characters_compare_equal()
    {
        // The same sentence as printed in Taipei and as transcribed in Beijing.
        Values("這少年便是閏土。我認識他時，也不過十多歲")
            .Should().Equal(Values("这少年便是闰土 我认识他时也不过十多岁"));
    }

    [Fact]
    public void A_latin_word_inside_unspaced_text_stays_one_token()
    {
        Values("阿Quei的故事").Should().Equal("阿", "quei", "的", "故", "事");
    }

    [Fact]
    public void Unspaced_text_is_recognised_by_its_majority()
    {
        AlignmentTokens.MostlyUnspaced(AlignmentTokens.Split("我要给阿Q做正传")).Should().BeTrue();
        AlignmentTokens.MostlyUnspaced(AlignmentTokens.Split("Lu Xun wrote 阿Q in 1921")).Should().BeFalse();
        AlignmentTokens.MostlyUnspaced([]).Should().BeFalse();

        MatcherSettings.For(AlignmentTokens.Split("我要给阿Q做正传"))
            .Should().Be(new MatcherSettings(
                MinimumTokens: 12,
                MaximumTokens: 48,
                RelaxedScore: 0.68,
                RelaxedMargin: 0.25));
        MatcherSettings.For(AlignmentTokens.Split("Marley was dead"))
            .Should().Be(new MatcherSettings());
    }
}

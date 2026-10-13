using FluentAssertions;
using Nostos.Product.Services.Ai;
using Xunit;

namespace Nostos.Backend.Tests.Services.Ai;

public sealed class ReadAloudVoiceSelectionTests
{
    [Theory]
    [InlineData("en-GB", "bf_emma")]
    [InlineData("en_gb", "bf_emma")]
    [InlineData("en-US", "af_heart")]
    [InlineData("en", "af_heart")]
    [InlineData("en-AU", "af_heart")]
    [InlineData("it-IT", "if_sara")]
    [InlineData("pt", "pf_dora")]
    [InlineData("zh-Hans", "zf_xiaoxiao")]
    public void The_books_language_picks_a_voice_that_reads_it(string language, string expected)
    {
        ReadAloudVoiceSelection.ForLanguage(KokoroVoices.All, language)[0].Id.Should().Be(expected);
    }

    [Theory]
    [InlineData("sv")]
    [InlineData("de-DE")]
    [InlineData("")]
    [InlineData(null)]
    public void A_language_no_voice_reads_gets_none_rather_than_a_wrong_one(string? language)
    {
        ReadAloudVoiceSelection.ForLanguage(KokoroVoices.All, language).Should().BeEmpty();
    }

    [Fact]
    public void A_regions_own_voices_are_offered_alone_and_a_bare_language_gets_all_of_them()
    {
        ReadAloudVoiceSelection.ForLanguage(KokoroVoices.All, "en-GB")
            .Should().OnlyContain(voice => voice.Language == "en-GB");
        ReadAloudVoiceSelection.ForLanguage(KokoroVoices.All, "en")
            .Select(voice => voice.Language).Should().Contain(["en-US", "en-GB"]);
    }

    [Fact]
    public void Every_voice_id_is_unique()
    {
        KokoroVoices.All.Select(voice => voice.Id).Should().OnlyHaveUniqueItems();
    }
}

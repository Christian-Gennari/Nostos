using FluentAssertions;
using Nostos.Backend.Services.Stylos;
using Xunit;

namespace Nostos.Backend.Tests.Services;

/// <summary>
/// The rules ported from <c>stylos-protocol</c>: the path validation is a
/// security boundary (a device writes through it), and the pairing alphabet is
/// the format a person reads aloud.
/// </summary>
public sealed class StylosProtocolHelpersTests
{
    [Theory]
    [InlineData("note.md")]
    [InlineData("notes/2026/november.md")]
    [InlineData("with space/file name.md")]
    [InlineData("Ünïcode/groß.md")]
    [InlineData("a.b/c.md")]
    public void Accepts_protocol_paths(string path) =>
        StylosPaths.Validate(path).Should().BeNull();

    [Theory]
    [InlineData("", "path is empty")]
    [InlineData("/note.md", "path must be relative")]
    [InlineData("../note.md", "path contains a '..' component")]
    [InlineData("dir/../note.md", "path contains a '..' component")]
    [InlineData("dir/./note.md", "path contains a '.' component")]
    [InlineData("dir//note.md", "path contains an empty component")]
    [InlineData("dir/.hidden/note.md", "path contains a hidden component")]
    [InlineData(".stylos/note.md", "path contains a hidden component")]
    [InlineData("dir\\note.md", "path must use '/' separators")]
    [InlineData("note.txt", "path must end in '.md'")]
    [InlineData("note.md/", "path must end in '.md'")]
    public void Rejects_unsafe_paths(string path, string error) =>
        StylosPaths.Validate(path).Should().Be(error);

    [Fact]
    public void A_path_over_the_byte_limit_is_refused()
    {
        StylosPaths.Validate(new string('a', StylosPaths.MaxPathBytes + 1))
            .Should().Be("path is too long");
    }

    [Fact]
    public void Split_gives_the_folder_chain_and_the_document_name()
    {
        var (folders, name) = StylosPaths.Split("notes/2026/november.md");
        folders.Should().Equal("notes", "2026");
        name.Should().Be("november");
    }

    [Fact]
    public void Studio_names_become_components_that_always_validate()
    {
        StylosPaths.Component("a/b\\c").Should().Be("a-b-c");
        StylosPaths.Component("   ").Should().Be("Untitled");
        StylosPaths.Component(".hidden").Should().Be("_hidden");

        var path = StylosPaths.ForDocument(["Projects", ".draft"], ".notes");
        StylosPaths.IsValid(path).Should().BeTrue(path);
        path.Should().Be("Projects/_draft/_notes.md");
    }

    [Fact]
    public void Two_drafts_with_one_name_never_share_a_path()
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var first = StylosPaths.Unique("note.md", taken);
        var second = StylosPaths.Unique("note.md", taken);
        var third = StylosPaths.Unique("note.md", taken);

        first.Should().Be("note.md");
        second.Should().Be("note (2).md");
        third.Should().Be("note (3).md");
    }

    [Fact]
    public void A_very_long_name_still_yields_a_valid_path()
    {
        var path = StylosPaths.ForDocument(["d"], new string('x', 4000));
        path.Should().EndWith(".md");
        StylosPaths.IsValid(path).Should().BeTrue();
    }

    // ------------------------------------------------------------------
    // Pairing codes
    // ------------------------------------------------------------------

    [Fact]
    public void A_code_is_two_groups_of_three_unambiguous_symbols()
    {
        var code = StylosPairingCodes.FromBytes([0, 1, 2, 3, 4, 5]);
        code.Should().HaveLength(7).And.MatchRegex("^[23456789ABCDEFGHJKMNPQRSTVWXYZ]{3}-[23456789ABCDEFGHJKMNPQRSTVWXYZ]{3}$");
        code[3].Should().Be('-');
        StylosPairingCodes.FromBytes([0, 1, 2, 3, 4, 5]).Should().Be(code);

        StylosPairingCodes.Generate().Should().HaveLength(7);
    }

    [Theory]
    [InlineData("K7Q-4MZ", "K7Q-4MZ")]
    [InlineData("k7q 4mz", "K7Q-4MZ")]
    [InlineData("k7q4mz", "K7Q-4MZ")]
    [InlineData("K7Q-4MZ ", "K7Q-4MZ")]
    [InlineData("0ABC23", null)]
    [InlineData("1ABC23", null)]
    [InlineData("IABC23", null)]
    [InlineData("LABC23", null)]
    [InlineData("OABC23", null)]
    [InlineData("UABC23", null)]
    [InlineData("ABC", null)]
    [InlineData("ABCDEFG", null)]
    [InlineData("", null)]
    public void Codes_normalize_ignoring_case_dash_and_spaces(string input, string? expected) =>
        StylosPairingCodes.Normalize(input).Should().Be(expected);

    [Fact]
    public void Code_matching_never_matches_a_refused_input()
    {
        StylosPairingCodes.Matches("K7Q-4MZ", "k7q4mz").Should().BeTrue();
        StylosPairingCodes.Matches("K7Q-4MZ", "K7Q-4MY").Should().BeFalse();
        StylosPairingCodes.Matches("nope", "K7Q-4MZ").Should().BeFalse();
    }

    [Fact]
    public void Tokens_are_long_random_looking_and_only_a_hash_is_kept()
    {
        var token = StylosTokens.Create();
        token.Should().StartWith("stl_").And.HaveLength(StylosTokens.TokenLength);
        StylosTokens.LooksLikeToken(token).Should().BeTrue();
        StylosTokens.LooksLikeToken(token + "x").Should().BeFalse();
        StylosTokens.LooksLikeToken("nst_123").Should().BeFalse();

        var hash = StylosTokens.Hash(token);
        hash.Should().HaveLength(64).And.NotContain(token);
        StylosTokens.HashEquals(hash, StylosTokens.Hash(token)).Should().BeTrue();
        StylosTokens.HashEquals(hash, StylosTokens.Hash(token + "x")).Should().BeFalse();
    }

    [Fact]
    public void The_content_hash_is_the_protocols_sha256_of_utf8_bytes()
    {
        StylosTokens.ContentHash(string.Empty).Should()
            .Be("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
        StylosTokens.ContentHash("hello\n").Should()
            .Be("5891b5b522d5df086d0ff0b110fbd9d21bb4fc7163af34d08286a2e846f6be03");
    }
}

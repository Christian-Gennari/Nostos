using FluentAssertions;
using Nostos.Backend.Services.Carrel;
using Xunit;

namespace Nostos.Backend.Tests.Services;

/// <summary>
/// The rules ported from <c>carrel-protocol</c>: the path validation is a
/// security boundary (a device writes through it), and the pairing alphabet is
/// the format a person reads aloud.
/// </summary>
public sealed class CarrelProtocolHelpersTests
{
    [Theory]
    [InlineData("note.md")]
    [InlineData("notes/2026/november.md")]
    [InlineData("with space/file name.md")]
    [InlineData("Ünïcode/groß.md")]
    [InlineData("a.b/c.md")]
    public void Accepts_protocol_paths(string path) =>
        CarrelPaths.Validate(path).Should().BeNull();

    [Theory]
    [InlineData("", "path is empty")]
    [InlineData("/note.md", "path must be relative")]
    [InlineData("../note.md", "path contains a '..' component")]
    [InlineData("dir/../note.md", "path contains a '..' component")]
    [InlineData("dir/./note.md", "path contains a '.' component")]
    [InlineData("dir//note.md", "path contains an empty component")]
    [InlineData("dir/.hidden/note.md", "path contains a hidden component")]
    [InlineData(".carrel/note.md", "path contains a hidden component")]
    [InlineData("dir\\note.md", "path must use '/' separators")]
    [InlineData("note.txt", "path must end in '.md'")]
    [InlineData("note.md/", "path must end in '.md'")]
    public void Rejects_unsafe_paths(string path, string error) =>
        CarrelPaths.Validate(path).Should().Be(error);

    [Fact]
    public void A_path_over_the_byte_limit_is_refused()
    {
        CarrelPaths.Validate(new string('a', CarrelPaths.MaxPathBytes + 1))
            .Should().Be("path is too long");
    }

    [Fact]
    public void Split_gives_the_folder_chain_and_the_document_name()
    {
        var (folders, name) = CarrelPaths.Split("notes/2026/november.md");
        folders.Should().Equal("notes", "2026");
        name.Should().Be("november");
    }

    [Fact]
    public void Studio_names_become_components_that_always_validate()
    {
        CarrelPaths.Component("a/b\\c").Should().Be("a-b-c");
        CarrelPaths.Component("   ").Should().Be("Untitled");
        CarrelPaths.Component(".hidden").Should().Be("_hidden");

        var path = CarrelPaths.ForDocument(["Projects", ".draft"], ".notes");
        CarrelPaths.IsValid(path).Should().BeTrue(path);
        path.Should().Be("Projects/_draft/_notes.md");
    }

    [Fact]
    public void Two_drafts_with_one_name_never_share_a_path()
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var first = CarrelPaths.Unique("note.md", taken);
        var second = CarrelPaths.Unique("note.md", taken);
        var third = CarrelPaths.Unique("note.md", taken);

        first.Should().Be("note.md");
        second.Should().Be("note (2).md");
        third.Should().Be("note (3).md");
    }

    [Fact]
    public void A_very_long_name_still_yields_a_valid_path()
    {
        var path = CarrelPaths.ForDocument(["d"], new string('x', 4000));
        path.Should().EndWith(".md");
        CarrelPaths.IsValid(path).Should().BeTrue();
    }

    // ------------------------------------------------------------------
    // Pairing codes
    // ------------------------------------------------------------------

    [Fact]
    public void A_code_is_two_groups_of_three_unambiguous_symbols()
    {
        var code = CarrelPairingCodes.FromBytes([0, 1, 2, 3, 4, 5]);
        code.Should().HaveLength(7).And.MatchRegex("^[23456789ABCDEFGHJKMNPQRSTVWXYZ]{3}-[23456789ABCDEFGHJKMNPQRSTVWXYZ]{3}$");
        code[3].Should().Be('-');
        CarrelPairingCodes.FromBytes([0, 1, 2, 3, 4, 5]).Should().Be(code);

        CarrelPairingCodes.Generate().Should().HaveLength(7);
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
        CarrelPairingCodes.Normalize(input).Should().Be(expected);

    [Fact]
    public void Code_matching_never_matches_a_refused_input()
    {
        CarrelPairingCodes.Matches("K7Q-4MZ", "k7q4mz").Should().BeTrue();
        CarrelPairingCodes.Matches("K7Q-4MZ", "K7Q-4MY").Should().BeFalse();
        CarrelPairingCodes.Matches("nope", "K7Q-4MZ").Should().BeFalse();
    }

    [Fact]
    public void Tokens_are_long_random_looking_and_only_a_hash_is_kept()
    {
        var token = CarrelTokens.Create();
        token.Should().StartWith("crl_").And.HaveLength(CarrelTokens.TokenLength);
        CarrelTokens.LooksLikeToken(token).Should().BeTrue();
        CarrelTokens.LooksLikeToken(token + "x").Should().BeFalse();
        CarrelTokens.LooksLikeToken("nst_123").Should().BeFalse();

        // A device paired before the rename still presents an stl_ token.
        var legacy = CarrelTokens.LegacyPrefix + new string('A', 43);
        CarrelTokens.LooksLikeToken(legacy).Should().BeTrue();
        CarrelTokens.LooksLikeToken("stl_short").Should().BeFalse();

        var hash = CarrelTokens.Hash(token);
        hash.Should().HaveLength(64).And.NotContain(token);
        CarrelTokens.HashEquals(hash, CarrelTokens.Hash(token)).Should().BeTrue();
        CarrelTokens.HashEquals(hash, CarrelTokens.Hash(token + "x")).Should().BeFalse();
    }

    [Fact]
    public void A_citation_escapes_the_markdown_a_title_could_be_read_as()
    {
        // The same escaper the Studio's Markdown export uses, so a citation a
        // device shows reads the same as the export's.
        CarrelLibrarySnapshotBuilder
            .CitationMarkdown("A*B", "C[d]|e", "Page 4", null)
            .Should().Be("A\\*B, *C\\[d\\]\\|e*, p. 4");
    }

    [Fact]
    public void The_content_hash_is_the_protocols_sha256_of_utf8_bytes()
    {
        CarrelTokens.ContentHash(string.Empty).Should()
            .Be("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
        CarrelTokens.ContentHash("hello\n").Should()
            .Be("5891b5b522d5df086d0ff0b110fbd9d21bb4fc7163af34d08286a2e846f6be03");
    }
}

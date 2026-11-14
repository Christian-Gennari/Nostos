using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Nostos.Backend.Services.Carrel;

/// <summary>
/// Device tokens and file hashes. A token is <c>crl_</c> plus 256 random bits
/// in unpadded base64url; the server keeps only its lowercase hex SHA-256, so a
/// database read can never hand out a working credential. A file's hash is the
/// protocol's content hash: SHA-256 of its UTF-8 bytes, lowercase hex.
///
/// <para>Tokens issued before the Stylos → Carrel rename carry
/// <see cref="LegacyPrefix"/> instead. Devices paired then still hold one, so
/// both prefixes authenticate; only <see cref="Prefix"/> is issued now.</para>
/// </summary>
public static class CarrelTokens
{
    /// <summary>The prefix of every token issued now.</summary>
    public const string Prefix = "crl_";

    /// <summary>
    /// The prefix of tokens issued before the rename (issue #1588). Accepted so
    /// a paired device never has to pair again; never issued.
    /// </summary>
    public const string LegacyPrefix = "stl_";

    /// <summary>Both prefixes are four characters, so a token's length does not depend on which one it has.</summary>
    public const int PrefixLength = 4;

    /// <summary><see cref="Prefix"/> plus 32 bytes in unpadded base64url.</summary>
    public const int TokenLength = PrefixLength + 43;

    public static string Create() => Prefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>Whether <paramref name="token"/> has the shape of a token issued now or before the rename.</summary>
    public static bool LooksLikeToken(string? token) =>
        token is { Length: TokenLength }
        && HasKnownPrefix(token)
        && token.AsSpan(PrefixLength).IndexOfAnyExcept(Base64UrlAlphabet) < 0;

    private static bool HasKnownPrefix(string token) =>
        token.StartsWith(Prefix, StringComparison.Ordinal)
        || token.StartsWith(LegacyPrefix, StringComparison.Ordinal);

    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>The protocol's content hash: lowercase hex SHA-256 of the UTF-8 bytes.</summary>
    public static string ContentHash(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    /// <summary>Constant-time comparison of a stored hash with a freshly computed one.</summary>
    public static bool HashEquals(string stored, string computed) =>
        stored.Length == computed.Length
        && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(stored),
            Encoding.ASCII.GetBytes(computed));

    private static readonly System.Buffers.SearchValues<char> Base64UrlAlphabet =
        System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_");
}

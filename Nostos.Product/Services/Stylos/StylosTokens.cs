using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Nostos.Backend.Services.Stylos;

/// <summary>
/// Device tokens and file hashes. A token is <c>stl_</c> plus 256 random bits
/// in unpadded base64url; the server keeps only its lowercase hex SHA-256, so a
/// database read can never hand out a working credential. A file's hash is the
/// protocol's content hash: SHA-256 of its UTF-8 bytes, lowercase hex.
/// </summary>
public static class StylosTokens
{
    public const string Prefix = "stl_";

    /// <summary><see cref="Prefix"/> plus 32 bytes in unpadded base64url.</summary>
    public const int TokenLength = 4 + 43;

    public static string Create() => Prefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static bool LooksLikeToken(string? token) =>
        token is { Length: TokenLength }
        && token.StartsWith(Prefix, StringComparison.Ordinal)
        && token.AsSpan(Prefix.Length).IndexOfAnyExcept(Base64UrlAlphabet) < 0;

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

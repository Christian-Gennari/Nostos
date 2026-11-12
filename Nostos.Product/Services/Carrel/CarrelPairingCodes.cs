using System.Security.Cryptography;

namespace Nostos.Backend.Services.Carrel;

/// <summary>
/// Pairing codes, ported from <c>carrel-protocol</c>'s <c>pairing.rs</c>: six
/// symbols from an alphabet without <c>0</c>, <c>1</c>, <c>I</c>, <c>L</c>,
/// <c>O</c> or <c>U</c>, shown as <c>K7Q-4MZ</c>. The person reads the code on
/// the device and types it on a phone, so the format is chosen for reading
/// aloud, and matching ignores case, spaces and the dash.
/// </summary>
public static class CarrelPairingCodes
{
    public const string Alphabet = "23456789ABCDEFGHJKMNPQRSTVWXYZ";

    public const int SymbolCount = 6;

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>A fresh code from a cryptographic source.</summary>
    public static string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(SymbolCount);
        return FromBytes(bytes);
    }

    /// <summary>The crate's <c>pairing_code_from_bytes</c>: one symbol per byte, modulo the alphabet.</summary>
    public static string FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < SymbolCount)
            throw new ArgumentException("A pairing code needs six bytes.", nameof(bytes));

        Span<char> symbols = stackalloc char[SymbolCount];
        for (var i = 0; i < SymbolCount; i++)
            symbols[i] = Alphabet[bytes[i] % Alphabet.Length];
        return $"{new string(symbols[..3])}-{new string(symbols[3..])}";
    }

    /// <summary>
    /// <c>k7q 4mz</c>, <c>K7Q4MZ</c> and <c>K7Q-4MZ</c> all become
    /// <c>K7Q-4MZ</c>; anything with a symbol outside the alphabet, or the
    /// wrong number of symbols, is null.
    /// </summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return null;

        Span<char> symbols = stackalloc char[SymbolCount];
        var count = 0;
        foreach (var raw in input)
        {
            if (raw is '-' or ' ')
                continue;
            var upper = char.ToUpperInvariant(raw);
            if (!Alphabet.Contains(upper, StringComparison.Ordinal))
                return null;
            if (count == SymbolCount)
                return null;
            symbols[count++] = upper;
        }

        return count == SymbolCount
            ? $"{new string(symbols[..3])}-{new string(symbols[3..])}"
            : null;
    }

    public static bool Matches(string? left, string? right)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        return a is not null && a == b;
    }
}

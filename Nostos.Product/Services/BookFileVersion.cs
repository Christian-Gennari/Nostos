using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Nostos.Backend.Services;

/// <summary>
/// The opaque identity of the bytes a book's file holds, for a client that keeps
/// a copy. Derived from storage metadata, so it needs no row of its own and no
/// write path can forget to move it. The modified time is part of it so a file
/// put back later is a new version, not a match for a copy of the old one.
/// </summary>
public static class BookFileVersion
{
    public static string From(StoredAssetInfo info)
    {
        var identity = string.Join(
            "\n",
            info.EntityTag,
            info.Length.ToString(CultureInfo.InvariantCulture),
            info.LastModified.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..32];
    }
}

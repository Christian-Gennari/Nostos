using System.Security.Cryptography;
using System.Text;

namespace Nostos.Product.BookText;

public static class BookTextIdentity
{
    public static Guid ChunkId(BookTextSourceRevision revision, int ordinal)
    {
        var input = Encoding.UTF8.GetBytes(
            $"{revision.BookId:D}\0{revision.SourceSha256}\0{revision.ExtractorVersion}\0{ordinal}");
        var hash = SHA256.HashData(input);
        return new Guid(hash.AsSpan(0, 16));
    }
}

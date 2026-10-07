using Xunit;

namespace Nostos.Backend.Tests.Portability;

public sealed class PortableArchiveTestSupportTests
{
    [Fact]
    public void CopyDirectory_throws_when_the_source_directory_is_missing()
    {
        var source = Path.Combine(Path.GetTempPath(), $"nostos-missing-source-{Guid.NewGuid():N}");
        var target = Path.Combine(Path.GetTempPath(), $"nostos-copy-target-{Guid.NewGuid():N}");

        try
        {
            Assert.Throws<DirectoryNotFoundException>(
                () => PortableArchiveTestSupport.CopyDirectory(source, target));
        }
        finally
        {
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
        }
    }
}

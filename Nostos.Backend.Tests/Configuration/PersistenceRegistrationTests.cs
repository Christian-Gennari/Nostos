using FluentAssertions;
using Nostos.Backend.Configuration;
using Xunit;

namespace Nostos.Backend.Tests.Configuration;

public sealed class PersistenceRegistrationTests
{
    [Fact]
    public void ResolveDatabasePath_WhenUnset_PreservesHistoricalContentRootLocation()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "nostos-persistence-tests", "app");

        var actual = PersistenceRegistration.ResolveDatabasePath(null, contentRoot);

        actual.Should().Be(Path.GetFullPath(Path.Combine(contentRoot, "nostos.db")));
    }

    [Fact]
    public void ResolveDatabasePath_WhenRelative_ResolvesFromContentRoot()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "nostos-persistence-tests", "app");

        var actual = PersistenceRegistration.ResolveDatabasePath(
            Path.Combine("data", "library.db"),
            contentRoot);

        actual.Should().Be(Path.GetFullPath(Path.Combine(contentRoot, "data", "library.db")));
    }

    [Fact]
    public void ResolveDatabasePath_WhenAbsolute_LeavesPersistenceOutsideContentRoot()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "nostos-persistence-tests", "app");
        var configured = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "nostos-persistence-tests", "volume", "nostos.db"));

        var actual = PersistenceRegistration.ResolveDatabasePath(configured, contentRoot);

        actual.Should().Be(configured);
    }
}

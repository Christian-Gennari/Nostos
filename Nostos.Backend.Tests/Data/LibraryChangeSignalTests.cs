using FluentAssertions;
using Microsoft.Data.Sqlite;
using Nostos.Backend.Data;
using Xunit;

namespace Nostos.Backend.Tests.Data;

public sealed class LibraryChangeSignalTests
{
    [Fact]
    public async Task A_change_wakes_only_the_feeds_of_its_own_database()
    {
        var mine = $"mine-{Guid.NewGuid():N}";
        var other = $"other-{Guid.NewGuid():N}";
        var waitingOnMine = LibraryChangeSignal.NextAsync(mine);
        var waitingOnOther = LibraryChangeSignal.NextAsync(other);

        LibraryChangeSignal.Publish(mine, new LibraryChange(ProgressOnly: true));

        (await waitingOnMine.WaitAsync(TimeSpan.FromSeconds(5))).ProgressOnly.Should().BeTrue();
        waitingOnOther.IsCompleted.Should().BeFalse("another account's database must not wake this feed");

        // A wait taken after the change waits for the next one.
        LibraryChangeSignal.NextAsync(mine).IsCompleted.Should().BeFalse();
    }

    [Fact]
    public void The_key_names_the_database_whether_or_not_the_connection_is_open()
    {
        var path = Path.Combine(Path.GetTempPath(), $"signal-{Guid.NewGuid():N}.db");
        try
        {
            using var connection = new SqliteConnection($"Data Source={path}");
            var closed = LibraryChangeSignal.KeyFor(connection);
            connection.Open();
            LibraryChangeSignal.KeyFor(connection).Should().Be(closed);

            using var elsewhere = new SqliteConnection($"Data Source={path}.other");
            LibraryChangeSignal.KeyFor(elsewhere).Should().NotBe(closed);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }
}

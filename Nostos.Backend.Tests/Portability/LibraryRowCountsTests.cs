using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Portability.Migration;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Preflight, activation and recovery decide "empty or populated" from these
/// counts. They once disagreed about an assistant settings row whose mode was
/// never chosen (issue #887); these tests pin the one rule they now share.
/// </summary>
public sealed class LibraryRowCountsTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private NostosDbContext _db = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _db = new NostosDbContext(
            new DbContextOptionsBuilder<NostosDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task A_settings_row_whose_mode_was_never_chosen_is_not_user_data()
    {
        _db.AssistantSettings.Add(new AssistantSettingsModel { CaptureProcessingMode = null });
        await _db.SaveChangesAsync();

        var userData = await LibraryRowCounts.ReadUserDataAsync(_db, CancellationToken.None);
        var all = await LibraryRowCounts.ReadAllAsync(_db, CancellationToken.None);

        userData.AssistantSettings.Should().Be(0);
        userData.TotalRows.Should().Be(0, "a library holding only that row is still empty");
        all.AssistantSettings.Should().Be(1, "the revision token must still see the row exist");
    }

    [Fact]
    public async Task A_chosen_capture_mode_is_user_data()
    {
        _db.AssistantSettings.Add(new AssistantSettingsModel { CaptureProcessingMode = "verbatim" });
        await _db.SaveChangesAsync();

        var userData = await LibraryRowCounts.ReadUserDataAsync(_db, CancellationToken.None);

        userData.AssistantSettings.Should().Be(1);
        userData.TotalRows.Should().Be(1);
    }

    [Fact]
    public async Task Both_readings_agree_on_every_other_table()
    {
        _db.Topics.Add(new TopicModel { Topic = "Stoicism" });
        _db.Collections.Add(new CollectionModel { Name = "Philosophy" });
        await _db.SaveChangesAsync();

        var userData = await LibraryRowCounts.ReadUserDataAsync(_db, CancellationToken.None);
        var all = await LibraryRowCounts.ReadAllAsync(_db, CancellationToken.None);

        userData.Should().Be(all);
        userData.Topics.Should().Be(1);
        userData.Collections.Should().Be(1);
        userData.TotalRows.Should().Be(2);
    }
}

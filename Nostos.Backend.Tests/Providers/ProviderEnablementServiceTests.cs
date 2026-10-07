using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Providers;
using Nostos.Backend.Providers.Contracts;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Providers;

/// <summary>
/// The provider enablement seam (issue #774): stored choices override a
/// provider's declaration, absence follows the declaration, and only registered
/// ids can ever be written.
/// </summary>
public sealed class ProviderEnablementServiceTests : IDisposable
{
    private readonly SqliteTestFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task With_no_stored_rows_enables_exactly_the_default_on_providers()
    {
        var on = new StubProvider("on-by-default", enabledByDefault: true);
        var off = new StubProvider("off-by-default", enabledByDefault: false);
        var alsoOn = new StubProvider("also-on", enabledByDefault: true);
        var path = _fixture.CreateDatabasePath();
        using var db = _fixture.CreateContext(path);
        var service = CreateService(path, on, off, alsoOn);

        var enabled = await service.GetEnabledAsync();
        var ids = await service.GetEnabledProviderIdsAsync();

        enabled.Select(entry => entry.Id).Should().Equal("on-by-default", "also-on");
        ids.Should().BeEquivalentTo(["on-by-default", "also-on"]);
        (await db.ProviderPreferences.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_stored_false_beats_a_default_on_provider()
    {
        var path = _fixture.CreateDatabasePath();
        using var db = _fixture.CreateContext(path);
        db.ProviderPreferences.Add(new ProviderPreferenceModel
        {
            ProviderId = "on-by-default",
            Enabled = false,
        });
        await db.SaveChangesAsync();
        var service = CreateService(path, new StubProvider("on-by-default", enabledByDefault: true));

        var enabled = await service.GetEnabledAsync();

        enabled.Should().BeEmpty();
        (await service.FindEnabledAsync("on-by-default")).Should().BeNull();
    }

    [Fact]
    public async Task A_stored_true_beats_a_default_off_provider()
    {
        var provider = new StubProvider("off-by-default", enabledByDefault: false);
        var path = _fixture.CreateDatabasePath();
        using var db = _fixture.CreateContext(path);
        db.ProviderPreferences.Add(new ProviderPreferenceModel
        {
            ProviderId = "off-by-default",
            Enabled = true,
        });
        await db.SaveChangesAsync();
        var service = CreateService(path, provider);

        var enabled = await service.GetEnabledAsync();

        enabled.Should().ContainSingle().Which.Id.Should().Be("off-by-default");
        (await service.FindEnabledAsync("off-by-default")).Should().NotBeNull();
    }

    [Fact]
    public async Task A_later_added_provider_follows_its_own_declaration_even_with_stored_choices()
    {
        var path = _fixture.CreateDatabasePath();
        using (var db = _fixture.CreateContext(path))
        {
            db.ProviderPreferences.Add(new ProviderPreferenceModel
            {
                ProviderId = "existing",
                Enabled = false,
            });
            await db.SaveChangesAsync();
        }

        // The same install after an upgrade that added two more providers: the
        // stored choice for one old provider must not decide the new ones.
        var service = CreateService(
            path,
            new StubProvider("existing", enabledByDefault: true),
            new StubProvider("new-default-off", enabledByDefault: false),
            new StubProvider("new-default-on", enabledByDefault: true));

        var ids = await service.GetEnabledProviderIdsAsync();

        ids.Should().BeEquivalentTo(["new-default-on"]);
    }

    [Fact]
    public async Task List_reports_the_effective_choice_and_the_declaration()
    {
        var on = new StubProvider("on", enabledByDefault: true);
        var off = new StubProvider("off", enabledByDefault: false);
        var path = _fixture.CreateDatabasePath();
        using var db = _fixture.CreateContext(path);
        var service = CreateService(path, on, off);

        var entries = await service.ListAsync();

        entries.Should().HaveCount(2);
        var onEntry = entries.Single(entry => entry.Provider.Id == "on");
        onEntry.Enabled.Should().BeTrue();
        onEntry.EnabledByDefault.Should().BeTrue();
        var offEntry = entries.Single(entry => entry.Provider.Id == "off");
        offEntry.Enabled.Should().BeFalse();
        offEntry.EnabledByDefault.Should().BeFalse();
    }

    [Fact]
    public async Task Set_an_unknown_id_returns_null_and_writes_no_row()
    {
        var path = _fixture.CreateDatabasePath();
        using var db = _fixture.CreateContext(path);
        var service = CreateService(path, new StubProvider("known", enabledByDefault: false));

        var entry = await service.SetAsync("not-registered", enabled: true);

        entry.Should().BeNull();
        (await db.ProviderPreferences.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Repeated_set_is_idempotent_and_keeps_one_row()
    {
        var path = _fixture.CreateDatabasePath();
        using var db = _fixture.CreateContext(path);
        var service = CreateService(path, new StubProvider("known", enabledByDefault: true));

        var first = await service.SetAsync("known", enabled: false);
        var second = await service.SetAsync("known", enabled: false);

        first!.Enabled.Should().BeFalse();
        second!.Enabled.Should().BeFalse();
        (await db.ProviderPreferences.CountAsync()).Should().Be(1);
        (await db.ProviderPreferences.SingleAsync()).Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task A_lost_first_time_insert_race_re_reads_and_applies_the_requested_value()
    {
        var provider = new StubProvider("known", enabledByDefault: false);
        var path = _fixture.CreateDatabasePath();
        using (var db = _fixture.CreateContext(path))
        {
            // Schema only. The racing writer commits its row only after the
            // service has already read "no row" and is about to insert.
        }

        var interceptor =
            new ConcurrentInsertInterceptor(path, providerId: "known", concurrentEnabled: true);
        var service = new ProviderEnablementService(
            new ProviderRegistry([provider]),
            new RacingContextFactory(path, interceptor));

        var entry = await service.SetAsync("known", enabled: false);

        interceptor.Fired.Should().BeTrue("the concurrent write must have been staged");
        entry!.Enabled.Should().BeFalse();
        using var verify = _fixture.CreateContext(path);
        var row = await verify.ProviderPreferences.SingleAsync();
        row.ProviderId.Should().Be("known");
        row.Enabled.Should().BeFalse("the losing request must re-read and apply its value");
    }

    [Fact]
    public async Task A_choice_survives_a_new_context_and_a_new_service()
    {
        var path = _fixture.CreateDatabasePath();
        using (var db = _fixture.CreateContext(path))
        {
            await CreateService(path, new StubProvider("known", enabledByDefault: true))
                .SetAsync("known", enabled: false);
        }

        // A fresh factory and service read the same file: the choice is durable,
        // not a cache of the instance that wrote it.
        var reopened = CreateService(path, new StubProvider("known", enabledByDefault: true));
        (await reopened.GetEnabledAsync()).Should().BeEmpty();
        (await reopened.FindEnabledAsync("known")).Should().BeNull();
    }

    private static ProviderEnablementService CreateService(
        string path,
        params IContentProvider[] providers) =>
        new(new ProviderRegistry(providers), new SqliteContextFactory(path));

    private sealed class SqliteContextFactory(string path) : IDbContextFactory<NostosDbContext>
    {
        public NostosDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<NostosDbContext>()
                .UseSqlite($"Data Source={path}")
                .Options);
    }

    /// <summary>
    /// Builds contexts normally except for the first one, which carries the
    /// interceptor that stages the concurrent insert. A retry therefore gets a
    /// clean context, exactly as it would in production.
    /// </summary>
    private sealed class RacingContextFactory(
        string path,
        DbCommandInterceptor firstInsertInterceptor) : IDbContextFactory<NostosDbContext>
    {
        private int _first = 1;

        public NostosDbContext CreateDbContext()
        {
            var builder = new DbContextOptionsBuilder<NostosDbContext>()
                .UseSqlite($"Data Source={path}");
            if (Interlocked.Exchange(ref _first, 0) == 1)
                builder.AddInterceptors(firstInsertInterceptor);

            return new NostosDbContext(builder.Options);
        }
    }

    // Simulates the other session winning the first-time write: another
    // connection commits its own row just before this context's INSERT runs,
    // so the insert fails the ProviderId primary-key constraint. EF executes
    // the INSERT through the reader pipeline, hence the reader hook.
    private sealed class ConcurrentInsertInterceptor(
        string databasePath,
        string providerId,
        bool concurrentEnabled) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (!Fired && command.CommandText.Contains(
                    "INSERT INTO \"ProviderPreferences\"",
                    StringComparison.Ordinal))
            {
                Fired = true;
                using var connection = new SqliteConnection($"Data Source={databasePath}");
                connection.Open();
                using var insert = connection.CreateCommand();
                insert.CommandText =
                    "INSERT INTO \"ProviderPreferences\" " +
                    "(\"ProviderId\", \"Enabled\", \"UpdatedAtUtc\") VALUES (@id, @enabled, @now)";
                insert.Parameters.AddWithValue("@id", providerId);
                insert.Parameters.AddWithValue("@enabled", concurrentEnabled);
                insert.Parameters.AddWithValue("@now", DateTime.UtcNow);
                insert.ExecuteNonQuery();
            }

            return new ValueTask<InterceptionResult<DbDataReader>>(result);
        }
    }

    private sealed class StubProvider(string id, bool enabledByDefault) : IContentProvider
    {
        public string Id { get; } = id;
        public string DisplayName => id;
        public ProviderCapabilities Capabilities => ProviderCapabilities.None;
        public string? RightsNotice => null;
        public bool EnabledByDefault { get; } = enabledByDefault;
    }
}

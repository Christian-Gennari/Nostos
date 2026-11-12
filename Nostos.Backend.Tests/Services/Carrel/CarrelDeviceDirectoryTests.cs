using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Carrel;
using Xunit;

namespace Nostos.Backend.Tests.Services.Carrel;

/// <summary>
/// The Carrel device directory seam: a host that keeps pairing rows apart
/// from each owner's database (Cloud) and one that keeps everything in one
/// database (SelfHosted) behave the same to a device, and one owner's device
/// can never reach another owner's data.
/// </summary>
public sealed class CarrelDeviceDirectoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "nostos-carrel-directory-" + Guid.NewGuid().ToString("N"));

    public CarrelDeviceDirectoryTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        SqliteConnectionPoolClear();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private static void SqliteConnectionPoolClear() =>
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    // ------------------------------------------------------------------
    // Registration
    // ------------------------------------------------------------------

    [Fact]
    public void Registration_defaults_to_the_single_database_directory()
    {
        using var provider = Services(register: null);

        provider.GetRequiredService<ICarrelDeviceDirectory>()
            .Should().BeOfType<SingleDatabaseCarrelDeviceDirectory>();
    }

    [Fact]
    public void A_host_directory_registered_first_is_kept()
    {
        var custom = new SplitDirectory(_directory);
        using var provider = Services(register: custom);

        provider.GetRequiredService<ICarrelDeviceDirectory>().Should().BeSameAs(custom);
    }

    // ------------------------------------------------------------------
    // One database: what SelfHosted does
    // ------------------------------------------------------------------

    [Fact]
    public async Task Single_database_directory_pairs_and_authenticates_in_the_one_database()
    {
        using var provider = Services(register: null);
        var pairing = provider.GetRequiredService<CarrelPairingService>();
        await EnsureDatabaseAsync(provider);

        var token = await PairAsync(pairing, "selfhosted", "Laptop");

        var identity = await pairing.AuthenticateAsync(token);
        identity.Should().NotBeNull();
        identity!.OwnerKey.Should().Be("selfhosted");
        (await pairing.ListDevicesAsync("selfhosted")).Should().ContainSingle(d => d.Name == "Laptop");

        (await pairing.RevokeAsync("selfhosted", identity.DeviceId)).Should().BeTrue();
        (await pairing.AuthenticateAsync(token)).Should().BeNull("a revoked device's token is refused");
    }

    // ------------------------------------------------------------------
    // Two owners, separate databases: what Cloud does
    // ------------------------------------------------------------------

    [Fact]
    public async Task Pairing_rows_live_in_the_pairings_database_and_devices_in_the_owner_database()
    {
        var split = new SplitDirectory(_directory);
        using var provider = Services(split);
        var pairing = provider.GetRequiredService<CarrelPairingService>();

        var started = await pairing.StartAsync("Tablet", new string('k', 64));
        started.Should().NotBeNull();

        (await split.CountAsync(split.PairingsPath, "CarrelPairings")).Should().Be(1);
        (await split.CountAsync(split.OwnerPath("a"), "CarrelPairings")).Should().Be(0,
            "an anonymous pairing is not stored in an owner's database");

        (await pairing.PendingAsync(started!.Code)).Should().NotBeNull();
        var outcome = await pairing.ApproveAsync("a", started.Code);
        outcome.Should().BeOfType<CarrelApprovalOutcome.Approved>();

        (await split.CountAsync(split.OwnerPath("a"), "CarrelDevices")).Should().Be(1);
        (await split.CountAsync(split.PairingsPath, "CarrelDevices")).Should().Be(0,
            "the pairings database holds no devices");
        (await split.CountAsync(split.OwnerPath("b"), "CarrelDevices")).Should().Be(0);
        split.Registered.Should().ContainSingle().Which.Owner.Should().Be("a");

        // The device collects its token by polling, then uses it.
        var status = await pairing.StatusAsync(Guid.Parse(started.PairId));
        status!.Status.Should().Be("approved");
        status.Token.Should().StartWith("stl_");
        var identity = await pairing.AuthenticateAsync(status.Token!);
        identity!.OwnerKey.Should().Be("a");
    }

    [Fact]
    public async Task A_device_of_one_owner_never_sees_or_removes_another_owners_devices()
    {
        var split = new SplitDirectory(_directory);
        using var provider = Services(split);
        var pairing = provider.GetRequiredService<CarrelPairingService>();
        var sync = provider.GetRequiredService<CarrelSyncService>();

        var tokenA = await PairAsync(pairing, "a", "Device of A");
        var tokenB = await PairAsync(pairing, "b", "Device of B");

        var identityA = (await pairing.AuthenticateAsync(tokenA))!;
        var identityB = (await pairing.AuthenticateAsync(tokenB))!;
        identityA.OwnerKey.Should().Be("a");
        identityB.OwnerKey.Should().Be("b");

        (await pairing.ListDevicesAsync("a")).Should().ContainSingle(d => d.Name == "Device of A");
        (await pairing.ListDevicesAsync("b")).Should().ContainSingle(d => d.Name == "Device of B");

        // B cannot revoke A's device by its id, and A's token keeps working.
        (await pairing.RevokeAsync("b", identityA.DeviceId)).Should().BeFalse();
        (await pairing.AuthenticateAsync(tokenA)).Should().NotBeNull();

        // Each owner's sync state is created in that owner's database only.
        (await sync.ListFilesAsync("a", since: null)).Should().NotBeNull();
        (await split.CountAsync(split.OwnerPath("a"), "CarrelSyncStates")).Should().Be(1);
        (await split.CountAsync(split.OwnerPath("b"), "CarrelSyncStates")).Should().Be(0);
    }

    [Fact]
    public async Task A_wrong_index_entry_cannot_reach_another_owners_data()
    {
        var split = new SplitDirectory(_directory);
        using var provider = Services(split);
        var pairing = provider.GetRequiredService<CarrelPairingService>();

        var tokenA = await PairAsync(pairing, "a", "Device of A");
        await PairAsync(pairing, "b", "Device of B");

        // The index is poisoned: A's token hash now routes to owner B. B's
        // database has no device row with that hash, so the token is refused.
        split.Reroute(CarrelTokens.Hash(tokenA), "b");
        (await pairing.AuthenticateAsync(tokenA)).Should().BeNull();

        // And a hash that was never issued finds no owner at all.
        (await pairing.AuthenticateAsync(CarrelTokens.Create())).Should().BeNull();
    }

    [Fact]
    public async Task Revoking_a_device_forgets_it_and_its_token_stops_working()
    {
        var split = new SplitDirectory(_directory);
        using var provider = Services(split);
        var pairing = provider.GetRequiredService<CarrelPairingService>();

        var token = await PairAsync(pairing, "a", "Device of A");
        var identity = (await pairing.AuthenticateAsync(token))!;
        split.Registered.Should().ContainSingle();

        (await pairing.RevokeAsync("a", identity.DeviceId)).Should().BeTrue();

        split.Registered.Should().BeEmpty();
        (await pairing.AuthenticateAsync(token)).Should().BeNull();
        (await pairing.RevokeAsync("a", identity.DeviceId)).Should().BeFalse("it is already gone");
    }

    [Fact]
    public async Task A_token_whose_owner_is_unavailable_is_unauthorized_not_an_error()
    {
        var split = new SplitDirectory(_directory);
        using var provider = Services(split);
        var pairing = provider.GetRequiredService<CarrelPairingService>();

        var token = await PairAsync(pairing, "a", "Device of A");
        split.Unavailable.Add("a");

        (await pairing.AuthenticateAsync(token)).Should().BeNull();
    }

    [Fact]
    public async Task A_failed_approval_puts_the_code_back_so_it_can_be_approved_again()
    {
        var split = new SplitDirectory(_directory);
        using var provider = Services(split);
        var pairing = provider.GetRequiredService<CarrelPairingService>();

        var started = (await pairing.StartAsync("Tablet", new string('k', 64)))!;
        split.Unavailable.Add("a");

        var failing = async () => await pairing.ApproveAsync("a", started.Code);
        await failing.Should().ThrowAsync<CarrelOwnerUnavailableException>();

        (await pairing.StatusAsync(Guid.Parse(started.PairId)))!.Status.Should().Be("pending");
        (await split.CountAsync(split.OwnerPath("a"), "CarrelDevices")).Should().Be(0);
        split.Registered.Should().BeEmpty();

        split.Unavailable.Clear();
        (await pairing.ApproveAsync("a", started.Code)).Should().BeOfType<CarrelApprovalOutcome.Approved>();
        (await split.CountAsync(split.OwnerPath("a"), "CarrelDevices")).Should().Be(1);
    }

    [Fact]
    public async Task One_approval_wins_when_two_owners_use_the_same_code()
    {
        var split = new SplitDirectory(_directory);
        using var provider = Services(split);
        var pairing = provider.GetRequiredService<CarrelPairingService>();

        var started = (await pairing.StartAsync("Tablet", new string('k', 64)))!;

        (await pairing.ApproveAsync("a", started.Code)).Should().BeOfType<CarrelApprovalOutcome.Approved>();
        (await pairing.ApproveAsync("b", started.Code)).Should().BeOfType<CarrelApprovalOutcome.AlreadyApproved>();

        (await split.CountAsync(split.OwnerPath("a"), "CarrelDevices")).Should().Be(1);
        (await split.CountAsync(split.OwnerPath("b"), "CarrelDevices")).Should().Be(0);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private ServiceProvider Services(ICarrelDeviceDirectory? register)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        services.AddDataProtection();
        services.AddRouting();
        services.AddSingleton<IDbContextFactory<NostosDbContext>>(
            new SqliteFactory(Path.Combine(_directory, "single.db")));
        if (register is not null)
            services.AddSingleton(register);
        services.AddNostosCarrelSync();
        return services.BuildServiceProvider();
    }

    private static async Task EnsureDatabaseAsync(IServiceProvider provider)
    {
        await using var db = await provider
            .GetRequiredService<IDbContextFactory<NostosDbContext>>().CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
    }

    private static async Task<string> PairAsync(CarrelPairingService pairing, string owner, string name)
    {
        var started = (await pairing.StartAsync(name, new string('k', 64)))!;
        (await pairing.ApproveAsync(owner, started.Code)).Should().BeOfType<CarrelApprovalOutcome.Approved>();
        var status = (await pairing.StatusAsync(Guid.Parse(started.PairId)))!;
        return status.Token!;
    }

    private static NostosDbContext OpenSqlite(string path)
    {
        var db = new NostosDbContext(new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False")
            .Options);
        db.Database.EnsureCreated();
        return db;
    }

    private sealed class SqliteFactory(string path) : IDbContextFactory<NostosDbContext>
    {
        public NostosDbContext CreateDbContext() => OpenSqlite(path);
    }

    /// <summary>
    /// A directory shaped like Cloud's: one database for pairing rows, one per
    /// owner, and a token index held apart from all of them.
    /// </summary>
    private sealed class SplitDirectory(string directory) : ICarrelDeviceDirectory
    {
        private readonly Dictionary<string, string> _index = new();

        public string PairingsPath { get; } = Path.Combine(directory, "pairings.db");

        public HashSet<string> Unavailable { get; } = new();

        public IEnumerable<(string Owner, Guid DeviceId)> Registered =>
            _registered.Select(r => (r.Value.Owner, r.Key));

        private readonly Dictionary<Guid, (string Owner, string Hash)> _registered = new();

        public string OwnerPath(string owner) => Path.Combine(directory, $"owner-{owner}.db");

        public void Reroute(string tokenHash, string owner) => _index[tokenHash] = owner;

        public ValueTask<NostosDbContext> OpenPairingsAsync(CancellationToken ct) =>
            ValueTask.FromResult(OpenSqlite(PairingsPath));

        public ValueTask<NostosDbContext> OpenOwnerAsync(string ownerKey, CancellationToken ct) =>
            Unavailable.Contains(ownerKey)
                ? throw new CarrelOwnerUnavailableException()
                : ValueTask.FromResult(OpenSqlite(OwnerPath(ownerKey)));

        public ValueTask<string?> FindOwnerAsync(string tokenHash, CancellationToken ct) =>
            ValueTask.FromResult(_index.TryGetValue(tokenHash, out var owner) ? owner : null);

        public ValueTask RegisterDeviceAsync(
            string ownerKey, Guid deviceId, string tokenHash, CancellationToken ct)
        {
            _index[tokenHash] = ownerKey;
            _registered[deviceId] = (ownerKey, tokenHash);
            return ValueTask.CompletedTask;
        }

        public ValueTask UnregisterDeviceAsync(string ownerKey, Guid deviceId, CancellationToken ct)
        {
            if (_registered.TryGetValue(deviceId, out var entry) && entry.Owner == ownerKey)
            {
                _index.Remove(entry.Hash);
                _registered.Remove(deviceId);
            }

            return ValueTask.CompletedTask;
        }

        public async Task<int> CountAsync(string path, string table)
        {
            await using var db = OpenSqlite(path);
            return await db.Database
                .SqlQueryRaw<int>($"SELECT COUNT(*) AS \"Value\" FROM \"{table}\"")
                .SingleAsync();
        }
    }
}

using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Providers;

/// <summary>
/// One provider together with the user's effective choice for it (issue #774).
/// <see cref="EnabledByDefault"/> is the provider's own declaration, so Settings
/// can distinguish "off because the user turned it off" from "off because this
/// source opts in" without a second read.
/// </summary>
public sealed record ProviderEnablementEntry(
    ProviderRegistration Provider,
    bool Enabled,
    bool EnabledByDefault);

/// <summary>
/// The one place provider enablement is resolved (issue #774). Endpoints never
/// query the preferences table themselves: every read answers "which providers
/// may this install use right now", which is the exact question discovery and
/// acquisition need to ask.
///
/// <para>
/// A provider is enabled when the user has stored a choice for it and that
/// choice is true, or when no choice is stored and the provider declares
/// <see cref="Contracts.IContentProvider.EnabledByDefault"/>. Absence of a row
/// therefore means "use the declaration", which is what keeps a later-added
/// provider from silently joining an existing install's searches.
/// </para>
///
/// <para>
/// No caching on purpose: the stored set is read per request, so a toggle is
/// visible to the very next search or acquisition, and restart semantics are
/// the database's.
/// </para>
/// </summary>
public interface IProviderEnablementService
{
    /// <summary>The registrations the user may currently discover and acquire from.</summary>
    Task<IReadOnlyList<ProviderRegistration>> GetEnabledAsync(CancellationToken ct = default);

    /// <summary>The same set as ids, for a discovery request's provider filter.</summary>
    Task<IReadOnlySet<string>> GetEnabledProviderIdsAsync(CancellationToken ct = default);

    /// <summary>
    /// The registration when it is both registered and enabled; null otherwise.
    /// A disabled provider is deliberately indistinguishable from an unknown
    /// one at the HTTP edge (both are the existing <c>provider_unknown</c> 404),
    /// so this surface never leaks which sources the install knows about.
    /// </summary>
    Task<ProviderRegistration?> FindEnabledAsync(string providerId, CancellationToken ct = default);

    /// <summary>Every registered provider with its effective choice, for Settings.</summary>
    Task<IReadOnlyList<ProviderEnablementEntry>> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// Stores an explicit choice and returns the resulting entry. Returns null
    /// for an id the registry does not know, writing nothing: a preference row
    /// for a provider that cannot resolve is dead state.
    /// </summary>
    Task<ProviderEnablementEntry?> SetAsync(
        string providerId,
        bool enabled,
        CancellationToken ct = default);
}

/// <inheritdoc cref="IProviderEnablementService" />
public sealed class ProviderEnablementService(
    IProviderRegistry registry,
    IDbContextFactory<NostosDbContext> dbFactory) : IProviderEnablementService
{
    public async Task<IReadOnlyList<ProviderRegistration>> GetEnabledAsync(
        CancellationToken ct = default)
    {
        var stored = await LoadAsync(ct);
        return registry.All.Where(registration => IsEnabled(registration, stored)).ToList();
    }

    public async Task<IReadOnlySet<string>> GetEnabledProviderIdsAsync(
        CancellationToken ct = default)
    {
        var stored = await LoadAsync(ct);
        return registry.All
            .Where(registration => IsEnabled(registration, stored))
            .Select(registration => registration.Id)
            .ToHashSet(StringComparer.Ordinal);
    }

    public async Task<ProviderRegistration?> FindEnabledAsync(
        string providerId,
        CancellationToken ct = default)
    {
        var registration = registry.Find(providerId);
        if (registration is null)
            return null;

        var stored = await LoadAsync(ct);
        return IsEnabled(registration, stored) ? registration : null;
    }

    public async Task<IReadOnlyList<ProviderEnablementEntry>> ListAsync(
        CancellationToken ct = default)
    {
        var stored = await LoadAsync(ct);
        return registry.All
            .Select(registration => ToEntry(registration, stored))
            .ToList();
    }

    public async Task<ProviderEnablementEntry?> SetAsync(
        string providerId,
        bool enabled,
        CancellationToken ct = default)
    {
        var registration = registry.Find(providerId);
        if (registration is null)
            return null;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.ProviderPreferences
            .FirstOrDefaultAsync(p => p.ProviderId == registration.Id, ct);

        if (row is null)
        {
            row = new ProviderPreferenceModel { ProviderId = registration.Id };
            db.ProviderPreferences.Add(row);
        }

        row.Enabled = enabled;
        row.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return new ProviderEnablementEntry(
            registration,
            enabled,
            registration.Provider.EnabledByDefault);
    }

    private async Task<Dictionary<string, bool>> LoadAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ProviderPreferences
            .AsNoTracking()
            .ToDictionaryAsync(p => p.ProviderId, p => p.Enabled, StringComparer.Ordinal, ct);
    }

    private static bool IsEnabled(
        ProviderRegistration registration,
        Dictionary<string, bool> stored) =>
        stored.TryGetValue(registration.Id, out var chosen)
            ? chosen
            : registration.Provider.EnabledByDefault;

    private static ProviderEnablementEntry ToEntry(
        ProviderRegistration registration,
        Dictionary<string, bool> stored) =>
        new(registration, IsEnabled(registration, stored), registration.Provider.EnabledByDefault);
}

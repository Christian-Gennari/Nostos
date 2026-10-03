using Microsoft.Extensions.Options;
using Nostos.Backend.Providers.Contracts;

namespace Nostos.Backend.Providers.Discovery;

/// <summary>One source's contribution to an aggregate discovery request.</summary>
public sealed record ProviderDiscoverySourceStatus(
    string ProviderId,
    string DisplayName,
    bool Succeeded,
    string? Notice = null,
    string? ErrorCode = null);

/// <summary>Provider-neutral result of searching every eligible source.</summary>
public sealed record ProviderDiscoveryResult(
    IReadOnlyList<ProviderItem> Items,
    bool HasMore,
    IReadOnlyList<ProviderDiscoverySourceStatus> Sources);

/// <summary>
/// Searches provider capabilities rather than provider identities, then combines
/// each source's own ranked results without inventing a cross-provider score.
/// </summary>
public sealed class ProviderDiscoveryService
{
    private readonly IProviderRegistry _registry;
    private readonly ILogger<ProviderDiscoveryService> _logger;
    private readonly TimeSpan _searchTimeout;

    public ProviderDiscoveryService(
        IProviderRegistry registry,
        IOptions<ProviderDiscoveryOptions> options,
        ILogger<ProviderDiscoveryService> logger)
    {
        _registry = registry;
        _logger = logger;
        _searchTimeout = options.Value.EffectiveProviderDeadline;
    }

    public async Task<ProviderDiscoveryResult> SearchAsync(
        string query,
        ProviderMediaKind? kind,
        int limit,
        CancellationToken ct)
    {
        var eligible = _registry.All
            .Where(registration => IsEligible(registration, kind))
            .OrderBy(registration => registration.Id, StringComparer.Ordinal)
            .ToList();

        var tasks = eligible
            .Select(registration => SearchOneAsync(registration, query, kind, limit, ct))
            .ToArray();

        // Each task is bounded by the same deadline and they all start now, so
        // this settles within the aggregate budget. The merge below runs once
        // on the settled outcomes; a provider that finishes later has already
        // been cancelled and cannot change the returned response.
        var results = await Task.WhenAll(tasks);

        var successfulPages = results
            .Where(result => result.Page is not null)
            .Select(result => result.Page!)
            .ToList();

        var candidateCount = successfulPages.Sum(page => page.Items.Count);
        var merged = RoundRobin(successfulPages.Select(page => page.Items).ToList(), limit);

        return new ProviderDiscoveryResult(
            Items: merged,
            HasMore: results.Any(result => result.Page?.HasMore == true) || candidateCount > merged.Count,
            Sources: results.Select(result => result.Status).ToList());
    }

    private async Task<SearchOutcome> SearchOneAsync(
        ProviderRegistration registration,
        string query,
        ProviderMediaKind? kind,
        int limit,
        CancellationToken requestCt)
    {
        using var providerCts = CancellationTokenSource.CreateLinkedTokenSource(requestCt);
        Task<ProviderSearchPage>? providerTask = null;

        try
        {
            providerTask = registration.Search!.SearchAsync(
                new ProviderSearchQuery(query, limit, Offset: 0, Kind: kind),
                providerCts.Token);

            var page = await providerTask.WaitAsync(_searchTimeout, requestCt);

            return new SearchOutcome(
                page,
                new ProviderDiscoverySourceStatus(
                    registration.Id,
                    registration.Provider.DisplayName,
                    Succeeded: true,
                    Notice: page.Notice));
        }
        catch (TimeoutException)
        {
            // If request cancellation raced the provider deadline, the outer
            // request remains authoritative.
            requestCt.ThrowIfCancellationRequested();

            CancelProvider(providerCts, registration.Id);
            ObserveLateFault(providerTask);
            requestCt.ThrowIfCancellationRequested();

            _logger.LogWarning(
                "Provider discovery timed out for {ProviderId} after {Timeout}",
                registration.Id,
                _searchTimeout);

            return TimeoutOutcome(registration);
        }
        catch (OperationCanceledException)
        {
            // HttpClient.Timeout and provider-local cancellation surface as
            // cancellation exceptions too. They are local failures unless the
            // aggregate request token itself was cancelled.
            requestCt.ThrowIfCancellationRequested();

            CancelProvider(providerCts, registration.Id);
            ObserveLateFault(providerTask);
            requestCt.ThrowIfCancellationRequested();

            _logger.LogWarning(
                "Provider discovery was locally cancelled for {ProviderId}; treating it as a timeout",
                registration.Id);

            return TimeoutOutcome(registration);
        }
        catch (ProviderException ex)
        {
            return new SearchOutcome(
                null,
                new ProviderDiscoverySourceStatus(
                    registration.Id,
                    registration.Provider.DisplayName,
                    Succeeded: false,
                    ErrorCode: ex.Code));
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unexpected provider discovery failure for {ProviderId}",
                registration.Id);

            return new SearchOutcome(
                null,
                new ProviderDiscoverySourceStatus(
                    registration.Id,
                    registration.Provider.DisplayName,
                    Succeeded: false,
                    ErrorCode: ProviderDiscoveryErrorCodes.SearchFailed));
        }
    }

    private SearchOutcome TimeoutOutcome(ProviderRegistration registration) =>
        new(
            null,
            new ProviderDiscoverySourceStatus(
                registration.Id,
                registration.Provider.DisplayName,
                Succeeded: false,
                ErrorCode: ProviderDiscoveryErrorCodes.Timeout));

    private void CancelProvider(CancellationTokenSource providerCts, string providerId)
    {
        try
        {
            providerCts.Cancel();
        }
        catch (Exception ex)
        {
            // Cancellation is best-effort after the hard WaitAsync boundary.
            // A misbehaving provider callback must not replace the timeout
            // outcome with an aggregate failure.
            _logger.LogDebug(
                ex,
                "Provider discovery cancellation callback failed for {ProviderId}",
                providerId);
        }
    }

    private static void ObserveLateFault(Task<ProviderSearchPage>? providerTask)
    {
        if (providerTask is null || providerTask.IsCompletedSuccessfully || providerTask.IsCanceled)
            return;

        _ = providerTask.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static bool IsEligible(
        ProviderRegistration registration,
        ProviderMediaKind? kind)
    {
        var capabilities = registration.Provider.Capabilities;
        if (registration.Search is null || !capabilities.HasFlag(ProviderCapabilities.Search))
            return false;

        return kind switch
        {
            ProviderMediaKind.Ebook =>
                capabilities.HasFlag(ProviderCapabilities.EbookAcquisition),
            ProviderMediaKind.Audiobook =>
                capabilities.HasFlag(ProviderCapabilities.AudiobookAcquisition),
            null =>
                capabilities.HasFlag(ProviderCapabilities.EbookAcquisition)
                || capabilities.HasFlag(ProviderCapabilities.AudiobookAcquisition),
            _ => false,
        };
    }

    private static IReadOnlyList<ProviderItem> RoundRobin(
        IReadOnlyList<IReadOnlyList<ProviderItem>> sources,
        int limit)
    {
        var merged = new List<ProviderItem>(limit);
        var index = 0;

        while (merged.Count < limit)
        {
            var added = false;

            foreach (var items in sources)
            {
                if (index >= items.Count)
                    continue;

                merged.Add(items[index]);
                added = true;

                if (merged.Count == limit)
                    break;
            }

            if (!added)
                break;

            index++;
        }

        return merged;
    }

    private sealed record SearchOutcome(
        ProviderSearchPage? Page,
        ProviderDiscoverySourceStatus Status);
}

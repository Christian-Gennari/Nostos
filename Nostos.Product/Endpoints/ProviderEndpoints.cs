using Nostos.Backend.Providers;
using Nostos.Backend.Providers.Acquisition;
using Nostos.Backend.Providers.Contracts;
using Nostos.Backend.Providers.Discovery;
using Nostos.Shared.Dtos;
using Nostos.Product.Composition;

namespace Nostos.Backend.Endpoints;

/// <summary>
/// The provider/acquisition HTTP surface.
///
/// Kept deliberately thin and provider-agnostic: every handler works in terms of
/// the normalised contracts, so a new source needs a DI registration and nothing
/// here. In particular no handler accepts a URL — a client picks a provider, an
/// item and an asset, and the provider resolves the rest server-side.
/// </summary>
public static class ProviderEndpoints
{
    public static IEndpointRouteBuilder MapProviderEndpoints(
        this IEndpointRouteBuilder routes,
        NostosProductEndpointPolicies? policies = null)
    {
        policies ??= NostosProductEndpointPolicies.None;

        var group = routes.MapGroup("/api/providers");
        if (!string.IsNullOrWhiteSpace(policies.ProviderFetchRateLimitPolicy))
            group.RequireRateLimiting(policies.ProviderFetchRateLimitPolicy);

        // Sources available to import from.
        group.MapGet(
            "/",
            (IProviderRegistry registry) =>
                Results.Ok(registry.All.Select(ToSummaryDto).ToList()));

        // Search every source that can both search and acquire the requested
        // kind. Providers remain provenance; capability registration determines
        // participation, so adding a provider does not add endpoint/UI rules.
        group.MapGet(
            "/search",
            async (
                string? query,
                int? limit,
                string? kind,
                ProviderDiscoveryService discovery,
                CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
                    return Results.Ok(new ProviderDiscoverySearchResultDto([], false, []));

                if (query.Length > 200)
                    return Results.BadRequest(new { error = "Search text is limited to 200 characters." });

                ProviderMediaKind? mediaKind = null;
                if (!string.IsNullOrWhiteSpace(kind))
                {
                    if (Enum.TryParse<ProviderMediaKind>(kind, ignoreCase: true, out var parsedKind))
                    {
                        mediaKind = parsedKind;
                    }
                    else
                    {
                        return Results.BadRequest(new { error = "Invalid material kind." });
                    }
                }

                var result = await discovery.SearchAsync(
                    query.Trim(),
                    mediaKind,
                    Math.Clamp(limit ?? 20, 1, 50),
                    ct);

                return Results.Ok(new ProviderDiscoverySearchResultDto(
                    result.Items.Select(ToItemDto).ToList(),
                    result.HasMore,
                    result.Sources
                        .Select(source => new ProviderDiscoverySourceStatusDto(
                            source.ProviderId,
                            source.DisplayName,
                            source.Succeeded,
                            source.Notice,
                            source.ErrorCode))
                        .ToList()));
            });

        // Search one source. A thin result set is not an error: the provider
        // explains itself in Notice instead.
        group.MapGet(
            "/{providerId}/search",
            async (
                string providerId,
                string? query,
                int? limit,
                int? offset,
                ProviderMediaKind? kind,
                IProviderRegistry registry,
                CancellationToken ct) =>
            {
                var provider = registry.Find(providerId);
                if (provider is null)
                    return UnknownProvider(providerId);

                if (provider.Search is null)
                    return Results.Problem(
                        statusCode: StatusCodes.Status422UnprocessableEntity,
                        title: "provider_search_unsupported",
                        detail: $"{provider.Provider.DisplayName} does not support searching.");

                if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
                    return Results.Ok(new ProviderSearchResultDto([], false, null));

                if (query.Length > 200)
                    return Results.BadRequest(new { error = "Search text is limited to 200 characters." });

                try
                {
                    var page = await provider.Search.SearchAsync(
                        new ProviderSearchQuery(
                            query.Trim(),
                            Math.Clamp(limit ?? 20, 1, 50),
                            Math.Clamp(offset ?? 0, 0, 1000),
                            kind),
                        ct);

                    return Results.Ok(new ProviderSearchResultDto(
                        page.Items.Select(ToItemDto).ToList(),
                        page.HasMore,
                        page.Notice));
                }
                catch (ProviderException ex)
                {
                    return ProviderFailure(ex);
                }
            });

        // Full detail for one item, including the assets that can be imported.
        group.MapGet(
            "/{providerId}/items/{externalId}",
            async (
                string providerId,
                string externalId,
                IProviderRegistry registry,
                CancellationToken ct) =>
            {
                var provider = registry.Find(providerId);
                if (provider is null)
                    return UnknownProvider(providerId);

                if (provider.Catalog is null)
                    return Results.Problem(
                        statusCode: StatusCodes.Status422UnprocessableEntity,
                        title: "provider_item_retrieval_unsupported",
                        detail: $"{provider.Provider.DisplayName} cannot retrieve individual items.");

                try
                {
                    var item = await provider.Catalog.GetItemAsync(externalId, ct);
                    return item is null
                        ? Results.NotFound(new { error = "provider_item_not_found" })
                        : Results.Ok(ToItemDto(item));
                }
                catch (ProviderException ex)
                {
                    return ProviderFailure(ex);
                }
            });

        // Proxied cover artwork. The browser never talks to the source directly:
        // hotlinking would leak the reader's IP to it, need CORS the source does
        // not grant, and bypass the same host policy that governs downloads.
        group.MapGet(
            "/{providerId}/items/{externalId}/cover",
            async (
                string providerId,
                string externalId,
                IProviderRegistry registry,
                IProviderContentDownloader downloader,
                CancellationToken ct) =>
            {
                var provider = registry.Find(providerId);
                if (provider is null)
                    return UnknownProvider(providerId);

                if (provider.Catalog is null || provider.DownloadPolicy is null)
                    return Results.NotFound();

                try
                {
                    var item = await provider.Catalog.GetItemAsync(externalId, ct);
                    if (item?.Cover is null)
                        return Results.NotFound();

                    var bytes = await downloader.DownloadBytesAsync(
                        item.Cover.Url, provider.DownloadPolicy, 12L * 1024 * 1024, ct);

                    var contentType = item.Cover.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                        ? item.Cover.ContentType
                        : "image/jpeg";

                    return Results.File(bytes, contentType);
                }
                catch (ProviderException ex)
                {
                    return ProviderFailure(ex);
                }
                catch (ProviderDownloadException)
                {
                    // A missing thumbnail is not worth an error page: the client
                    // simply falls back to its placeholder.
                    return Results.NotFound();
                }
            });

        // Start an import. Returns as soon as the job is queued: a whole
        // audiobook takes far longer than any sane request, so completion is
        // reported through the job status below.
        group.MapPost(
            "/{providerId}/acquire",
            (
                string providerId,
                ProviderAcquireRequestDto dto,
                IProviderRegistry registry,
                IAcquisitionJobManager jobs) =>
            {
                var provider = registry.Find(providerId);
                if (provider is null)
                    return UnknownProvider(providerId);

                if (string.IsNullOrWhiteSpace(dto.ExternalId))
                    return Results.BadRequest(new { error = "An external item id is required." });

                try
                {
                    var job = jobs.Start(new AcquisitionRequest(
                        providerId,
                        dto.ExternalId.Trim(),
                        string.IsNullOrWhiteSpace(dto.AssetId) ? null : dto.AssetId.Trim(),
                        dto.CollectionIds,
                        dto.IncludeCover,
                        ToMetadataOverrides(dto.MetadataOverrides)));

                    return Results.Accepted($"/api/providers/acquisitions/{job.JobId}", ToJobDto(job));
                }
                catch (InvalidOperationException)
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status429TooManyRequests,
                        title: "acquisition_queue_full",
                        detail: "The acquisition queue is currently full.");
                }
            });

        // Job status. This is what the UI polls.
        group.MapGet(
            "/acquisitions/{jobId}",
            (string jobId, IAcquisitionJobManager jobs) =>
            {
                var job = jobs.Get(jobId);

                // A job id this process does not know about is the expected
                // outcome of a restart, and says so rather than looking like a
                // client bug.
                return job is null
                    ? Results.NotFound(new
                    {
                        error = "acquisition_job_unknown",
                        detail = "This import is no longer being tracked. It may have finished before the server restarted; check your library.",
                    })
                    : Results.Ok(ToJobDto(job));
            });

        group.MapDelete(
            "/acquisitions/{jobId}",
            (string jobId, IAcquisitionJobManager jobs) =>
                jobs.Cancel(jobId) ? Results.NoContent() : Results.NotFound());

        return routes;
    }

    private static IResult UnknownProvider(string providerId) =>
        Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "provider_unknown",
            detail: $"No content provider is registered as '{providerId}'.");

    private static IResult ProviderFailure(ProviderException ex) => Results.Problem(
        statusCode: ex.Code switch
        {
            ProviderException.ItemNotFound => StatusCodes.Status404NotFound,
            ProviderException.AssetUnavailable => StatusCodes.Status404NotFound,
            ProviderException.Unavailable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status502BadGateway,
        },
        title: ex.Code,
        detail: ex.Code switch
        {
            ProviderException.ItemNotFound => "The provider item was not found.",
            ProviderException.AssetUnavailable => "The requested provider asset is unavailable.",
            ProviderException.Unavailable => "The content provider is temporarily unavailable.",
            _ => "The content provider request failed.",
        });

    private static ProviderSummaryDto ToSummaryDto(ProviderRegistration registration) => new(
        registration.Provider.Id,
        registration.Provider.DisplayName,
        DescribeCapabilities(registration.Provider.Capabilities),
        registration.Provider.RightsNotice);

    /// <summary>
    /// Capability flags as lower-case strings, so the client can test for what it
    /// needs without a shared enum drifting out of step with the server's.
    /// </summary>
    private static IReadOnlyList<string> DescribeCapabilities(ProviderCapabilities capabilities)
    {
        var names = new List<string>();

        foreach (var value in Enum.GetValues<ProviderCapabilities>())
        {
            if (value == ProviderCapabilities.None || !capabilities.HasFlag(value))
                continue;

            names.Add(value.ToString().ToLowerInvariant());
        }

        return names;
    }

    private static ProviderItemDto ToItemDto(ProviderItem item)
    {
        var metadata = item.Metadata;

        return new ProviderItemDto(
            ProviderId: item.ProviderId,
            ExternalId: item.ExternalId,
            MediaKind: item.MediaKind == ProviderMediaKind.Audiobook ? "audiobook" : "ebook",
            Title: metadata.Title,
            Subtitle: metadata.Subtitle,
            Author: metadata.Author,
            Description: metadata.Description,
            Language: metadata.Language,
            Publisher: metadata.Publisher,
            PublishedDate: metadata.PublishedDate,
            Categories: metadata.Categories,
            Narrator: metadata.Narrator,
            Duration: metadata.Duration,
            PageCount: metadata.PageCount,
            Assets: item.Assets
                .Select(a => new ProviderAssetDto(
                    a.Id,
                    a.Kind == ProviderMediaKind.Audiobook ? "audiobook" : "ebook",
                    a.Label,
                    a.SourceFormat,
                    a.SizeBytes,
                    a.IsPreferred))
                .ToList(),
            CoverUrl: item.Cover is null
                ? null
                : $"/api/providers/{Uri.EscapeDataString(item.ProviderId)}/items/{Uri.EscapeDataString(item.ExternalId)}/cover",
            SourceUrl: item.Source?.ItemUrl,
            RightsStatement: item.Source?.RightsStatement,
            PartCount: item.PartCount);
    }

    /// <summary>
    /// Maps the wire shape onto the internal one. Null in, null out: a client
    /// that sent no overrides must not read as having sent empty ones, which
    /// would clear every field it left out. Named arguments on purpose — the two
    /// records describe the same thing and must stay field-for-field aligned.
    /// </summary>
    private static ProviderMetadataOverrides? ToMetadataOverrides(ProviderMetadataOverridesDto? dto) =>
        dto is null
            ? null
            : new ProviderMetadataOverrides(
                Title: dto.Title,
                Subtitle: dto.Subtitle,
                Author: dto.Author,
                Description: dto.Description,
                Language: dto.Language,
                Publisher: dto.Publisher,
                PublishedDate: dto.PublishedDate,
                Categories: dto.Categories,
                Narrator: dto.Narrator,
                Duration: dto.Duration,
                PageCount: dto.PageCount);

    private static ProviderAcquisitionDto ToJobDto(AcquisitionJobStatus job) => new(
        JobId: job.JobId,
        State: job.State.ToString().ToLowerInvariant(),
        Stage: job.Stage,
        Percent: job.Percent,
        Detail: job.Detail,
        ProviderId: job.ProviderId,
        ExternalId: job.ExternalId,
        AssetId: job.AssetId,
        BookId: job.BookId,
        ErrorCode: job.ErrorCode,
        Message: job.Message,
        CreatedAt: job.CreatedAt,
        UpdatedAt: job.UpdatedAt);
}

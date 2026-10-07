using Nostos.Backend.Providers;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Endpoints;

/// <summary>
/// The provider enablement settings surface (issue #774). Two routes under one
/// group, matching the <c>/api/settings/assistant</c> and
/// <c>/api/settings/ai-provider</c> shape:
/// <list type="bullet">
/// <item><c>GET /api/settings/providers</c> — every registered source with its
/// capabilities, description and effective choice. This is the one surface
/// that lists disabled providers, so they can be re-enabled.</item>
/// <item><c>PUT /api/settings/providers/{providerId}</c> — store one explicit
/// enable/disable choice. A body without <c>enabled</c> is a 400 and stores
/// nothing; an unregistered id is the same 404 <c>provider_unknown</c> problem
/// the provider surface uses.</item>
/// </list>
///
/// These routes fetch no remote content and take no rate-limit/auth policy in
/// SelfHosted; a host applies its own auth middleware in front, as with the
/// other settings groups.
/// </summary>
public static class ProviderSettingsEndpoints
{
    public const string Route = "/api/settings/providers";

    public static IEndpointRouteBuilder MapProviderSettingsEndpoints(
        this IEndpointRouteBuilder routes)
    {
        routes.MapGet(Route, GetAsync);
        routes.MapPut($"{Route}/{{providerId}}", UpdateAsync);
        return routes;
    }

    private static async Task<IResult> GetAsync(
        IProviderEnablementService enablement,
        CancellationToken ct)
    {
        var entries = await enablement.ListAsync(ct);
        return Results.Ok(new ProviderSettingsResponseDto(
            entries.Select(ToDto).ToList()));
    }

    private static async Task<IResult> UpdateAsync(
        string providerId,
        ProviderPreferenceUpdateDto request,
        IProviderEnablementService enablement,
        CancellationToken ct)
    {
        // A missing field is a malformed request, never an implicit disable:
        // the repo has a real empty-body-PUT data-loss incident behind this.
        if (request?.Enabled is not { } enabled)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "invalid_provider_preference",
                detail: "Provide the 'enabled' value for this provider.");
        }

        var entry = await enablement.SetAsync(providerId, enabled, ct);
        return entry is null
            ? ProviderEndpoints.UnknownProvider(providerId)
            : Results.Ok(ToDto(entry));
    }

    private static ProviderSettingsItemDto ToDto(ProviderEnablementEntry entry) => new(
        entry.Provider.Provider.Id,
        entry.Provider.Provider.DisplayName,
        entry.Provider.Provider.Description,
        ProviderEndpoints.DescribeCapabilities(entry.Provider.Provider.Capabilities),
        entry.Provider.Provider.RightsNotice,
        entry.Enabled,
        entry.EnabledByDefault);
}

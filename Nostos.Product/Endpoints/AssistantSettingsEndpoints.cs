using Nostos.Backend.Integrations.Assistant;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Endpoints;

/// <summary>
/// The assistant settings surface (issue #262 §7). Two routes under one group:
/// <list type="bullet">
/// <item><c>GET /api/settings/assistant</c> — the effective settings; never 404s
/// and never carries anything but the one field.</item>
/// <item><c>PUT /api/settings/assistant</c> — replace the capture processing
/// mode. An unsupported value is a 400 with the stable title
/// <c>invalid_processing_mode</c> and stores nothing.</item>
/// </list>
///
/// Route naming follows the existing <c>/api/settings/ai-provider</c> surface.
/// </summary>
public static class AssistantSettingsEndpoints
{
    public const string Route = "/api/settings/assistant";

    public static IEndpointRouteBuilder MapAssistantSettingsEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(Route, GetAsync);
        routes.MapPut(Route, UpdateAsync);
        return routes;
    }

    private static async Task<IResult> GetAsync(
        IAssistantSettingsService settings,
        CancellationToken ct) =>
        Results.Ok(await settings.GetAsync(ct));

    private static async Task<IResult> UpdateAsync(
        AssistantSettingsUpdateRequest request,
        IAssistantSettingsService settings,
        CancellationToken ct)
    {
        var outcome = await settings.UpdateAsync(request, ct);
        return outcome.Success
            ? Results.Ok(outcome.Settings)
            : Failure(
                outcome.ErrorCode ?? AssistantErrorCodes.InvalidProcessingMode,
                "The capture processing mode must be one of the supported modes.");
    }

    private static IResult Failure(string code, string detail) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: code, detail: detail);
}

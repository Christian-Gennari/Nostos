using System.Text.Json;
using Nostos.Backend.Configuration;
using Nostos.Backend.Integrations.Assistant;
using Nostos.Backend.Services.Ai;
using Nostos.Shared.Dtos;
using Nostos.Product.Services.Ai;

namespace Nostos.Backend.Endpoints;

/// <summary>
/// Ask Nostos HTTP surface. The ordinary turn route remains the compatibility
/// path; the streamed route adds ordered product events without requiring token
/// streaming from the configured model provider.
/// </summary>
public static class AssistantEndpoints
{
    public const string StatusRoute = "/api/assistant/status";
    public const string TurnRoute = "/api/assistant/turn";
    public const string StreamTurnRoute = "/api/assistant/turn/stream";
    public const string CancelTurnRoute = "/api/assistant/turn/cancel";
    public const string ApproveRoute = "/api/assistant/plan/approve";

    private static readonly JsonSerializerOptions StreamJson =
        new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapAssistantEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/assistant");
        group.MapGet("/status", StatusAsync);
        group.MapPost("/turn", TurnAsync);
        group.MapPost("/turn/stream", StreamTurnAsync);
        group.MapPost("/turn/cancel", CancelTurn);
        group.MapPost("/plan/approve", ApproveAsync);
        return routes;
    }

    private static async Task<IResult> StatusAsync(
        IAiProviderConfigResolver config,
        IAiAccessPolicy access,
        CancellationToken ct)
    {
        if (!await access.IsAllowedAsync(ct))
            return Results.Ok(new AssistantStatusResponse(false));

        var effective = await config.GetEffectiveLlmAsync(ct);
        return Results.Ok(new AssistantStatusResponse(effective.IsAvailable));
    }

    private static async Task<IResult> TurnAsync(
        AssistantTurnRequest request,
        AssistantOrchestrator orchestrator,
        IAiProviderConfigResolver config,
        IAiAccessPolicy access,
        CancellationToken ct)
    {
        var unavailable = await UnavailableAsync(config, access, ct);
        if (unavailable is not null) return unavailable;

        try
        {
            return Results.Ok(await orchestrator.HandleTurnAsync(request, ct));
        }
        catch (AiUsageException ex)
        {
            return UsageFailure(ex);
        }
        catch (LlmException ex)
        {
            return Failure(ex.Code, StatusFor(ex.Code), ex.Message);
        }
    }

    private static async Task StreamTurnAsync(
        HttpContext http,
        AssistantTurnRequest request,
        AssistantOrchestrator orchestrator,
        AssistantTurnExecutionRegistry executions,
        IAiProviderConfigResolver config,
        IAiAccessPolicy access)
    {
        var conversationKey = AssistantTurnIdentity.ConversationKey(
            string.IsNullOrWhiteSpace(request.ConversationId)
                ? request.ClientId
                : request.ConversationId);
        var turnId = AssistantTurnIdentity.TurnKey(request);
        var writer = new TurnEventWriter(http.Response, turnId);

        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "application/x-ndjson; charset=utf-8";

        await writer.WriteAsync(
            AssistantTurnEventKinds.Started,
            requestAborted: http.RequestAborted);

        var unavailable = await UnavailableFailureAsync(config, access, http.RequestAborted);
        if (unavailable is not null)
        {
            await writer.WriteAsync(
                AssistantTurnEventKinds.Failed,
                failure: unavailable,
                requestAborted: http.RequestAborted);
            return;
        }

        using var execution = executions.TryBegin(
            conversationKey,
            turnId,
            http.RequestAborted);

        if (execution is null)
        {
            await writer.WriteAsync(
                AssistantTurnEventKinds.Failed,
                failure: new AssistantTurnFailureDto(
                    AssistantErrorCodes.TurnAlreadyRunning,
                    "This turn is already running.",
                    Retryable: false),
                requestAborted: http.RequestAborted);
            return;
        }

        try
        {
            var response = await orchestrator.HandleTurnAsync(
                request,
                activity => writer.WriteAsync(
                    AssistantTurnEventKinds.Activity,
                    activity: activity,
                    requestAborted: http.RequestAborted),
                execution.Token);

            if (response.Error?.Code == AssistantErrorCodes.TurnCancelled)
            {
                await writer.WriteAsync(
                    AssistantTurnEventKinds.Cancelled,
                    failure: new AssistantTurnFailureDto(
                        response.Error.Code,
                        response.Error.Message,
                        Retryable: false),
                    response: response,
                    requestAborted: http.RequestAborted);
            }
            else if (response.Error is { } turnError)
            {
                await writer.WriteAsync(
                    AssistantTurnEventKinds.Failed,
                    failure: new AssistantTurnFailureDto(
                        turnError.Code,
                        turnError.Message,
                        Retryable: Retryable(turnError.Code)),
                    response: response,
                    requestAborted: http.RequestAborted);
            }
            else
            {
                await writer.WriteAsync(
                    AssistantTurnEventKinds.Completed,
                    response: response,
                    requestAborted: http.RequestAborted);
            }
        }
        catch (AiUsageException ex)
        {
            await writer.WriteAsync(
                AssistantTurnEventKinds.Failed,
                failure: UsageEventFailure(ex),
                requestAborted: http.RequestAborted);
        }
        catch (LlmException ex)
        {
            await writer.WriteAsync(
                AssistantTurnEventKinds.Failed,
                failure: LlmEventFailure(ex),
                requestAborted: http.RequestAborted);
        }
        catch (OperationCanceledException) when (!http.RequestAborted.IsCancellationRequested)
        {
            await writer.WriteAsync(
                AssistantTurnEventKinds.Cancelled,
                failure: new AssistantTurnFailureDto(
                    AssistantErrorCodes.TurnCancelled,
                    "Stopped.",
                    Retryable: false),
                requestAborted: http.RequestAborted);
        }
    }

    private static IResult CancelTurn(
        AssistantTurnCancelRequest request,
        AssistantTurnExecutionRegistry executions)
    {
        if (string.IsNullOrWhiteSpace(request.TurnId))
        {
            return Failure(
                AssistantErrorCodes.InvalidArguments,
                StatusCodes.Status400BadRequest,
                "A TurnId is required.");
        }

        var conversationKey = AssistantTurnIdentity.ConversationKey(request.ConversationId);
        var accepted = executions.TryCancel(conversationKey, request.TurnId.Trim());
        return Results.Ok(new AssistantTurnCancelResponse(
            accepted,
            accepted ? "cancel_requested" : "not_active"));
    }

    private static async Task<IResult> ApproveAsync(
        AssistantPlanApproveRequest request,
        AssistantOrchestrator orchestrator,
        IAiProviderConfigResolver config,
        IAiAccessPolicy access,
        CancellationToken ct)
    {
        var unavailable = await UnavailableAsync(config, access, ct);
        if (unavailable is not null) return unavailable;

        var response = await orchestrator.ApproveAsync(request.PlanId, request.ApprovalToken, ct);
        if (response.Success)
            return Results.Ok(response);

        var code = response.ErrorCode ?? AssistantErrorCodes.NotFound;
        return Failure(code, StatusForApproval(code), response.ErrorMessage ?? "The plan was refused.");
    }

    private static async Task<AssistantTurnFailureDto?> UnavailableFailureAsync(
        IAiProviderConfigResolver config,
        IAiAccessPolicy access,
        CancellationToken ct)
    {
        if (!await access.IsAllowedAsync(ct))
        {
            return new AssistantTurnFailureDto(
                LlmErrorCodes.AccessDenied,
                "Ask Nostos is not available for this account.",
                Retryable: false);
        }

        var effective = await config.GetEffectiveLlmAsync(ct);
        if (effective.IsAvailable) return null;

        return effective.Enabled
            ? new AssistantTurnFailureDto(
                LlmErrorCodes.NotConfigured,
                "Ask Nostos needs an AI provider configured in Settings.",
                Retryable: false)
            : new AssistantTurnFailureDto(
                LlmErrorCodes.Disabled,
                "Ask Nostos is disabled by this Nostos host.",
                Retryable: false);
    }

    private static async Task<IResult?> UnavailableAsync(
        IAiProviderConfigResolver config,
        IAiAccessPolicy access,
        CancellationToken ct)
    {
        if (!await access.IsAllowedAsync(ct))
        {
            return Failure(
                LlmErrorCodes.AccessDenied,
                StatusCodes.Status403Forbidden,
                "The host policy does not allow Ask Nostos for this request.");
        }

        var effective = await config.GetEffectiveLlmAsync(ct);
        if (effective.IsAvailable) return null;

        return effective.Enabled
            ? Failure(
                LlmErrorCodes.NotConfigured,
                StatusCodes.Status503ServiceUnavailable,
                LlmException.NotConfigured(effective.ApiKeyEnvironmentVariable).Message)
            : Failure(
                LlmErrorCodes.Disabled,
                StatusCodes.Status503ServiceUnavailable,
                LlmException.Disabled().Message);
    }

    private static IResult UsageFailure(AiUsageException exception) =>
        exception.Reason switch
        {
            AiUsageBlockReason.AccessDenied =>
                Failure("ai_access_denied", StatusCodes.Status403Forbidden, exception.Message),
            AiUsageBlockReason.RateLimited =>
                Failure("ai_rate_limited", StatusCodes.Status429TooManyRequests, exception.Message),
            AiUsageBlockReason.LimitReached =>
                Failure("ai_usage_limit_reached", StatusCodes.Status429TooManyRequests, exception.Message),
            AiUsageBlockReason.Disabled =>
                Failure("ai_disabled", StatusCodes.Status503ServiceUnavailable, exception.Message),
            _ =>
                Failure("ai_temporarily_unavailable", StatusCodes.Status503ServiceUnavailable, exception.Message),
        };

    private static AssistantTurnFailureDto UsageEventFailure(AiUsageException exception) =>
        exception.Reason switch
        {
            AiUsageBlockReason.AccessDenied =>
                new("ai_access_denied", "Ask Nostos is not available for this account.", false),
            AiUsageBlockReason.RateLimited =>
                new("ai_rate_limited", "Ask Nostos is temporarily busy. Try again shortly.", true),
            AiUsageBlockReason.LimitReached =>
                new("ai_usage_limit_reached", "Your AI allowance is used up for now.", false),
            AiUsageBlockReason.Disabled =>
                new("ai_disabled", "Managed AI is temporarily disabled.", true),
            _ =>
                new("ai_temporarily_unavailable", "Ask Nostos is temporarily unavailable.", true),
        };

    private static AssistantTurnFailureDto LlmEventFailure(LlmException exception) =>
        exception.Code switch
        {
            LlmErrorCodes.Timeout =>
                new(exception.Code, "Ask Nostos took too long to answer. Try again.", true),
            LlmErrorCodes.RateLimited =>
                new(exception.Code, "Ask Nostos is temporarily busy. Try again shortly.", true),
            LlmErrorCodes.NotConfigured =>
                new(exception.Code, "Ask Nostos needs an AI provider configured in Settings.", false),
            LlmErrorCodes.Disabled =>
                new(exception.Code, "Ask Nostos is disabled by this Nostos host.", false),
            LlmErrorCodes.AccessDenied =>
                new(exception.Code, "Ask Nostos is not available for this account.", false),
            _ =>
                new(exception.Code, "The AI service could not complete this turn. Try again.", true),
        };

    private static bool Retryable(string code) =>
        code is LlmErrorCodes.Timeout
            or LlmErrorCodes.Provider
            or LlmErrorCodes.InvalidResponse
            or LlmErrorCodes.RateLimited
            or "ai_rate_limited"
            or "ai_temporarily_unavailable";

    private static int StatusFor(string code) => code switch
    {
        LlmErrorCodes.AccessDenied => StatusCodes.Status403Forbidden,
        LlmErrorCodes.Disabled or LlmErrorCodes.NotConfigured => StatusCodes.Status503ServiceUnavailable,
        LlmErrorCodes.RateLimited => StatusCodes.Status429TooManyRequests,
        LlmErrorCodes.Timeout => StatusCodes.Status504GatewayTimeout,
        LlmErrorCodes.Permission => StatusCodes.Status502BadGateway,
        _ => StatusCodes.Status502BadGateway,
    };

    private static int StatusForApproval(string code) => code switch
    {
        AssistantErrorCodes.NotFound => StatusCodes.Status404NotFound,
        AssistantErrorCodes.ApprovalPlanMismatch => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status400BadRequest,
    };

    private static IResult Failure(string code, int statusCode, string detail) =>
        Results.Problem(statusCode: statusCode, title: code, detail: detail);

    private sealed class TurnEventWriter(HttpResponse response, string turnId)
    {
        private long _sequence;

        public async ValueTask WriteAsync(
            string kind,
            AssistantTurnActivityDto? activity = null,
            AssistantTurnFailureDto? failure = null,
            AssistantTurnResponse? responseBody = null,
            CancellationToken requestAborted = default)
        {
            var turnEvent = new AssistantTurnEventDto(
                turnId,
                Interlocked.Increment(ref _sequence),
                kind,
                activity,
                failure,
                responseBody);

            await response.WriteAsync(
                JsonSerializer.Serialize(turnEvent, StreamJson) + "\n",
                requestAborted);
            await response.Body.FlushAsync(requestAborted);
        }
    }
}

public sealed record AssistantStatusResponse(bool Available);

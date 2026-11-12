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
    public const string ChangesetRoute = "/api/assistant/changesets/{changesetId}";

    private static readonly JsonSerializerOptions StreamJson =
        new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapAssistantEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/assistant");
        group.MapGet("/status", StatusAsync);
        group.MapPost("/turn", TurnAsync);
        group.MapPost("/turn/stream", StreamTurnAsync);
        group.MapPost("/turn/cancel", CancelTurn);
        // Changeset actions touch only the library, never the model, so they
        // stay available when the AI provider is not: a review can still be
        // applied or discarded and an applied change can still be undone.
        var changesets = group.MapGroup("/changesets/{changesetId}");
        changesets.MapPost("/preview", (string changesetId, AssistantChangesetPreviewRequest request,
            AssistantChangesetCoordinator coordinator, CancellationToken ct) =>
            ActionResultAsync(coordinator.PreviewAsync(changesetId, request, ct)));
        changesets.MapPost("/apply", (string changesetId, AssistantChangesetApplyRequest request,
            AssistantChangesetCoordinator coordinator, CancellationToken ct) =>
            ActionResultAsync(coordinator.ApplyAsync(changesetId, request, ct)));
        changesets.MapPost("/discard", (string changesetId, AssistantChangesetTokenRequest request,
            AssistantChangesetCoordinator coordinator) =>
            ActionResult(coordinator.Discard(changesetId, request)));
        changesets.MapPost("/undo", (string changesetId, AssistantChangesetTokenRequest request,
            AssistantChangesetCoordinator coordinator, CancellationToken ct) =>
            ActionResultAsync(coordinator.UndoAsync(changesetId, request, ct)));
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
        HttpContext http,
        AssistantTurnRequest request,
        AssistantOrchestrator orchestrator,
        IAiProviderConfigResolver config,
        IAiAccessPolicy access,
        CancellationToken ct)
    {
        var managed = ManagedAi(http);
        var unavailable = await UnavailableAsync(config, access, managed, ct);
        if (unavailable is not null) return unavailable;

        try
        {
            return Results.Ok(await orchestrator.HandleTurnAsync(request, ct));
        }
        catch (AssistantTurnAbortedException ex)
        {
            // The turn committed writes and a later provider call failed (#1558):
            // the body carries those outcomes beside the typed failure.
            return Failure(
                ex.Cause.Code,
                StatusFor(ex.Cause.Code),
                LlmEventFailure(ex.Cause, managed).Message,
                ex.Committed);
        }
        catch (AiUsageException ex)
        {
            return Failure(ex.Code, ex.StatusCode, ex.Message);
        }
        catch (LlmException ex)
        {
            // The same plain words as the stream: the exception's own message
            // can carry the upstream body (an HTML error page, a gateway's
            // text), which is for the log, not for an API client.
            return Failure(ex.Code, StatusFor(ex.Code), LlmEventFailure(ex, managed).Message);
        }
    }

    private static async Task StreamTurnAsync(
        HttpContext http,
        AssistantTurnRequest request,
        AssistantOrchestrator orchestrator,
        AssistantTurnExecutionRegistry executions,
        IAssistantOwner owner,
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

        using var execution = executions.TryBegin(
            owner.Key,
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
            // A client may expose Stop only after observing this event. Register
            // the exact turn first so "started" is a truthful acknowledgement
            // that /cancel can already target it.
            await writer.WriteAsync(
                AssistantTurnEventKinds.Started,
                requestAborted: http.RequestAborted);

            var unavailable = await UnavailableFailureAsync(config, access, ManagedAi(http), execution.Token);
            if (unavailable is not null)
            {
                await writer.WriteAsync(
                    AssistantTurnEventKinds.Failed,
                    failure: unavailable,
                    requestAborted: http.RequestAborted);
                return;
            }

            var response = await orchestrator.HandleTurnAsync(
                request,
                activity => writer.WriteAsync(
                    AssistantTurnEventKinds.Activity,
                    activity: activity,
                    requestAborted: http.RequestAborted),
                execution,
                execution.Token);

            if (response.Error?.Code == AssistantErrorCodes.TurnCancelled)
            {
                await writer.WriteAsync(
                    AssistantTurnEventKinds.Cancelled,
                    failure: new AssistantTurnFailureDto(
                        response.Error.Code,
                        response.Error.Message,
                        Retryable: false),
                    responseBody: response,
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
                    responseBody: response,
                    requestAborted: http.RequestAborted);
            }
            else
            {
                await writer.WriteAsync(
                    AssistantTurnEventKinds.Completed,
                    responseBody: response,
                    requestAborted: http.RequestAborted);
            }
        }
        catch (AssistantTurnAbortedException ex)
        {
            // A committed capture (or another write) survives the provider
            // failure: the failed event keeps the outcomes so the panel can say
            // what was saved before the failure (#1558).
            await writer.WriteAsync(
                AssistantTurnEventKinds.Failed,
                failure: LlmEventFailure(ex.Cause, ManagedAi(http)),
                responseBody: ex.Committed,
                requestAborted: http.RequestAborted);
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
                failure: LlmEventFailure(ex, ManagedAi(http)),
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
        AssistantTurnExecutionRegistry executions,
        IAssistantOwner owner)
    {
        if (string.IsNullOrWhiteSpace(request.TurnId))
        {
            return Failure(
                AssistantErrorCodes.InvalidArguments,
                StatusCodes.Status400BadRequest,
                "A TurnId is required.");
        }

        var conversationKey = AssistantTurnIdentity.ConversationKey(request.ConversationId);
        var accepted = executions.TryCancel(owner.Key, conversationKey,request.TurnId.Trim());
        return Results.Ok(new AssistantTurnCancelResponse(
            accepted,
            accepted ? "cancel_requested" : "not_active"));
    }

    private static async Task<IResult> ActionResultAsync(Task<AssistantChangesetActionResponse> action) =>
        ActionResult(await action);

    /// <summary>
    /// Failures are data in the body. Only an unknown changeset and a wrong
    /// token change the status, so a client never mistakes them for a refusal
    /// it can resolve by retrying.
    /// </summary>
    private static IResult ActionResult(AssistantChangesetActionResponse response) =>
        response.ErrorCode switch
        {
            AssistantErrorCodes.ChangesetNotFound => Results.NotFound(response),
            AssistantErrorCodes.ChangesetTokenMismatch => Results.Json(response, statusCode: StatusCodes.Status403Forbidden),
            _ => Results.Ok(response),
        };

    private static async Task<AssistantTurnFailureDto?> UnavailableFailureAsync(
        IAiProviderConfigResolver config,
        IAiAccessPolicy access,
        bool managed,
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
                managed ? ManagedUnavailable : "Ask Nostos needs an AI provider configured in Settings.",
                Retryable: false)
            : new AssistantTurnFailureDto(
                LlmErrorCodes.Disabled,
                "Ask Nostos is disabled by this Nostos host.",
                Retryable: false);
    }

    private static async Task<IResult?> UnavailableAsync(
        IAiProviderConfigResolver config,
        IAiAccessPolicy access,
        bool managed,
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
                // The operator's variable name helps a SelfHosted owner and
                // must never reach a managed deployment's users.
                managed ? ManagedUnavailable : LlmException.NotConfigured(effective.ApiKeyEnvironmentVariable).Message)
            : Failure(
                LlmErrorCodes.Disabled,
                StatusCodes.Status503ServiceUnavailable,
                LlmException.Disabled().Message);
    }

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

    /// <summary>
    /// True where the host runs the AI and the user has no provider setting
    /// (Nostos Cloud). Advice to "check Settings" is then wrong, and the
    /// operator's configuration (a key's variable name) must not show (#1479).
    /// </summary>
    private static bool ManagedAi(HttpContext http) =>
        http.RequestServices.GetService<DeploymentDescriptor>() is { } deployment
        && !deployment.Capabilities.CanConfigureAiProvider;

    private const string ManagedUnavailable = "Ask Nostos is temporarily unavailable.";

    private static AssistantTurnFailureDto LlmEventFailure(LlmException exception, bool managed) =>
        exception.Code switch
        {
            LlmErrorCodes.NotConfigured or LlmErrorCodes.Permission when managed =>
                new(exception.Code, ManagedUnavailable, false),
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
            // A refused key does not fix itself: trying again cannot help,
            // checking the provider can (#1466).
            LlmErrorCodes.Permission =>
                new(exception.Code, "The AI service did not accept its key. Check the AI provider in Settings.", false),
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

    /// <summary>
    /// The failure body. Nothing committed keeps the exact problem-details shape
    /// clients already read; a turn that committed writes before failing appends
    /// the optional <c>committed</c> response object (#1558), so an older client
    /// simply ignores it.
    /// </summary>
    private static IResult Failure(
        string code,
        int statusCode,
        string detail,
        AssistantTurnResponse? committed = null) =>
        committed is null
            ? Results.Problem(statusCode: statusCode, title: code, detail: detail)
            : Results.Problem(
                statusCode: statusCode,
                title: code,
                detail: detail,
                extensions: new Dictionary<string, object?> { ["committed"] = committed });

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

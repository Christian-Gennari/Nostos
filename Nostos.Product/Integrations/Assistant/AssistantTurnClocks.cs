using Microsoft.Extensions.Logging;
using Nostos.Backend.Configuration;
using Nostos.Backend.Services.Ai;

namespace Nostos.Backend.Integrations.Assistant;

/// <summary>What one provider call did under the turn's clocks.</summary>
internal enum AssistantUpstreamOutcome
{
    /// <summary>The provider answered; <see cref="AssistantUpstreamResult.Completion"/> is set.</summary>
    Completed,

    /// <summary>The caller cancelled the turn.</summary>
    Cancelled,

    /// <summary>The request's own clock fired: a typed provider timeout.</summary>
    RequestTimedOut,

    /// <summary>The whole turn's wall-clock budget ran out during the call.</summary>
    TurnCeilingReached,

    /// <summary>The provider failed; <see cref="AssistantUpstreamResult.Failure"/> is set.</summary>
    ProviderFailed,
}

/// <summary>One provider call's outcome.</summary>
internal readonly record struct AssistantUpstreamResult(
    AssistantUpstreamOutcome Outcome,
    LlmCompletion? Completion = null,
    LlmException? Failure = null);

/// <summary>
/// The time one more upstream call may spend: the request's own timeout and what
/// is left of the whole turn.
/// </summary>
internal readonly record struct AssistantTurnWindow(
    long RequestTimeoutMilliseconds,
    long? RemainingTurnMilliseconds)
{
    /// <summary>
    /// Only a request that can finish inside what is left of the turn gets its
    /// own clock; otherwise the turn clock is the deliberate failure, because it
    /// has nothing left to order against.
    /// </summary>
    public bool RequestClockFitsInTurn =>
        RequestTimeoutMilliseconds > 0
        && (RemainingTurnMilliseconds is not { } remaining
            || RequestTimeoutMilliseconds < remaining);
}

/// <summary>
/// The turn's wall clocks (issue #406, #1561), innermost first:
///
/// <list type="number">
/// <item>one provider request — <see cref="AssistantOptions.RequestTimeoutSeconds"/>,
/// typed as a provider timeout;</item>
/// <item>the whole turn — <see cref="AssistantOptions.MaxTurnElapsedMilliseconds"/>,
/// typed as the execution budget.</item>
/// </list>
///
/// Both use the turn's <see cref="TimeProvider"/>. A request gets the smaller of
/// its own timeout and the remaining turn budget; when the turn has less than a
/// full request timeout left, the turn clock is the one that fires, because it
/// has nothing left to order against. The startup validation guarantees a full
/// request timeout fits inside this turn budget in a fresh turn.
///
/// This type decides nothing about the turn beyond time: it does not record
/// usage, settle a lease, or choose a stop reason. It exists so the ordering of
/// the two clocks (and which of them an <see cref="OperationCanceledException"/>
/// belongs to) is written down in one place.
/// </summary>
internal sealed class AssistantTurnClocks(
    AssistantOptions options,
    TimeProvider time,
    ILogger logger)
{
    /// <summary>
    /// The window one more upstream call may spend, or null when the turn must
    /// stop spending them: a configured ceiling is reached, or no turn time is
    /// left.
    /// </summary>
    public AssistantTurnWindow? Open(AssistantExecutionUsage usage)
    {
        var exceededCeiling = AssistantExecutionBudget.ExceededCeiling(usage, options);
        if (exceededCeiling is not null)
        {
            logger.LogDebug(
                "Assistant turn stopped at the {Ceiling} execution ceiling.",
                exceededCeiling);
            return null;
        }

        var requestTimeoutMilliseconds = AssistantExecutionBudget.RequestTimeoutMilliseconds(options);
        long? remainingTurnMilliseconds = options.MaxTurnElapsedMilliseconds > 0
            ? options.MaxTurnElapsedMilliseconds - usage.ElapsedMilliseconds
            : null;
        if (remainingTurnMilliseconds is <= 0)
        {
            return null;
        }

        return new AssistantTurnWindow(requestTimeoutMilliseconds, remainingTurnMilliseconds);
    }

    /// <summary>Starts one provider call's clocks. Dispose it when the call is over.</summary>
    public AssistantTurnCall Begin(AssistantTurnWindow window, CancellationToken ct) =>
        new(window, time, options, logger, ct);
}

/// <summary>
/// One provider call's live clocks. Disposing it disposes every timer it made,
/// exactly as the loop's own <c>finally</c> used to.
/// </summary>
internal sealed class AssistantTurnCall : IDisposable
{
    private readonly CancellationToken _turnCancellation;
    private readonly CancellationTokenSource? _turnTimer;
    private readonly CancellationTokenSource? _requestTimer;
    private readonly CancellationTokenSource? _upstreamLink;
    private readonly ILogger _logger;
    private readonly AssistantOptions _options;

    public AssistantTurnCall(
        AssistantTurnWindow window,
        TimeProvider time,
        AssistantOptions options,
        ILogger logger,
        CancellationToken ct)
    {
        _turnCancellation = ct;
        _logger = logger;
        _options = options;

        if (window.RemainingTurnMilliseconds is { } turnMilliseconds)
        {
            _turnTimer = new CancellationTokenSource(
                TimeSpan.FromMilliseconds(turnMilliseconds),
                time);
        }

        // Only a request that can finish inside what is left of the turn gets
        // its own clock; otherwise the turn clock is the deliberate failure.
        if (window.RequestClockFitsInTurn)
        {
            _requestTimer = new CancellationTokenSource(
                TimeSpan.FromMilliseconds(window.RequestTimeoutMilliseconds),
                time);
        }

        if (_turnTimer is not null || _requestTimer is not null)
        {
            var tokens = new List<CancellationToken>(3) { ct };
            if (_requestTimer is not null) tokens.Add(_requestTimer.Token);
            if (_turnTimer is not null) tokens.Add(_turnTimer.Token);
            _upstreamLink = CancellationTokenSource.CreateLinkedTokenSource(tokens.ToArray());
            Token = _upstreamLink.Token;
        }
        else
        {
            Token = ct;
        }
    }

    /// <summary>The token the provider call observes: the turn, linked to its clocks.</summary>
    public CancellationToken Token { get; }

    /// <summary>
    /// Runs one provider call and reports what its clocks did. No exception
    /// escapes for cancellation or a provider failure: the caller decides what a
    /// stop or a typed failure means for the turn's outcomes.
    /// </summary>
    public async Task<AssistantUpstreamResult> CompleteAsync(
        ILlmProvider llm,
        LlmCompletionRequest request)
    {
        try
        {
            var completion = await llm.CompleteAsync(request, Token);
            return new AssistantUpstreamResult(AssistantUpstreamOutcome.Completed, completion);
        }
        catch (OperationCanceledException) when (_turnCancellation.IsCancellationRequested)
        {
            return new AssistantUpstreamResult(AssistantUpstreamOutcome.Cancelled);
        }
        catch (OperationCanceledException) when (_requestTimer?.IsCancellationRequested == true)
        {
            // The request's own clock fired before the turn's, so the person gets
            // the retryable provider timeout, not "execution limit".
            _logger.LogDebug(
                "Ask Nostos provider request exceeded its {TimeoutSeconds} s timeout; " +
                "the turn budget was not reached.",
                _options.RequestTimeoutSeconds);
            return new AssistantUpstreamResult(
                AssistantUpstreamOutcome.RequestTimedOut,
                Failure: LlmException.TimedOut());
        }
        catch (OperationCanceledException) when (_turnTimer?.IsCancellationRequested == true)
        {
            _logger.LogDebug(
                "Assistant turn stopped at the wall-clock execution ceiling during an upstream call.");
            return new AssistantUpstreamResult(AssistantUpstreamOutcome.TurnCeilingReached);
        }
        catch (LlmException exception)
        {
            return new AssistantUpstreamResult(
                AssistantUpstreamOutcome.ProviderFailed,
                Failure: exception);
        }
    }

    public void Dispose()
    {
        _upstreamLink?.Dispose();
        _requestTimer?.Dispose();
        _turnTimer?.Dispose();
    }
}

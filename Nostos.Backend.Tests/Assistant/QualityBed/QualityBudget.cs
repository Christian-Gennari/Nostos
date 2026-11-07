// Spend cap for the live quality bed (issue #1466).
//
// A live campaign calls a paid model. The cap is enforced here, in the
// harness, so it holds however the bed is started (a workflow, a laptop):
//
//   NOSTOS_QG_MAX_USD                  stop once this much is spent (required live)
//   NOSTOS_QG_PRICE_INPUT_PER_MTOK     USD per million prompt tokens (required with a cap)
//   NOSTOS_QG_PRICE_OUTPUT_PER_MTOK    USD per million completion tokens (required with a cap)
//   NOSTOS_QG_MAX_SCENARIO_RUNS        refuse to start a campaign larger than this
//
// The cap is checked before every upstream call, so a campaign overshoots
// it by at most one call. A call whose usage the provider does not report is
// charged an estimate from the request and answer length, never nothing.
namespace Nostos.Backend.Tests.Assistant.QualityBed;

using System.Globalization;
using Nostos.Backend.Services.Ai;

internal sealed class QualityBudgetExceededException(string message) : Exception(message);

internal sealed class QualityBudget
{
    public const string MaxUsdVariable = "NOSTOS_QG_MAX_USD";
    public const string InputPriceVariable = "NOSTOS_QG_PRICE_INPUT_PER_MTOK";
    public const string OutputPriceVariable = "NOSTOS_QG_PRICE_OUTPUT_PER_MTOK";
    public const string MaxScenarioRunsVariable = "NOSTOS_QG_MAX_SCENARIO_RUNS";

    private readonly object _lock = new();
    private long _promptTokens;
    private long _completionTokens;
    private int _calls;
    private int _estimatedCalls;

    public QualityBudget(decimal? maxUsd, decimal inputPerMTok, decimal outputPerMTok, int? maxScenarioRuns)
    {
        MaxUsd = maxUsd;
        InputPerMTok = inputPerMTok;
        OutputPerMTok = outputPerMTok;
        MaxScenarioRuns = maxScenarioRuns;
    }

    public decimal? MaxUsd { get; }

    public decimal InputPerMTok { get; }

    public decimal OutputPerMTok { get; }

    public int? MaxScenarioRuns { get; }

    private static readonly Lazy<QualityBudget> ProcessBudget = new(FromEnvironment);

    /// <summary>
    /// The one cap for every live campaign in this test process, so a run of
    /// Ask Nostos and MCP together spends one NOSTOS_QG_MAX_USD, not one each.
    /// </summary>
    public static QualityBudget Process => ProcessBudget.Value;

    /// <summary>No cap: deterministic runs and the offline wiring proof.</summary>
    public static QualityBudget Unlimited { get; } = new(null, 0, 0, null);

    public static QualityBudget FromEnvironment()
    {
        var maxUsd = Decimal(MaxUsdVariable);
        var input = Decimal(InputPriceVariable);
        var output = Decimal(OutputPriceVariable);
        var runs = int.TryParse(
            Environment.GetEnvironmentVariable(MaxScenarioRunsVariable),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : (int?)null;
        if (maxUsd is not null && (input is null || output is null))
            throw new InvalidOperationException(
                $"{MaxUsdVariable} needs {InputPriceVariable} and {OutputPriceVariable}: " +
                "a cap without prices cannot be enforced.");
        return new QualityBudget(maxUsd, input ?? 0, output ?? 0, runs);
    }

    public decimal SpentUsd
    {
        get
        {
            lock (_lock)
                return Cost(_promptTokens, _completionTokens);
        }
    }

    public int Calls
    {
        get { lock (_lock) return _calls; }
    }

    public int EstimatedCalls
    {
        get { lock (_lock) return _estimatedCalls; }
    }

    public bool Exhausted => MaxUsd is { } cap && SpentUsd >= cap;

    /// <summary>Refuses a campaign larger than the configured number of scenario runs.</summary>
    public void CheckCampaignSize(int scenarioRuns)
    {
        if (MaxScenarioRuns is { } max && scenarioRuns > max)
            throw new QualityBudgetExceededException(
                $"the campaign would run {scenarioRuns} scenario runs, over {MaxScenarioRunsVariable}={max}");
    }

    /// <summary>Called before each upstream call.</summary>
    public void ThrowIfExhausted()
    {
        if (Exhausted)
            throw new QualityBudgetExceededException(Describe());
    }

    public void Record(LlmCompletionRequest request, LlmCompletion completion)
    {
        lock (_lock)
        {
            _calls++;
            if (completion.PromptTokens is { } prompt && completion.CompletionTokens is { } answer)
            {
                // Reasoning tokens are reported inside completion_tokens.
                _promptTokens += prompt;
                _completionTokens += answer;
                return;
            }

            // No usage reported: charge roughly three characters a token, so
            // an unmetered gateway still counts against the cap.
            _estimatedCalls++;
            var promptChars = request.Messages.Sum(m => (long)(m.Content?.Length ?? 0))
                + request.Tools.Sum(t => (long)(t.Description.Length + t.ParametersJsonSchema.Length));
            var answerChars = (completion.Content?.Length ?? 0)
                + completion.ToolCalls.Sum(c => c.Name.Length + c.ArgumentsJson.Length);
            _promptTokens += completion.PromptTokens ?? promptChars / 3 + 1;
            _completionTokens += completion.CompletionTokens ?? answerChars / 3 + 1;
        }
    }

    public string Describe()
    {
        lock (_lock)
            return string.Create(
                CultureInfo.InvariantCulture,
                $"spent ${Cost(_promptTokens, _completionTokens):F4} of the ${MaxUsd:F2} cap " +
                $"({_calls} calls, {_promptTokens} prompt / {_completionTokens} completion tokens, " +
                $"{_estimatedCalls} calls estimated)");
    }

    private decimal Cost(long prompt, long completion) =>
        (prompt * InputPerMTok + completion * OutputPerMTok) / 1_000_000m;

    private static decimal? Decimal(string name) =>
        decimal.TryParse(
            Environment.GetEnvironmentVariable(name),
            NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value >= 0
            ? value
            : null;
}

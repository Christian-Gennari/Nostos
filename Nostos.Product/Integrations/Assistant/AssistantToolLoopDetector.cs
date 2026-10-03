using System.Text;
using Nostos.Backend.Services.Ai;

namespace Nostos.Backend.Integrations.Assistant;

/// <summary>
/// Detects the narrow runaway pattern that is always redundant for today's
/// in-process Nostos tools: the model asks for the same ordered tool batch with
/// JSON-equivalent arguments on two consecutive completion rounds.
///
/// Tool-call ids are intentionally ignored. Object property order and
/// whitespace are normalized, while array order is preserved because it can be
/// semantically meaningful.
/// </summary>
public sealed class AssistantToolLoopDetector
{
    private string? _previousBatchFingerprint;

    public bool IsImmediateRepeat(IReadOnlyList<LlmToolCall> calls)
    {
        ArgumentNullException.ThrowIfNull(calls);

        if (calls.Count == 0)
        {
            _previousBatchFingerprint = null;
            return false;
        }

        var current = Fingerprint(calls);
        var repeated = string.Equals(
            current,
            _previousBatchFingerprint,
            StringComparison.Ordinal);

        _previousBatchFingerprint = current;
        return repeated;
    }

    public static string Fingerprint(IReadOnlyList<LlmToolCall> calls)
    {
        ArgumentNullException.ThrowIfNull(calls);

        var builder = new StringBuilder();
        foreach (var call in calls)
        {
            builder.Append(call.Name);
            builder.Append('\n');
            builder.Append(AssistantMutationIdentity.CanonicalizeArguments(call.ArgumentsJson));
            builder.Append("\n--\n");
        }

        return builder.ToString();
    }
}

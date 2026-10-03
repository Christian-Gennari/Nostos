using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nostos.Backend.Services.Ai;

/// <summary>
/// Shared raw-HTTP serializer/parser for OpenAI-compatible chat-completions
/// providers. Provider-specific application logic stays outside this transport.
/// Unknown assistant-message fields are carried through <see cref="LlmMessage.ProviderState"/>
/// so gateway metadata needed by a later tool round can be replayed unchanged.
/// </summary>
public static class OpenAiCompatibleChatCompletions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static Uri BuildUri(string baseUrl) =>
        new($"{baseUrl.TrimEnd('/')}/chat/completions");

    public static string BuildRequestBody(
        LlmCompletionRequest request,
        string model,
        int? maxTokensCeiling = null,
        string? reasoningEffort = null)
    {
        var maxTokens = maxTokensCeiling is { } ceiling
            ? Math.Clamp(request.MaxTokens, 1, ceiling)
            : Math.Max(1, request.MaxTokens);

        var root = new JsonObject
        {
            ["model"] = model,
            ["stream"] = false,
            ["max_tokens"] = maxTokens,
        };

        var messages = new JsonArray();
        foreach (var message in request.Messages)
            messages.Add(MessagePayload(message));
        root["messages"] = messages;

        if (!string.IsNullOrWhiteSpace(reasoningEffort))
            root["reasoning_effort"] = reasoningEffort.Trim().ToLowerInvariant();

        if (request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var tool in request.Tools)
            {
                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = ParseSchema(tool.ParametersJsonSchema),
                    },
                });
            }

            root["tools"] = tools;
        }

        return root.ToJsonString(JsonOptions);
    }

    public static LlmCompletion ParseResponse(string body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw LlmException.InvalidResponse("the AI provider returned unreadable JSON.");
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                throw LlmException.InvalidResponse("the AI provider returned no choice.");
            }

            var choice = choices[0];
            choice.TryGetProperty("finish_reason", out var finish);
            choice.TryGetProperty("message", out var message);

            if (message.ValueKind != JsonValueKind.Object)
                throw LlmException.InvalidResponse("the AI provider returned no assistant message.");

            var content = message.TryGetProperty("content", out var contentElement)
                && contentElement.ValueKind == JsonValueKind.String
                    ? contentElement.GetString()
                    : null;

            var toolCalls = ParseToolCalls(message);
            var providerState = toolCalls.Count > 0 ? message.GetRawText() : null;

            int? promptTokens = null;
            int? completionTokens = null;
            int? thinkingTokens = null;

            if (document.RootElement.TryGetProperty("usage", out var usage)
                && usage.ValueKind == JsonValueKind.Object)
            {
                promptTokens = ReadInt(usage, "prompt_tokens");
                completionTokens = ReadInt(usage, "completion_tokens");

                if (usage.TryGetProperty("completion_tokens_details", out var details)
                    && details.ValueKind == JsonValueKind.Object)
                {
                    thinkingTokens = ReadInt(details, "reasoning_tokens");
                }

                var totalTokens = ReadInt(usage, "total_tokens");
                if (completionTokens is null
                    && totalTokens is { } total
                    && promptTokens is { } prompt
                    && total >= prompt)
                {
                    completionTokens = total - prompt;
                }
            }

            return new LlmCompletion(
                content,
                finish.ValueKind == JsonValueKind.String ? finish.GetString() : null,
                toolCalls,
                promptTokens,
                completionTokens,
                thinkingTokens,
                providerState);
        }
    }

    private static JsonObject MessagePayload(LlmMessage message)
    {
        if (string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)
            && TryReadProviderMessage(message.ProviderState) is { } providerMessage)
        {
            // The raw provider assistant message may contain opaque fields inside
            // tool_calls (for example a thought signature). Preserve it and only
            // normalize the shared fields Nostos owns.
            providerMessage["role"] = "assistant";
            providerMessage["content"] = message.Content;

            if (providerMessage["tool_calls"] is null && message.ToolCalls is { Count: > 0 })
                providerMessage["tool_calls"] = ToolCallsPayload(message.ToolCalls);

            return providerMessage;
        }

        var payload = new JsonObject
        {
            ["role"] = message.Role,
            ["content"] = message.Content,
        };

        if (!string.IsNullOrWhiteSpace(message.ToolCallId))
            payload["tool_call_id"] = message.ToolCallId;

        if (message.ToolCalls is { Count: > 0 })
            payload["tool_calls"] = ToolCallsPayload(message.ToolCalls);

        return payload;
    }

    private static JsonArray ToolCallsPayload(IReadOnlyList<LlmToolCall> toolCalls)
    {
        var array = new JsonArray();
        foreach (var call in toolCalls)
        {
            array.Add(new JsonObject
            {
                ["id"] = call.Id,
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = call.Name,
                    ["arguments"] = call.ArgumentsJson,
                },
            });
        }

        return array;
    }

    private static JsonObject? TryReadProviderMessage(string? state)
    {
        if (string.IsNullOrWhiteSpace(state))
            return null;

        try
        {
            return (JsonNode.Parse(state) as JsonObject)?.DeepClone().AsObject();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonNode ParseSchema(string schema)
    {
        if (!string.IsNullOrWhiteSpace(schema))
        {
            try
            {
                return JsonNode.Parse(schema)
                    ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
            }
            catch (JsonException)
            {
                // The canonical argument readers remain authoritative. A malformed
                // advertised schema degrades to an open object rather than losing
                // the entire assistant turn.
            }
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(),
        };
    }

    private static IReadOnlyList<LlmToolCall> ParseToolCalls(JsonElement message)
    {
        if (!message.TryGetProperty("tool_calls", out var toolCalls)
            || toolCalls.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var parsed = new List<LlmToolCall>();
        foreach (var call in toolCalls.EnumerateArray())
        {
            if (call.ValueKind != JsonValueKind.Object
                || !call.TryGetProperty("function", out var function)
                || function.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = function.TryGetProperty("name", out var nameElement)
                && nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString()
                    : null;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var id = call.TryGetProperty("id", out var idElement)
                && idElement.ValueKind == JsonValueKind.String
                    ? idElement.GetString()
                    : null;

            string argumentsJson;
            if (function.TryGetProperty("arguments", out var arguments))
            {
                argumentsJson = arguments.ValueKind == JsonValueKind.String
                    ? arguments.GetString() ?? "{}"
                    : arguments.GetRawText();
            }
            else
            {
                argumentsJson = "{}";
            }

            parsed.Add(new LlmToolCall(
                id ?? Guid.NewGuid().ToString("N"),
                name,
                argumentsJson));
        }

        return parsed;
    }

    private static int? ReadInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var parsed)
            ? parsed
            : null;
}

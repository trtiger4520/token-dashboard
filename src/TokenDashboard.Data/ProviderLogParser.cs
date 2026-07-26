using System.Globalization;
using System.Text.Json;
using TokenDashboard.Core;

namespace TokenDashboard.Data;

internal sealed record ProviderParseAttempt(bool Recognized, ParseResult Result);

internal static class ProviderLogParser
{
    public static ProviderParseAttempt ParseJsonLines(
        string text,
        SourceAdapterKind adapterKind,
        CancellationToken cancellationToken)
    {
        return adapterKind is SourceAdapterKind.ClaudeCodeApp or SourceAdapterKind.ClaudeCodeCli
            ? ParseClaude(text, adapterKind, cancellationToken)
            : ParseCodex(text, adapterKind, cancellationToken);
    }

    private static ProviderParseAttempt ParseClaude(
        string text,
        SourceAdapterKind adapterKind,
        CancellationToken cancellationToken)
    {
        var events = new List<NormalizedEvent>();
        var errors = new List<ParseError>();
        var recognized = false;
        var lineNumber = 0;

        foreach (var line in Lines(text))
        {
            lineNumber++;
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var recordType = GetString(root, "type");
                if (IsClaudeRecordType(recordType) &&
                    !string.IsNullOrWhiteSpace(GetString(root, "sessionId", "session_id")))
                {
                    recognized = true;
                }

                if (recordType is not ("user" or "assistant") || !TryGetObject(root, "message", out var message))
                {
                    continue;
                }

                var sessionId = GetString(root, "sessionId", "session_id");
                var turnId = GetString(root, "uuid");
                var timestamp = GetString(root, "timestamp");
                if (string.IsNullOrWhiteSpace(sessionId) ||
                    string.IsNullOrWhiteSpace(turnId) ||
                    !TryParseUtc(timestamp, out var occurredAtUtc))
                {
                    errors.Add(new ParseError(lineNumber, "Claude message metadata is missing or invalid"));
                    continue;
                }

                var role = GetString(message, "role") ?? recordType;
                var content = TryGetProperty(message, "content", out var messageContent)
                    ? ExtractText(messageContent)
                    : "";
                var toolNames = TryGetProperty(message, "content", out messageContent)
                    ? ReadToolNames(messageContent)
                    : [];
                var hasToolResult = TryGetProperty(message, "content", out messageContent) &&
                                    ContainsContentType(messageContent, "tool_result");
                var eventType = toolNames.Length > 0
                    ? "assistant.tool"
                    : hasToolResult
                        ? "tool.result"
                        : $"{recordType}.message";
                var prompt = string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase) ? "" : content;
                var response = string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase) ? content : "";
                var tokens = ReadClaudeTokens(message);
                var tool = toolNames.Length > 0 ? string.Join(", ", toolNames) : hasToolResult ? "tool-result" : "";
                var model = GetString(message, "model") ?? "";
                var workflow = GetString(root, "entrypoint") ?? "";
                var subagent = IsTrue(root, "isSidechain") ? "sidechain" : "";
                var sourceId = GetString(root, "source_id", "source") ?? adapterKind.ToString();
                var canonicalPayload = CanonicalPayload(
                    "claude",
                    eventType,
                    role,
                    prompt,
                    response,
                    model,
                    tool,
                    subagent,
                    workflow,
                    tokens);

                events.Add(NormalizedEvent.Create(
                    adapterKind,
                    sourceId,
                    sessionId,
                    turnId,
                    Math.Max(0, lineNumber - 1),
                    eventType,
                    role,
                    prompt,
                    response,
                    model,
                    tool,
                    subagent,
                    workflow,
                    occurredAtUtc,
                    "UTC",
                    canonicalPayload,
                    tokens,
                    [],
                    line));
            }
            catch (JsonException exception)
            {
                errors.Add(new ParseError(lineNumber, exception.Message));
            }
        }

        return new ProviderParseAttempt(recognized, Result(events, errors));
    }

    private static ProviderParseAttempt ParseCodex(
        string text,
        SourceAdapterKind adapterKind,
        CancellationToken cancellationToken)
    {
        var lines = ReadJsonLines(text, cancellationToken, out var errors);
        var recognized = lines.Any(static item =>
            GetString(item.Element, "type") is "session_meta" or "turn_context" or "response_item" or "event_msg");
        if (!recognized)
        {
            return new ProviderParseAttempt(false, Result([], errors));
        }

        var sessionId = lines
            .Where(static item => string.Equals(GetString(item.Element, "type"), "session_meta", StringComparison.Ordinal))
            .Select(static item => TryGetObject(item.Element, "payload", out var payload)
                ? GetString(payload, "session_id", "id")
                : null)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            errors.Add(new ParseError(0, "Codex session metadata is missing"));
            return new ProviderParseAttempt(true, Result([], errors));
        }

        var modelsByTurn = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in lines)
        {
            if (!string.Equals(GetString(item.Element, "type"), "turn_context", StringComparison.Ordinal) ||
                !TryGetObject(item.Element, "payload", out var payload))
            {
                continue;
            }

            var turnId = GetString(payload, "turn_id");
            var model = GetString(payload, "model");
            if (!string.IsNullOrWhiteSpace(turnId) && !string.IsNullOrWhiteSpace(model))
            {
                modelsByTurn[turnId] = model;
            }
        }

        var events = new List<NormalizedEvent>();
        var turnSequences = new Dictionary<string, int>(StringComparer.Ordinal);
        var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);
        string? currentTurnId = null;
        var currentModel = "";
        var nextSequence = 0;

        foreach (var item in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = item.Element;
            var outerType = GetString(root, "type");
            if (!TryGetObject(root, "payload", out var payload))
            {
                continue;
            }

            if (outerType is "turn_context" ||
                string.Equals(GetString(payload, "type"), "task_started", StringComparison.Ordinal))
            {
                var contextualTurnId = GetString(payload, "turn_id");
                if (!string.IsNullOrWhiteSpace(contextualTurnId))
                {
                    currentTurnId = contextualTurnId;
                    if (!turnSequences.ContainsKey(currentTurnId))
                    {
                        turnSequences[currentTurnId] = nextSequence++;
                    }
                }

                var contextualModel = GetString(payload, "model");
                if (!string.IsNullOrWhiteSpace(contextualModel))
                {
                    currentModel = contextualModel;
                }
                else if (currentTurnId is not null && modelsByTurn.TryGetValue(currentTurnId, out var mappedModel))
                {
                    currentModel = mappedModel;
                }

                continue;
            }

            if (outerType is not ("response_item" or "event_msg") || currentTurnId is null)
            {
                continue;
            }

            if (!TryParseUtc(GetString(root, "timestamp"), out var occurredAtUtc))
            {
                errors.Add(new ParseError(item.LineNumber, "Codex event timestamp is missing or invalid"));
                continue;
            }

            var payloadType = GetString(payload, "type") ?? "";
            var parsed = outerType == "response_item"
                ? ParseCodexResponse(payload, toolNames)
                : ParseCodexEvent(payload);
            if (parsed is null)
            {
                continue;
            }

            var model = modelsByTurn.TryGetValue(currentTurnId, out var mapped)
                ? mapped
                : currentModel;
            var canonicalPayload = CanonicalPayload(
                "codex",
                parsed.EventType,
                parsed.Role,
                parsed.Prompt,
                parsed.Response,
                model,
                parsed.Tool,
                "",
                "",
                parsed.Tokens);
            events.Add(NormalizedEvent.Create(
                adapterKind,
                adapterKind.ToString(),
                sessionId,
                currentTurnId,
                turnSequences[currentTurnId],
                parsed.EventType,
                parsed.Role,
                parsed.Prompt,
                parsed.Response,
                model,
                parsed.Tool,
                "",
                "",
                occurredAtUtc,
                "UTC",
                canonicalPayload,
                parsed.Tokens,
                [],
                item.RawText));

            if (payloadType is "function_call" or "custom_tool_call")
            {
                var callId = GetString(payload, "call_id", "id");
                if (!string.IsNullOrWhiteSpace(callId) && !string.IsNullOrWhiteSpace(parsed.Tool))
                {
                    toolNames[callId] = parsed.Tool;
                }
            }
        }

        return new ProviderParseAttempt(true, Result(events, errors));
    }

    private static CodexEvent? ParseCodexResponse(
        JsonElement payload,
        Dictionary<string, string> toolNames)
    {
        var payloadType = GetString(payload, "type") ?? "";
        if (string.Equals(payloadType, "message", StringComparison.Ordinal))
        {
            var role = GetString(payload, "role") ?? "unknown";
            var content = TryGetProperty(payload, "content", out var value) ? ExtractText(value) : "";
            var isAssistant = string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase);
            return new CodexEvent(
                $"{role}.message",
                role,
                isAssistant ? "" : content,
                isAssistant ? content : "",
                "",
                EmptyTokens);
        }

        if (payloadType is "function_call" or "custom_tool_call")
        {
            var tool = GetString(payload, "name") ?? "tool";
            var details = FieldText(payload, "arguments", "input");
            return new CodexEvent("tool.call", "assistant", "", details, tool, EmptyTokens);
        }

        if (payloadType is "function_call_output" or "custom_tool_call_output")
        {
            var callId = GetString(payload, "call_id", "id");
            var tool = callId is not null && toolNames.TryGetValue(callId, out var name) ? name : "tool-result";
            return new CodexEvent("tool.result", "tool", "", FieldText(payload, "output"), tool, EmptyTokens);
        }

        if (string.Equals(payloadType, "reasoning", StringComparison.Ordinal))
        {
            var summary = FieldText(payload, "summary");
            return string.IsNullOrWhiteSpace(summary)
                ? null
                : new CodexEvent("assistant.reasoning", "assistant", "", summary, "", EmptyTokens);
        }

        return null;
    }

    private static CodexEvent? ParseCodexEvent(JsonElement payload)
    {
        if (!string.Equals(GetString(payload, "type"), "token_count", StringComparison.Ordinal) ||
            !TryGetObject(payload, "info", out var info) ||
            !TryGetObject(info, "last_token_usage", out var usage))
        {
            return null;
        }

        var tokens = ReadCodexTokens(usage);
        return tokens.Count == 0
            ? null
            : new CodexEvent("token.usage", "assistant", "", "", "", tokens);
    }

    private static Dictionary<TokenType, long> ReadClaudeTokens(JsonElement message)
    {
        var tokens = new Dictionary<TokenType, long>();
        if (!TryGetObject(message, "usage", out var usage))
        {
            return tokens;
        }

        AddToken(tokens, TokenType.Input, ReadLong(usage, "input_tokens"));
        AddToken(tokens, TokenType.Output, ReadLong(usage, "output_tokens"));
        AddToken(tokens, TokenType.Create("cache-read"), ReadLong(usage, "cache_read_input_tokens"));

        if (TryGetObject(usage, "cache_creation", out var cacheCreation))
        {
            AddToken(tokens, TokenType.Create("cache-write-5m"), ReadLong(cacheCreation, "ephemeral_5m_input_tokens"));
            AddToken(tokens, TokenType.Create("cache-write-1h"), ReadLong(cacheCreation, "ephemeral_1h_input_tokens"));
        }
        else
        {
            AddToken(tokens, TokenType.Create("cache-write"), ReadLong(usage, "cache_creation_input_tokens"));
        }

        return tokens;
    }

    private static Dictionary<TokenType, long> ReadCodexTokens(JsonElement usage)
    {
        var tokens = new Dictionary<TokenType, long>();
        var input = ReadLong(usage, "input_tokens") ?? 0;
        var cacheRead = ReadLong(usage, "cached_input_tokens") ?? 0;
        var cacheWrite = ReadLong(usage, "cache_write_input_tokens") ?? 0;
        var output = ReadLong(usage, "output_tokens") ?? 0;
        var reasoning = ReadLong(usage, "reasoning_output_tokens") ?? 0;

        AddToken(tokens, TokenType.Input, Math.Max(0, input - cacheRead - cacheWrite));
        AddToken(tokens, TokenType.CachedInput, cacheRead);
        AddToken(tokens, TokenType.Create("cache-write-input"), cacheWrite);
        AddToken(tokens, TokenType.Output, Math.Max(0, output - reasoning));
        AddToken(tokens, TokenType.Reasoning, reasoning);
        return tokens;
    }

    private static void AddToken(Dictionary<TokenType, long> tokens, TokenType type, long? value)
    {
        if (value is >= 0)
        {
            tokens[type] = value.Value;
        }
    }

    private static long? ReadLong(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
            _ => null
        };
    }

    private static List<JsonLine> ReadJsonLines(
        string text,
        CancellationToken cancellationToken,
        out List<ParseError> errors)
    {
        var rows = new List<JsonLine>();
        errors = [];
        var lineNumber = 0;
        foreach (var line in Lines(text))
        {
            lineNumber++;
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                rows.Add(new JsonLine(lineNumber, document.RootElement.Clone(), line));
            }
            catch (JsonException exception)
            {
                errors.Add(new ParseError(lineNumber, exception.Message));
            }
        }

        return rows;
    }

    private static string[] ReadToolNames(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return content.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.Object &&
                                  string.Equals(GetString(item, "type"), "tool_use", StringComparison.Ordinal))
            .Select(static item => GetString(item, "name"))
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool ContainsContentType(JsonElement content, string type)
    {
        return content.ValueKind == JsonValueKind.Array &&
               content.EnumerateArray().Any(item =>
                   item.ValueKind == JsonValueKind.Object &&
                   string.Equals(GetString(item, "type"), type, StringComparison.Ordinal));
    }

    private static string ExtractText(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Array => Join(value.EnumerateArray().Select(ExtractText)),
            JsonValueKind.Object => ExtractObjectText(value),
            _ => ""
        };
    }

    private static string ExtractObjectText(JsonElement value)
    {
        var type = GetString(value, "type");
        if (type is "tool_use")
        {
            return FieldText(value, "input");
        }

        if (type is "tool_result")
        {
            return TryGetProperty(value, "content", out var content) ? ExtractText(content) : "";
        }

        foreach (var name in new[] { "text", "thinking", "message", "output", "content" })
        {
            if (TryGetProperty(value, name, out var property))
            {
                var text = property.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                    ? ExtractText(property)
                    : FieldText(value, name);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return "";
    }

    private static string FieldText(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var value))
            {
                continue;
            }

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? "",
                JsonValueKind.Null or JsonValueKind.Undefined => "",
                _ => value.GetRawText()
            };
        }

        return "";
    }

    private static string Join(IEnumerable<string> values)
    {
        return string.Join(
            "\n",
            values.Where(static value => !string.IsNullOrWhiteSpace(value)).Select(static value => value.Trim()));
    }

    private static string CanonicalPayload(
        string provider,
        string eventType,
        string role,
        string prompt,
        string response,
        string model,
        string tool,
        string subagent,
        string workflow,
        IReadOnlyDictionary<TokenType, long> tokens)
    {
        return JsonSerializer.Serialize(new
        {
            provider,
            eventType,
            role,
            prompt,
            response,
            model,
            tool,
            subagent,
            workflow,
            tokens = tokens.ToDictionary(static pair => pair.Key.Value, static pair => pair.Value, StringComparer.Ordinal)
        });
    }

    private static ParseResult Result(
        IReadOnlyList<NormalizedEvent> events,
        List<ParseError> errors)
    {
        return new ParseResult(
            events,
            errors,
            errors.Count == 0 ? AdapterCapabilityStatus.Available : AdapterCapabilityStatus.ParseFallback);
    }

    private static string[] Lines(string text)
    {
        return text.Split(["\r\n", "\n"], StringSplitOptions.None);
    }

    private static bool TryParseUtc(string? value, out DateTimeOffset result)
    {
        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out result))
        {
            result = result.ToUniversalTime();
            return true;
        }

        result = default;
        return false;
    }

    private static bool TryGetObject(JsonElement element, string name, out JsonElement value)
    {
        return TryGetProperty(element, name, out value) && value.ValueKind == JsonValueKind.Object;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            return element.TryGetProperty(name, out value);
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static bool IsTrue(JsonElement element, string name)
    {
        return TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.True;
    }

    private static bool IsClaudeRecordType(string? value)
    {
        return value is
            "user" or
            "assistant" or
            "attachment" or
            "file-history-delta" or
            "file-history-snapshot" or
            "last-prompt" or
            "mode" or
            "permission-mode" or
            "queue-operation" or
            "system" or
            "agent-name" or
            "ai-title";
    }

    private static IReadOnlyDictionary<TokenType, long> EmptyTokens { get; } =
        new Dictionary<TokenType, long>();

    private sealed record CodexEvent(
        string EventType,
        string Role,
        string Prompt,
        string Response,
        string Tool,
        IReadOnlyDictionary<TokenType, long> Tokens);

    private readonly record struct JsonLine(int LineNumber, JsonElement Element, string RawText);
}

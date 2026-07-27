using System.Globalization;
using System.Text;
using System.Text.Json;
using TokenDashboard.Core;

namespace TokenDashboard.Data;

public enum SourceAdapterKind
{
    ClaudeCodeApp,
    ClaudeCodeCli,
    CodexApp,
    CodexCli
}

public enum AdapterCapabilityStatus
{
    Available,
    NotFound,
    PermissionDenied,
    UnsupportedVersion,
    ParseFallback
}

public enum HostPlatform
{
    Windows,
    Linux,
    MacOS
}

public sealed record AdapterCapabilities(
    SourceAdapterKind AdapterKind,
    AdapterCapabilityStatus Status,
    IReadOnlyList<string> Formats,
    string Notes);

public sealed record SourcePathCandidate(string Path, bool IsDefault, bool Exists);

public sealed record SourceDiscoveryOptions(
    HostPlatform Platform,
    string UserHome,
    string? AppData = null,
    IReadOnlyList<string>? CustomPaths = null)
{
    public IReadOnlyList<string> Paths { get; } = CustomPaths ?? [];
}

public sealed record ParseError(int LineNumber, string Message);

public sealed record ParseResult(
    IReadOnlyList<NormalizedEvent> Events,
    IReadOnlyList<ParseError> Errors,
    AdapterCapabilityStatus Status);

public sealed record NormalizedEvent
{
    private NormalizedEvent(
        SourceAdapterKind adapterKind,
        string sourceId,
        string? sessionId,
        string? turnId,
        int sequence,
        string eventType,
        string role,
        string prompt,
        string response,
        string model,
        string tool,
        string subagent,
        string workflow,
        DateTimeOffset occurredAtUtc,
        string sourceTimeZone,
        string payload,
        IReadOnlyDictionary<TokenType, long> tokenCounts,
        IReadOnlyList<(string Key, string Value)> tags,
        EventFingerprint eventFingerprint)
    {
        AdapterKind = adapterKind;
        SourceId = sourceId;
        SessionId = sessionId;
        TurnId = turnId;
        Sequence = sequence;
        EventType = eventType;
        Role = role;
        Prompt = prompt;
        Response = response;
        Model = model;
        Tool = tool;
        Subagent = subagent;
        Workflow = workflow;
        OccurredAtUtc = occurredAtUtc;
        SourceTimeZone = sourceTimeZone;
        Payload = payload;
        TokenCounts = tokenCounts;
        Tags = tags;
        EventFingerprint = eventFingerprint;
    }

    public SourceAdapterKind AdapterKind { get; }

    public string SourceId { get; }

    public string? SessionId { get; }

    public string? TurnId { get; }

    public int Sequence { get; }

    public string EventType { get; }

    public string Role { get; }

    public string Prompt { get; }

    public string Response { get; }

    public string Model { get; }

    public string Tool { get; }

    public string Subagent { get; }

    public string Workflow { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public string SourceTimeZone { get; }

    public string Payload { get; }

    public IReadOnlyDictionary<TokenType, long> TokenCounts { get; }

    public bool CacheMetricsReported => TokenCounts.Keys.Any(static token => token.Value.Contains("cache", StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<(string Key, string Value)> Tags { get; }

    public EventFingerprint EventFingerprint { get; }

    internal static NormalizedEvent Create(
        SourceAdapterKind adapterKind,
        string sourceId,
        string? sessionId,
        string? turnId,
        int sequence,
        string eventType,
        string role,
        string prompt,
        string response,
        string model,
        string tool,
        string subagent,
        string workflow,
        DateTimeOffset occurredAtUtc,
        string sourceTimeZone,
        string payload,
        IReadOnlyDictionary<TokenType, long> tokenCounts,
        IReadOnlyList<(string Key, string Value)> tags,
        string? fingerprintPayload = null)
    {
        var fingerprint = EventFingerprint.Create(
            sourceId,
            eventType,
            occurredAtUtc,
            sourceTimeZone,
            fingerprintPayload ?? payload,
            sessionId,
            turnId,
            sequence);
        return new NormalizedEvent(
            adapterKind,
            sourceId,
            sessionId,
            turnId,
            sequence,
            eventType,
            role,
            prompt,
            response,
            model,
            tool,
            subagent,
            workflow,
            occurredAtUtc,
            sourceTimeZone,
            payload,
            tokenCounts,
            tags,
            fingerprint);
    }
}

public interface ILogSourceAdapter
{
    SourceAdapterKind Kind { get; }

    IReadOnlyList<SourcePathCandidate> DiscoverPaths(SourceDiscoveryOptions options);

    ParseResult Parse(string path, CancellationToken cancellationToken = default);

    AdapterCapabilities GetCapabilities();
}

public static class SourcePathCatalog
{
    public static IReadOnlyList<string> GetDefaultPaths(SourceAdapterKind kind, SourceDiscoveryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var home = Required(options.UserHome, nameof(options.UserHome));
        var claudeRoot = Path.Combine(home, ".claude");
        var codexRoot = Path.Combine(home, ".codex");
        var claudePaths = new[]
        {
            Path.Combine(claudeRoot, "projects"),
            Path.Combine(claudeRoot, "sessions")
        };
        var codexPaths = new[]
        {
            Path.Combine(codexRoot, "sessions"),
            Path.Combine(codexRoot, "archived_sessions")
        };

        var appDataClaude = options.Platform switch
        {
            HostPlatform.Windows => options.AppData is null ? null : Path.Combine(options.AppData, "Claude"),
            HostPlatform.MacOS => Path.Combine(home, "Library", "Application Support", "Claude"),
            _ => Path.Combine(home, ".config", "Claude")
        };

        return kind switch
        {
            SourceAdapterKind.ClaudeCodeApp => claudePaths.Concat(appDataClaude is null ? [] : [appDataClaude]).ToArray(),
            SourceAdapterKind.ClaudeCodeCli => claudePaths,
            SourceAdapterKind.CodexApp => codexPaths,
            SourceAdapterKind.CodexCli => codexPaths,
            _ => []
        };
    }

    private static string Required(string value, string parameterName)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value is required", parameterName)
            : value.Trim();
    }
}

public abstract class LogSourceAdapterBase : ILogSourceAdapter
{
    public abstract SourceAdapterKind Kind { get; }

    public IReadOnlyList<SourcePathCandidate> DiscoverPaths(SourceDiscoveryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var defaults = SourcePathCatalog.GetDefaultPaths(Kind, options);
        var paths = options.Paths
            .Concat(defaults)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new SourcePathCandidate(path, defaults.Contains(path, StringComparer.OrdinalIgnoreCase), Directory.Exists(path) || File.Exists(path)))
            .ToArray();
        return paths;
    }

    public ParseResult Parse(string path, CancellationToken cancellationToken = default)
    {
        return SourceFileParser.Parse(path, Kind, cancellationToken);
    }

    public AdapterCapabilities GetCapabilities()
    {
        return new AdapterCapabilities(
            Kind,
            AdapterCapabilityStatus.Available,
            ["json", "jsonl", "csv"],
            "Tolerant capability and fallback parser; real provider format compatibility requires redacted samples");
    }
}

public sealed class ClaudeCodeAppAdapter : LogSourceAdapterBase
{
    public override SourceAdapterKind Kind => SourceAdapterKind.ClaudeCodeApp;
}

public sealed class ClaudeCodeCliAdapter : LogSourceAdapterBase
{
    public override SourceAdapterKind Kind => SourceAdapterKind.ClaudeCodeCli;
}

public sealed class CodexAppAdapter : LogSourceAdapterBase
{
    public override SourceAdapterKind Kind => SourceAdapterKind.CodexApp;
}

public sealed class CodexCliAdapter : LogSourceAdapterBase
{
    public override SourceAdapterKind Kind => SourceAdapterKind.CodexCli;
}

internal static class SourceFileParser
{
    public static ParseResult Parse(string path, SourceAdapterKind adapterKind, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path is required", nameof(path));
        }

        try
        {
            var text = File.ReadAllText(path);
            var extension = Path.GetExtension(path).ToLowerInvariant();
            return extension switch
            {
                ".json" => ParseJson(text, adapterKind, cancellationToken),
                ".jsonl" or ".ndjson" => ParseJsonLines(text, adapterKind, cancellationToken),
                ".csv" => ParseCsv(text, adapterKind, cancellationToken),
                _ => ParseFallback(text, adapterKind, cancellationToken)
            };
        }
        catch (FileNotFoundException)
        {
            return new ParseResult([], [new ParseError(0, "Source file was not found")], AdapterCapabilityStatus.NotFound);
        }
        catch (DirectoryNotFoundException)
        {
            return new ParseResult([], [new ParseError(0, "Source directory was not found")], AdapterCapabilityStatus.NotFound);
        }
        catch (UnauthorizedAccessException)
        {
            return new ParseResult([], [new ParseError(0, "Source path permission was denied")], AdapterCapabilityStatus.PermissionDenied);
        }
    }

    private static ParseResult ParseJson(string text, SourceAdapterKind adapterKind, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            if (adapterKind is SourceAdapterKind.ClaudeCodeApp or SourceAdapterKind.ClaudeCodeCli &&
                IsClaudeMetadataDocument(document.RootElement))
            {
                return new ParseResult([], [], AdapterCapabilityStatus.Available);
            }

            var elements = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToArray()
                : [document.RootElement];
            return NormalizeElements(elements, adapterKind, false, cancellationToken);
        }
        catch (JsonException exception)
        {
            return new ParseResult([], [new ParseError(exception.LineNumber is null ? 0 : checked((int)exception.LineNumber.Value), exception.Message)], AdapterCapabilityStatus.ParseFallback);
        }
    }

    private static bool IsClaudeMetadataDocument(JsonElement root)
    {
        var elements = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray().ToArray()
            : [root];
        return elements.Length > 0 && elements.All(static element =>
            element.ValueKind == JsonValueKind.Object &&
            ((element.TryGetProperty("id", out _) &&
              element.TryGetProperty("subject", out _) &&
              element.TryGetProperty("status", out _)) ||
             (element.TryGetProperty("agentType", out _) &&
              element.TryGetProperty("toolUseId", out _))));
    }

    private static ParseResult ParseJsonLines(string text, SourceAdapterKind adapterKind, CancellationToken cancellationToken)
    {
        var providerResult = ProviderLogParser.ParseJsonLines(text, adapterKind, cancellationToken);
        if (providerResult.Recognized)
        {
            return providerResult.Result;
        }

        var events = new List<NormalizedEvent>();
        var errors = new List<ParseError>();
        var lineNumber = 0;
        foreach (var line in text.Split(["\r\n", "\n"], StringSplitOptions.None))
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
                var result = NormalizeElement(document.RootElement, adapterKind, lineNumber);
                if (result.Event is not null)
                {
                    events.Add(result.Event);
                }

                if (result.Error is not null)
                {
                    errors.Add(result.Error);
                }
            }
            catch (JsonException exception)
            {
                errors.Add(new ParseError(lineNumber, exception.Message));
            }
        }

        return new ParseResult(events, errors, errors.Count == 0 ? AdapterCapabilityStatus.Available : AdapterCapabilityStatus.ParseFallback);
    }

    private static ParseResult ParseCsv(string text, SourceAdapterKind adapterKind, CancellationToken cancellationToken)
    {
        var rows = CsvRows(text).ToArray();
        if (rows.Length == 0)
        {
            return new ParseResult([], [new ParseError(1, "CSV header is missing")], AdapterCapabilityStatus.ParseFallback);
        }

        var headers = rows[0];
        var elements = new List<NormalizedEvent>();
        var errors = new List<ParseError>();
        for (var index = 1; index < rows.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = rows[index];
            if (row.Count == 0 || row.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            var values = headers.Select((header, headerIndex) => new { header, Value = headerIndex < row.Count ? row[headerIndex] : "" })
                .ToDictionary(item => item.header, item => item.Value, StringComparer.OrdinalIgnoreCase);
            var result = NormalizeMap(values, adapterKind, index + 1, JsonSerializer.Serialize(values));
            if (result.Event is not null)
            {
                elements.Add(result.Event);
            }

            if (result.Error is not null)
            {
                errors.Add(result.Error);
            }
        }

        return new ParseResult(elements, errors, errors.Count == 0 ? AdapterCapabilityStatus.Available : AdapterCapabilityStatus.ParseFallback);
    }

    private static ParseResult ParseFallback(string text, SourceAdapterKind adapterKind, CancellationToken cancellationToken)
    {
        var json = ParseJson(text, adapterKind, cancellationToken);
        if (json.Events.Count > 0)
        {
            return json with { Status = AdapterCapabilityStatus.ParseFallback };
        }

        return ParseJsonLines(text, adapterKind, cancellationToken) with { Status = AdapterCapabilityStatus.ParseFallback };
    }

    private static ParseResult NormalizeElements(IEnumerable<JsonElement> elements, SourceAdapterKind adapterKind, bool fallback, CancellationToken cancellationToken)
    {
        var events = new List<NormalizedEvent>();
        var errors = new List<ParseError>();
        var index = 0;
        foreach (var element in elements)
        {
            index++;
            cancellationToken.ThrowIfCancellationRequested();
            var result = NormalizeElement(element, adapterKind, index);
            if (result.Event is not null)
            {
                events.Add(result.Event);
            }

            if (result.Error is not null)
            {
                errors.Add(result.Error);
            }
        }

        return new ParseResult(events, errors, fallback || errors.Count > 0 ? AdapterCapabilityStatus.ParseFallback : AdapterCapabilityStatus.Available);
    }

    private static (NormalizedEvent? Event, ParseError? Error) NormalizeElement(JsonElement element, SourceAdapterKind adapterKind, int lineNumber)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return (null, new ParseError(lineNumber, "Event must be a JSON object"));
        }

        var values = element.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.OrdinalIgnoreCase);
        var map = values.ToDictionary(pair => pair.Key, pair => pair.Value.ValueKind == JsonValueKind.String ? pair.Value.GetString() ?? "" : pair.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        var result = NormalizeMap(map, adapterKind, lineNumber, element.GetRawText(), values);
        return result;
    }

    private static (NormalizedEvent? Event, ParseError? Error) NormalizeMap(
        IReadOnlyDictionary<string, string> map,
        SourceAdapterKind adapterKind,
        int lineNumber,
        string payload,
        IReadOnlyDictionary<string, JsonElement>? jsonValues = null)
    {
        var sourceId = Get(map, "source_id") ?? Get(map, "source") ?? adapterKind.ToString();
        var sourceTimeZone = Get(map, "source_timezone") ?? Get(map, "timezone") ?? "UTC";
        var eventType = Get(map, "event_type") ?? Get(map, "type") ?? "event";
        var occurredText = Get(map, "occurred_at_utc") ?? Get(map, "occurred_at") ?? Get(map, "timestamp");
        if (!TryParseUtc(occurredText, out var occurredAtUtc))
        {
            return (null, new ParseError(lineNumber, "Event timestamp is missing or invalid"));
        }

        var sequence = ParseInt(Get(map, "sequence"), Math.Max(0, lineNumber - 1));
        var sessionId = Get(map, "session_id");
        var turnId = Get(map, "turn_id");
        var role = Get(map, "role") ?? "unknown";
        var body = Get(map, "content") ?? "";
        var prompt = Get(map, "prompt") ?? (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase) ? body : "");
        var response = Get(map, "response") ?? (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase) ? body : "");
        var tokens = ReadTokens(map, jsonValues);
        var tags = ReadTags(map, jsonValues);
        var canonicalPayload = JsonSerializer.Serialize(new
        {
            adapter = adapterKind.ToString(),
            sourceId,
            sessionId,
            turnId,
            sequence,
            eventType,
            role,
            prompt,
            response,
            model = Get(map, "model") ?? "",
            tool = Get(map, "tool") ?? "",
            subagent = Get(map, "subagent") ?? "",
            workflow = Get(map, "workflow") ?? "",
            mode = Get(map, "mode"),
            occurredAtUtc = occurredAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            sourceTimeZone,
            tokens = tokens.OrderBy(pair => pair.Key.Value, StringComparer.Ordinal).ToDictionary(pair => pair.Key.Value, pair => pair.Value, StringComparer.Ordinal),
            tags = tags.OrderBy(pair => pair.Key, StringComparer.Ordinal).ThenBy(pair => pair.Value, StringComparer.Ordinal).ToArray()
        });
        var normalized = NormalizedEvent.Create(
            adapterKind,
            sourceId,
            sessionId,
            turnId,
            sequence,
            eventType,
            role,
            prompt,
            response,
            Get(map, "model") ?? "",
            Get(map, "tool") ?? "",
            Get(map, "subagent") ?? "",
            Get(map, "workflow") ?? "",
            occurredAtUtc,
            sourceTimeZone,
            canonicalPayload,
            tokens,
            tags,
            payload);
        return (normalized, null);
    }

    private static Dictionary<TokenType, long> ReadTokens(IReadOnlyDictionary<string, string> map, IReadOnlyDictionary<string, JsonElement>? jsonValues)
    {
        var tokens = new Dictionary<TokenType, long>();
        foreach (var pair in map)
        {
            if (!pair.Key.EndsWith("_tokens", StringComparison.OrdinalIgnoreCase) || !long.TryParse(pair.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count < 0)
            {
                continue;
            }

            tokens[TokenType.Create(pair.Key[..^7].Replace('_', '-'))] = count;
        }

        if (jsonValues is not null && jsonValues.TryGetValue("token_usage", out var tokenUsage) && tokenUsage.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in tokenUsage.EnumerateObject())
            {
                if (property.Value.TryGetInt64(out var count) && count >= 0)
                {
                    tokens[TokenType.Create(property.Name.Replace('_', '-'))] = count;
                }
            }
        }

        return tokens;
    }

    private static IReadOnlyList<(string Key, string Value)> ReadTags(IReadOnlyDictionary<string, string> map, IReadOnlyDictionary<string, JsonElement>? jsonValues)
    {
        if (jsonValues is not null && jsonValues.TryGetValue("tags", out var jsonTags) && jsonTags.ValueKind == JsonValueKind.Array)
        {
            return jsonTags.EnumerateArray()
                .Where(tag => tag.ValueKind == JsonValueKind.String)
                .Select(tag => ("tag", tag.GetString() ?? ""))
                .Where(tag => !string.IsNullOrWhiteSpace(tag.Item2))
                .ToArray();
        }

        return (Get(map, "tags") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => ("tag", value))
            .ToArray();
    }

    private static string? Get(IReadOnlyDictionary<string, string> map, string key) => map.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static int ParseInt(string? value, int fallback) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) && result >= 0 ? result : fallback;

    private static bool TryParseUtc(string? value, out DateTimeOffset result)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out result))
        {
            result = result.ToUniversalTime();
            return true;
        }

        result = default;
        return false;
    }

    private static IEnumerable<IReadOnlyList<string>> CsvRows(string text)
    {
        var rows = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '"')
            {
                if (quoted && index + 1 < text.Length && text[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (character == ',' && !quoted)
            {
                rows.Add(field.ToString());
                field.Clear();
            }
            else if ((character == '\r' || character == '\n') && !quoted)
            {
                if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }

                rows.Add(field.ToString());
                field.Clear();
                yield return rows.ToArray();
                rows.Clear();
            }
            else
            {
                field.Append(character);
            }
        }

        if (field.Length > 0 || rows.Count > 0)
        {
            rows.Add(field.ToString());
            yield return rows.ToArray();
        }
    }
}

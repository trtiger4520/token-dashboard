using System.Globalization;
using System.Text.Json;
using TokenDashboard.Data;

namespace TokenDashboard.Api;

public sealed class DashboardReadService
{
    private readonly DashboardDataService data;
    private readonly PricingResolver pricing;

    public DashboardReadService(DashboardDataService data, PricingResolver pricing)
    {
        this.data = data;
        this.pricing = pricing;
    }

    public IReadOnlyList<EventRow> Events(DateRange range, DashboardFilter? filter = null)
    {
        var rows = data.Query(
            """
            SELECT se.event_fingerprint, se.source_id, s.adapter_kind, se.session_id, se.turn_id,
                   se.occurred_at_utc, se.source_timezone, se.event_type, se.prompt, se.response,
                   se.model, se.tool, se.subagent, se.workflow, se.payload, se.cache_metrics_reported
            FROM sub_events AS se
            INNER JOIN sources AS s ON s.source_id = se.source_id
            WHERE se.occurred_at_utc >= $fromUtc AND se.occurred_at_utc < $toUtc
            ORDER BY se.occurred_at_utc, se.event_fingerprint;
            """,
            ("$fromUtc", Utc(range.FromUtc)),
            ("$toUtc", Utc(range.ToUtc)));
        var tokens = data.Query(
            """
            SELECT turn_id, token_type, SUM(token_count) AS token_count
            FROM token_usages
            GROUP BY turn_id, token_type;
            """);
        var byTurn = tokens
            .GroupBy(row => String(row, "turn_id"), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyDictionary<string, long>)AggregateTokens(group.Select(item => new KeyValuePair<string, long>(String(item, "token_type"), Long(item, "token_count")))),
                StringComparer.Ordinal);

        var events = rows.Select(row => new EventRow(
            String(row, "event_fingerprint"),
            String(row, "source_id"),
            String(row, "adapter_kind"),
            NullableString(row, "session_id"),
            NullableString(row, "turn_id"),
            DateTimeOffset.Parse(String(row, "occurred_at_utc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            String(row, "source_timezone"),
            String(row, "event_type"),
            String(row, "prompt"),
            String(row, "response"),
            String(row, "model"),
            String(row, "tool"),
            String(row, "subagent"),
            String(row, "workflow"),
            String(row, "payload"),
            NullableString(row, "turn_id") is { } turnId && byTurn.TryGetValue(turnId, out var counts)
                ? counts
                : new Dictionary<string, long>(StringComparer.Ordinal),
            ModeFromPayload(String(row, "payload")),
            row["cache_metrics_reported"] is null ? null : Convert.ToInt32(row["cache_metrics_reported"], CultureInfo.InvariantCulture) != 0)).ToArray();

        return ApplyFilter(events, filter).ToArray();
    }

    public object Overview(DateRange range, DashboardFilter? filter = null)
    {
        var events = StatisticalEvents(range, filter);
        var tokens = AggregateTokens(events.SelectMany(item => item.Tokens));
        var coverage = CostCoverage(events);
        return new
        {
            range.FromUtc,
            range.ToUtc,
            range.TimeZoneId,
            eventCount = events.Length,
            sessionCount = events.Select(item => item.SessionId).Where(static value => value is not null).Distinct(StringComparer.Ordinal).Count(),
            totalTokens = tokens.Values.Sum(),
            tokens,
            tokenTypes = tokens,
            inputTokens = events.Sum(item => item.InputTokens),
            cachedInputTokens = events.Sum(item => item.CachedInputTokens),
            outputTokens = events.Sum(item => item.OutputTokens),
            cacheHitRate = CacheRate(events),
            cacheReportedEventCount = events.Count(static item => item.CacheReported),
            cacheUnreportedEventCount = events.Count(static item => !item.CacheReported),
            cacheCoverage = events.Length == 0 ? (decimal?)null : (decimal)events.Count(static item => item.CacheReported) / events.Length,
            costUsd = Cost(events),
            partialCostUsd = PartialCost(events),
            pricedTokenCount = coverage.PricedTokens,
            unpricedTokenCount = coverage.UnpricedTokens,
            costCoverage = coverage.TotalTokens == 0 ? (decimal?)null : (decimal)coverage.PricedTokens / coverage.TotalTokens,
            unpriced = events.Any(item => Cost(item) is null && item.Tokens.Values.Any(static value => value > 0)),
            unpricedCount = events.Count(item => Cost(item) is null && item.Tokens.Values.Any(static value => value > 0))
        };
    }

    public IReadOnlyList<object> Daily(DateRange range, DashboardFilter? filter = null) => GroupByDate(StatisticalEvents(range, filter), range.TimeZoneId, static date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    public IReadOnlyList<object> Monthly(DateRange range, DashboardFilter? filter = null) => GroupByDate(StatisticalEvents(range, filter), range.TimeZoneId, static date => date.ToString("yyyy-MM", CultureInfo.InvariantCulture));

    public IReadOnlyList<object> Heatmap(DateRange range, DashboardFilter? filter = null) => GroupByDate(StatisticalEvents(range, filter), range.TimeZoneId, static date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    public IReadOnlyList<object> Comparisons(DateRange range, string? groupBy, DashboardFilter? filter = null)
    {
        return StatisticalEvents(range, filter, groupBy)
            .GroupBy(item => groupBy?.ToLowerInvariant() switch
            {
                "source" => item.SourceId,
                "tool" => string.IsNullOrWhiteSpace(item.Tool) ? "(none)" : item.Tool,
                _ => item.Model
            }, StringComparer.OrdinalIgnoreCase)
            .Select(group => (object)Summary(group.Key, group))
            .ToArray();
    }

    public IReadOnlyList<object> Sessions(DateRange range, DashboardFilter? filter = null)
    {
        var events = StatisticalEvents(range, filter);
        var bySession = events.Where(item => item.SessionId is not null)
            .GroupBy(item => item.SessionId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        return data.Query(
            """
            SELECT session_id, source_id, started_at_utc, last_activity_at_utc, source_timezone, workspace_id, owner_id
            FROM sessions
            WHERE last_activity_at_utc >= $fromUtc AND started_at_utc < $toUtc
            ORDER BY last_activity_at_utc DESC;
            """,
            ("$fromUtc", Utc(range.FromUtc)),
            ("$toUtc", Utc(range.ToUtc)))
            .Where(row => filter is null || string.IsNullOrWhiteSpace(filter.SourceId) || string.Equals(String(row, "source_id"), filter.SourceId, StringComparison.OrdinalIgnoreCase))
            .Where(row => filter is null || !HasNonSourceFilter(filter) || bySession.ContainsKey(String(row, "session_id")))
            .Select(row =>
            {
                var id = String(row, "session_id");
                bySession.TryGetValue(id, out var sessionEvents);
                sessionEvents ??= [];
                var tokens = AggregateTokens(sessionEvents.SelectMany(item => item.Tokens));
                var coverage = CostCoverage(sessionEvents);
                return (object)new
                {
                    id,
                    sourceId = String(row, "source_id"),
                    startedAtUtc = String(row, "started_at_utc"),
                    lastActivityAtUtc = String(row, "last_activity_at_utc"),
                    endedAtUtc = DateTimeOffset.Parse(String(row, "last_activity_at_utc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).AddMinutes(30),
                    sourceTimeZone = String(row, "source_timezone"),
                    workspaceId = NullableString(row, "workspace_id"),
                    ownerId = NullableString(row, "owner_id"),
                    eventCount = sessionEvents.Length,
                    totalTokens = tokens.Values.Sum(),
                    tokens,
                    tokenTypes = tokens,
                    costUsd = Cost(sessionEvents),
                    partialCostUsd = PartialCost(sessionEvents),
                    pricedTokenCount = coverage.PricedTokens,
                    unpricedTokenCount = coverage.UnpricedTokens,
                    costCoverage = coverage.TotalTokens == 0 ? (decimal?)null : (decimal)coverage.PricedTokens / coverage.TotalTokens
                };
            }).ToArray();
    }

    public object? Session(string id, bool revealContent = false)
    {
        var session = data.Query("SELECT * FROM sessions WHERE session_id = $id;", ("$id", id)).SingleOrDefault();
        if (session is null)
        {
            return null;
        }

        var started = DateTimeOffset.Parse(String(session, "started_at_utc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var last = DateTimeOffset.Parse(String(session, "last_activity_at_utc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var allEvents = Events(new DateRange(started.AddTicks(-1), last.AddTicks(1), String(session, "source_timezone")))
            .Where(item => string.Equals(item.SessionId, id, StringComparison.Ordinal))
            .ToArray();
        var accountingEvents = UniqueTurnEvents(allEvents);
        var turns = data.Query("SELECT * FROM turns WHERE session_id = $id ORDER BY sequence;", ("$id", id)).Select(turn =>
        {
            var turnId = String(turn, "turn_id");
            var turnEvents = allEvents.Where(item => string.Equals(item.TurnId, turnId, StringComparison.Ordinal)).ToArray();
            var turnAccountingEvents = UniqueTurnEvents(turnEvents);
            var tokens = AggregateTokens(turnAccountingEvents.SelectMany(item => item.Tokens));
            var coverage = CostCoverage(turnAccountingEvents);
            var contents = data.Query("SELECT role, body, occurred_at_utc, source_timezone FROM contents WHERE turn_id = $id ORDER BY occurred_at_utc;", ("$id", turnId))
                .Select(content => revealContent
                    ? content
                    : new Dictionary<string, object?>(content, StringComparer.OrdinalIgnoreCase)
                    {
                        ["body"] = MaskContent(Convert.ToString(content["body"], CultureInfo.InvariantCulture) ?? string.Empty),
                        ["contentMasked"] = true
                    })
                .ToArray();
            var subEvents = data.Query("SELECT * FROM sub_events WHERE turn_id = $id ORDER BY occurred_at_utc;", ("$id", turnId))
                .Select(subEvent => revealContent
                    ? subEvent
                    : new Dictionary<string, object?>(subEvent, StringComparer.OrdinalIgnoreCase)
                    {
                        ["prompt"] = MaskContent(Convert.ToString(subEvent["prompt"], CultureInfo.InvariantCulture) ?? string.Empty),
                        ["response"] = MaskContent(Convert.ToString(subEvent["response"], CultureInfo.InvariantCulture) ?? string.Empty),
                        ["payload"] = MaskContent(Convert.ToString(subEvent["payload"], CultureInfo.InvariantCulture) ?? string.Empty),
                        ["contentMasked"] = true
                    })
                .ToArray();
            var tokenUsage = tokens.Select(pair => new { tokenType = pair.Key, tokenCount = pair.Value }).ToArray();
            return (object)new
            {
                id = turnId,
                sequence = Convert.ToInt32(turn["sequence"], CultureInfo.InvariantCulture),
                occurredAtUtc = String(turn, "occurred_at_utc"),
                contents,
                subEvents,
                tokenUsage,
                tokens,
                tokenTypes = tokens,
                costUsd = Cost(turnAccountingEvents),
                partialCostUsd = PartialCost(turnAccountingEvents),
                pricedTokenCount = coverage.PricedTokens,
                unpricedTokenCount = coverage.UnpricedTokens,
                costCoverage = coverage.TotalTokens == 0 ? (decimal?)null : (decimal)coverage.PricedTokens / coverage.TotalTokens
            };
        }).ToArray();
        var sessionTokens = AggregateTokens(accountingEvents.SelectMany(item => item.Tokens));
        var sessionCoverage = CostCoverage(accountingEvents);
        return new
        {
            session,
            tags = Tags("session", id),
            turns,
            totalTokens = sessionTokens.Values.Sum(),
            tokens = sessionTokens,
            tokenTypes = sessionTokens,
            costUsd = Cost(accountingEvents),
            partialCostUsd = PartialCost(accountingEvents),
            pricedTokenCount = sessionCoverage.PricedTokens,
            unpricedTokenCount = sessionCoverage.UnpricedTokens,
            costCoverage = sessionCoverage.TotalTokens == 0 ? (decimal?)null : (decimal)sessionCoverage.PricedTokens / sessionCoverage.TotalTokens
        };
    }

    private static string MaskContent(string body)
    {
        var lines = body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var visible = lines.Take(5).Select(static line => line.Length <= 12 ? new string('•', line.Length) : line[..4] + "…" + line[^4..]);
        return string.Join('\n', visible);
    }

    public IReadOnlyList<SearchResult> Search(string query, int page, int pageSize, string? sourceId)
    {
        return data.SearchFts(query, Math.Min(page * pageSize, 500))
            .Where(result => sourceId is null || string.Equals(result.SourceId, sourceId, StringComparison.Ordinal))
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToArray();
    }

    public IReadOnlyList<object> GroupByDate(IEnumerable<EventRow> events, string timeZoneId, Func<DateTime, string> keySelector)
    {
        var zone = DateRangeResolver.FindTimeZone(timeZoneId);
        return events.GroupBy(item => keySelector(TimeZoneInfo.ConvertTime(item.OccurredAtUtc, zone).Date))
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => (object)DateSummary(group.Key, group))
            .ToArray();
    }

    private EventRow[] StatisticalEvents(DateRange range, DashboardFilter? filter, string? groupBy = null)
    {
        return UniqueTurnEvents(Events(range, filter), groupBy);
    }

    private static EventRow[] UniqueTurnEvents(IEnumerable<EventRow> events, string? groupBy = null)
    {
        return events
            .GroupBy(item => item.TurnId ?? item.Fingerprint, StringComparer.Ordinal)
            .Select(group => string.Equals(groupBy, "tool", StringComparison.OrdinalIgnoreCase)
                ? group.OrderByDescending(item => !string.IsNullOrWhiteSpace(item.Tool)).ThenBy(item => item.OccurredAtUtc).First()
                : group.First())
            .ToArray();
    }

    private Dictionary<string, object?> Summary(string key, IEnumerable<EventRow> events)
    {
        return BuildSummary("key", key, events);
    }

    private Dictionary<string, object?> DateSummary(string date, IEnumerable<EventRow> events)
    {
        return BuildSummary("date", date, events);
    }

    private Dictionary<string, object?> BuildSummary(string label, string key, IEnumerable<EventRow> events)
    {
        var array = events.ToArray();
        var tokens = AggregateTokens(array.SelectMany(item => item.Tokens));
        var coverage = CostCoverage(array);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [label] = key,
            ["eventCount"] = array.Length,
            ["totalTokens"] = tokens.Values.Sum(),
            ["tokens"] = tokens,
            ["tokenTypes"] = tokens,
            ["inputTokens"] = array.Sum(item => item.InputTokens),
            ["cachedInputTokens"] = array.Sum(item => item.CachedInputTokens),
            ["outputTokens"] = array.Sum(item => item.OutputTokens),
            ["cacheHitRate"] = CacheRate(array),
            ["cacheReportedEventCount"] = array.Count(static item => item.CacheReported),
            ["cacheUnreportedEventCount"] = array.Count(static item => !item.CacheReported),
            ["cacheCoverage"] = array.Length == 0 ? (decimal?)null : (decimal)array.Count(static item => item.CacheReported) / array.Length,
            ["costUsd"] = Cost(array),
            ["partialCostUsd"] = PartialCost(array),
            ["pricedTokenCount"] = coverage.PricedTokens,
            ["unpricedTokenCount"] = coverage.UnpricedTokens,
            ["costCoverage"] = coverage.TotalTokens == 0 ? (decimal?)null : (decimal)coverage.PricedTokens / coverage.TotalTokens
        };
    }

    private CostCoverageResult CostCoverage(IEnumerable<EventRow> events)
    {
        var priced = 0L;
        var unpriced = 0L;
        foreach (var item in events)
        {
            var provider = ProviderFor(item);
            var totalInput = item.InputTokens + item.CacheReadTokens;
            foreach (var pair in item.Tokens)
            {
                if (pair.Value <= 0)
                {
                    continue;
                }

                var price = pricing.Resolve(provider, item.Model, pair.Key, item.OccurredAtUtc, totalInput, item.Mode);
                if (price is null)
                {
                    unpriced = checked(unpriced + pair.Value);
                }
                else
                {
                    priced = checked(priced + pair.Value);
                }
            }
        }

        return new CostCoverageResult(priced, unpriced);
    }

    private decimal? Cost(IEnumerable<EventRow> events)
    {
        var total = 0m;
        foreach (var item in events)
        {
            var cost = Cost(item);
            if (cost is null && item.Tokens.Values.Any(static value => value > 0))
            {
                return null;
            }

            total += cost ?? 0m;
        }

        return total;
    }

    private decimal PartialCost(IEnumerable<EventRow> events)
    {
        var total = 0m;
        foreach (var item in events)
        {
            var provider = ProviderFor(item);
            var totalInput = item.InputTokens + item.CacheReadTokens;
            foreach (var pair in item.Tokens)
            {
                if (pair.Value <= 0)
                {
                    continue;
                }

                var price = pricing.Resolve(provider, item.Model, pair.Key, item.OccurredAtUtc, totalInput, item.Mode);
                if (price is not null)
                {
                    total += pair.Value * price.UsdPerMillionTokens / 1_000_000m;
                }
            }
        }

        return total;
    }

    private decimal? Cost(EventRow item)
    {
        var provider = ProviderFor(item);
        var totalInput = item.InputTokens + item.CacheReadTokens;
        var total = 0m;
        foreach (var pair in item.Tokens)
        {
            var price = pricing.Resolve(provider, item.Model, pair.Key, item.OccurredAtUtc, totalInput, item.Mode);
            if (price is null && pair.Value > 0)
            {
                return null;
            }

            total += pair.Value * (price?.UsdPerMillionTokens ?? 0m) / 1_000_000m;
        }

        return total;
    }

    private static string ProviderFor(EventRow item) => item.AdapterKind.Contains("Claude", StringComparison.OrdinalIgnoreCase) ? "anthropic" : item.AdapterKind.Contains("Codex", StringComparison.OrdinalIgnoreCase) ? "openai" : "unknown";

    private readonly record struct CostCoverageResult(long PricedTokens, long UnpricedTokens)
    {
        public long TotalTokens => checked(PricedTokens + UnpricedTokens);
    }

    private static decimal? CacheRate(IEnumerable<EventRow> events)
    {
        var hits = events.Sum(item => item.CacheReadTokens);
        var misses = events.Sum(item => item.InputTokens);
        return hits + misses == 0 ? null : (decimal)hits / (hits + misses);
    }

    private object[] Tags(string scope, string entityId)
    {
        var table = scope switch
        {
            "source" => "source_tags",
            "session" => "session_tags",
            "project" => "project_tags",
            _ => throw new ArgumentOutOfRangeException(nameof(scope))
        };
        var column = scope switch
        {
            "source" => "source_id",
            "session" => "session_id",
            _ => "project_id"
        };
        return data.Query($"""
            SELECT '{scope}' AS scope, assignment.{column} AS entity_id, t.tag_id AS id, t.tag_key AS key, t.tag_value AS value
            FROM {table} AS assignment
            INNER JOIN tags AS t ON t.tag_id = assignment.tag_id
            WHERE assignment.{column} = $entityId
            ORDER BY t.tag_key, t.tag_value;
            """, ("$entityId", entityId)).Select(row => (object)new
        {
            scope = String(row, "scope"),
            entityId = String(row, "entity_id"),
            id = String(row, "id"),
            key = String(row, "key"),
            value = String(row, "value")
        }).ToArray();
    }

    private static IEnumerable<EventRow> ApplyFilter(IEnumerable<EventRow> events, DashboardFilter? filter)
    {
        if (filter is null)
        {
            return events;
        }

        var tokenType = string.IsNullOrWhiteSpace(filter.TokenType) ? null : CanonicalTokenType(filter.TokenType);
        return events
            .Where(item => string.IsNullOrWhiteSpace(filter.SourceId) || string.Equals(item.SourceId, filter.SourceId, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(filter.Tool) || string.Equals(item.Tool, filter.Tool, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(filter.Model) || string.Equals(item.Model, filter.Model, StringComparison.OrdinalIgnoreCase))
            .Select(item => tokenType is null
                ? item
                : item with { Tokens = item.Tokens.Where(pair => string.Equals(CanonicalTokenType(pair.Key), tokenType, StringComparison.Ordinal)).ToDictionary(pair => CanonicalTokenType(pair.Key), pair => pair.Value, StringComparer.Ordinal) })
            .Where(item => tokenType is null || item.Tokens.Count > 0);
    }

    private static bool HasNonSourceFilter(DashboardFilter filter) => !string.IsNullOrWhiteSpace(filter.Tool) || !string.IsNullOrWhiteSpace(filter.Model) || !string.IsNullOrWhiteSpace(filter.TokenType);

    private static Dictionary<string, long> AggregateTokens(IEnumerable<KeyValuePair<string, long>> tokens)
    {
        return tokens.GroupBy(pair => CanonicalTokenType(pair.Key), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(pair => pair.Value), StringComparer.Ordinal);
    }

    private static string CanonicalTokenType(string value)
    {
        var normalized = TokenTypeNormalizer.Normalize(value);
        return TokenTypeNormalizer.IsCacheRead(normalized) ? "cached-input" : TokenTypeNormalizer.IsCacheableInput(normalized) ? "input" : normalized;
    }

    private static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string String(Dictionary<string, object?> row, string name) => Convert.ToString(row[name], CultureInfo.InvariantCulture) ?? string.Empty;

    private static string? NullableString(Dictionary<string, object?> row, string name) => row[name] is null ? null : Convert.ToString(row[name], CultureInfo.InvariantCulture);

    private static long Long(Dictionary<string, object?> row, string name) => Convert.ToInt64(row[name], CultureInfo.InvariantCulture);

    private static string? ModeFromPayload(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.TryGetProperty("mode", out var mode) && mode.ValueKind == JsonValueKind.String ? mode.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

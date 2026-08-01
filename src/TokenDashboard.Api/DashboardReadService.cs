using System.Globalization;
using System.Text;
using System.Text.Json;
using TokenDashboard.Core;
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
                   se.model, se.tool, se.subagent, se.workflow, se.payload, se.cache_metrics_reported,
                   sess.workspace_id
            FROM sub_events AS se
            INNER JOIN sources AS s ON s.source_id = se.source_id
            LEFT JOIN sessions AS sess ON sess.session_id = se.session_id
            WHERE se.occurred_at_utc >= $fromUtc AND se.occurred_at_utc < $toUtc
            ORDER BY se.occurred_at_utc, se.event_fingerprint;
            """,
            ("$fromUtc", Utc(range.FromUtc)),
            ("$toUtc", Utc(range.ToUtc)));
        var tokens = data.Query(
            """
            SELECT turn_id, token_type, SUM(token_count) AS token_count
            FROM token_usages
            WHERE turn_id IN
            (
                SELECT DISTINCT turn_id
                FROM sub_events
                WHERE occurred_at_utc >= $fromUtc AND occurred_at_utc < $toUtc AND turn_id IS NOT NULL
            )
            GROUP BY turn_id, token_type;
            """,
            ("$fromUtc", Utc(range.FromUtc)),
            ("$toUtc", Utc(range.ToUtc)));
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
            row["cache_metrics_reported"] is null ? null : Convert.ToInt32(row["cache_metrics_reported"], CultureInfo.InvariantCulture) != 0,
            NullableString(row, "workspace_id"))).ToArray();

        return ApplyFilter(events, filter).ToArray();
    }

    public IReadOnlyList<object> UnknownPricing(DateRange range, DashboardFilter? filter = null)
    {
        var unknown = new Dictionary<string, (string Provider, string Model, string Mode, string TokenType, DateTimeOffset First, DateTimeOffset Last, long Count, long MaxInputTokens)>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Events(range, filter))
        {
            var provider = ProviderFor(item);
            var totalInput = item.InputTokens + item.CacheReadTokens;
            foreach (var pair in item.Tokens.Where(static pair => pair.Value > 0))
            {
                if (pricing.Resolve(provider, item.Model, pair.Key, item.OccurredAtUtc, totalInput, item.Mode) is not null)
                {
                    continue;
                }

                var mode = string.IsNullOrWhiteSpace(item.Mode) ? "standard" : item.Mode!;
                var key = $"{provider}|{item.Model}|{mode}|{TokenTypeNormalizer.Normalize(pair.Key)}";
                if (unknown.TryGetValue(key, out var existing))
                {
                    unknown[key] = existing with { First = existing.First < item.OccurredAtUtc ? existing.First : item.OccurredAtUtc, Last = existing.Last > item.OccurredAtUtc ? existing.Last : item.OccurredAtUtc, Count = existing.Count + pair.Value, MaxInputTokens = Math.Max(existing.MaxInputTokens, totalInput) };
                }
                else
                {
                    unknown[key] = (provider, item.Model, mode, TokenTypeNormalizer.Normalize(pair.Key), item.OccurredAtUtc, item.OccurredAtUtc, pair.Value, totalInput);
                }
            }
        }

        return unknown.Values.OrderBy(item => item.First).Select(item => new UnknownPricingDto(
            item.Provider,
            item.Model,
            item.Mode,
            item.TokenType,
            item.First,
            item.Last,
            item.Count,
            BuiltInPricingCatalog.Suggest(item.Provider, item.Model, item.TokenType, item.Mode, item.MaxInputTokens))).ToArray();
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
            uniqueSessionCount = events.Select(item => item.SessionId).Where(static value => value is not null).Distinct(StringComparer.Ordinal).Count(),
            turnCount = events.Select(item => item.TurnId).Where(static value => value is not null).Distinct(StringComparer.Ordinal).Count(),
            coverage = CoverageMetadata(events, range),
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

    public bool TryTrend(DateRange range, string? interval, DashboardFilter? filter, out IReadOnlyList<object> points)
    {
        if (!TrendInterval.TryParse(interval, out var duration))
        {
            points = [];
            return false;
        }

        var zone = DateRangeResolver.FindTimeZone(range.TimeZoneId);
        var localFrom = TimeZoneInfo.ConvertTime(range.FromUtc, zone).DateTime.Date;
        var localTo = TimeZoneInfo.ConvertTime(range.ToUtc, zone).DateTime;
        var events = StatisticalEvents(range, filter);
        var result = new List<object>();

        for (var start = localFrom; start < localTo; start = start.Add(duration))
        {
            var end = start.Add(duration);
            if (end > localTo)
            {
                end = localTo;
            }

            var startUtc = LocalToUtc(start, zone);
            var endUtc = LocalToUtc(end, zone);
            var bucket = events.Where(item => item.OccurredAtUtc >= startUtc && item.OccurredAtUtc < endUtc).ToArray();
            var summary = BuildSummary("bucket", start.ToString("O", CultureInfo.InvariantCulture), bucket);
            summary["bucketStartUtc"] = startUtc;
            summary["bucketEndUtc"] = endUtc;
            result.Add(summary);
        }

        points = result;
        return true;
    }

    public IReadOnlyList<object> ComparisonTree(DateRange range, DashboardFilter? filter = null)
    {
        return StatisticalEvents(range, filter)
            .GroupBy(item => string.IsNullOrWhiteSpace(item.Model) ? "模型未提供" : item.Model, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Sum(item => item.TotalTokens))
            .Select(group =>
            {
                var children = group
                    .GroupBy(item => string.IsNullOrWhiteSpace(item.Tool) ? "非工具" : item.Tool, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(child => child.Sum(item => item.TotalTokens))
                    .Select(child => ComparisonNode("tool", child.Key, child))
                    .ToArray();
                return ComparisonNode("model", group.Key, group, children);
            })
            .Cast<object>()
            .ToArray();
    }

    public IReadOnlyList<object> Comparisons(DateRange range, string? groupBy, DashboardFilter? filter = null)
    {
        return StatisticalEvents(range, filter)
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
            .Where(row => filter is null || string.IsNullOrWhiteSpace(filter.WorkspaceId) || string.Equals(NullableString(row, "workspace_id"), filter.WorkspaceId, StringComparison.OrdinalIgnoreCase))
            .Where(row => filter is null || string.IsNullOrWhiteSpace(filter.ProjectId) || string.Equals(NullableString(row, "workspace_id"), filter.ProjectId, StringComparison.OrdinalIgnoreCase))
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
                    uniqueSessionCount = sessionEvents.Length == 0 ? 0 : 1,
                    turnCount = sessionEvents.Select(item => item.TurnId).Where(static value => value is not null).Distinct(StringComparer.Ordinal).Count(),
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

    public SessionPageDto SessionsPage(DateRange range, DashboardFilter? filter, string? cursor, int pageSize)
    {
        pageSize = Math.Clamp(pageSize, 1, 50);
        var decodedCursor = DecodeCursor(cursor);
        var rows = data.Query(
            """
            SELECT s.session_id, s.source_id, s.started_at_utc, s.last_activity_at_utc,
                   s.source_timezone, s.workspace_id, s.owner_id,
                   COALESCE(r.event_count, 0) AS event_count,
                   COALESCE(r.turn_count, 0) AS turn_count,
                   COALESCE(r.total_tokens, 0) AS total_tokens,
                   COALESCE(r.priced_tokens, 0) AS priced_tokens,
                   COALESCE(r.unpriced_tokens, 0) AS unpriced_tokens
            FROM sessions AS s
            LEFT JOIN session_usage_rollups AS r ON r.session_id = s.session_id
            WHERE s.last_activity_at_utc >= $fromUtc
              AND s.started_at_utc < $toUtc
              AND ($sourceId IS NULL OR s.source_id = $sourceId)
              AND ($workspaceId IS NULL OR s.workspace_id = $workspaceId)
              AND ($cursorTime IS NULL OR s.last_activity_at_utc < $cursorTime OR (s.last_activity_at_utc = $cursorTime AND s.session_id < $cursorId))
            ORDER BY s.last_activity_at_utc DESC, s.session_id DESC
            LIMIT $limit;
            """,
            ("$fromUtc", Utc(range.FromUtc)),
            ("$toUtc", Utc(range.ToUtc)),
            ("$sourceId", (object?)NullIfEmpty(filter?.SourceId) ?? DBNull.Value),
            ("$workspaceId", (object?)NullIfEmpty(filter?.WorkspaceId) ?? DBNull.Value),
            ("$cursorTime", (object?)decodedCursor?.Time ?? DBNull.Value),
            ("$cursorId", (object?)decodedCursor?.Id ?? DBNull.Value),
            ("$limit", pageSize + 1));

        var hasMore = rows.Count > pageSize;
        var selected = rows.Take(pageSize).ToArray();
        var ids = selected.Select(row => String(row, "session_id")).ToArray();
        var rollupEvents = LoadRollupEvents(ids);
        var result = selected.Select(row =>
        {
            var id = String(row, "session_id");
            var events = rollupEvents.TryGetValue(id, out var sessionEvents) ? sessionEvents : [];
            var tokens = AggregateTokens(events.SelectMany(item => item.Tokens));
            var coverage = CostCoverage(events);
            var model = events.Select(item => item.Model).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
            var tool = events.Select(item => item.Tool).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
            return (object)new
            {
                id,
                sourceId = String(row, "source_id"),
                startedAtUtc = String(row, "started_at_utc"),
                lastActivityAtUtc = String(row, "last_activity_at_utc"),
                endedAtUtc = DateTimeOffset.Parse(String(row, "last_activity_at_utc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).AddMinutes(30),
                sourceTimeZone = String(row, "source_timezone"),
                model,
                tool,
                workspaceId = NullableString(row, "workspace_id"),
                ownerId = NullableString(row, "owner_id"),
                eventCount = Convert.ToInt32(row["event_count"], CultureInfo.InvariantCulture),
                turnCount = Convert.ToInt32(row["turn_count"], CultureInfo.InvariantCulture),
                uniqueSessionCount = 1,
                totalTokens = tokens.Values.Sum(),
                tokens,
                tokenTypes = tokens,
                costUsd = Cost(events),
                partialCostUsd = PartialCost(events),
                pricedTokenCount = coverage.PricedTokens,
                unpricedTokenCount = coverage.UnpricedTokens,
                costCoverage = coverage.TotalTokens == 0 ? (decimal?)null : (decimal)coverage.PricedTokens / coverage.TotalTokens
            };
        }).ToArray();

        var nextCursor = hasMore && selected.Length > 0
            ? EncodeCursor(String(selected[^1], "last_activity_at_utc"), String(selected[^1], "session_id"))
            : null;
        return new SessionPageDto(result, nextCursor, hasMore);
    }

    public DashboardSnapshotDto Snapshot(DateRange range, DashboardFilter? filter, string? cursor, int pageSize)
    {
        var page = SessionsPage(range, filter, cursor, pageSize);
        var events = StatisticalEvents(range, filter);
        return new DashboardSnapshotDto(
            OverviewFromEvents(events, range),
            TrendFromEvents(events, range, TimeSpan.FromDays(1)),
            GroupByDate(events, range.TimeZoneId, static date => date.ToString("yyyy-MM", CultureInfo.InvariantCulture)),
            GroupByDate(events, range.TimeZoneId, static date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ComparisonTreeFromEvents(events),
            page.Items,
            page.NextCursor,
            page.HasMore,
            DateTimeOffset.UtcNow);
    }

    private object OverviewFromEvents(EventRow[] events, DateRange range)
    {
        var tokens = AggregateTokens(events.SelectMany(item => item.Tokens));
        var coverage = CostCoverage(events);
        var sessions = events.Select(item => item.SessionId).Where(static value => value is not null).Distinct(StringComparer.Ordinal).Count();
        return new
        {
            range.FromUtc,
            range.ToUtc,
            range.TimeZoneId,
            eventCount = events.Length,
            sessionCount = sessions,
            uniqueSessionCount = sessions,
            turnCount = events.Select(item => item.TurnId).Where(static value => value is not null).Distinct(StringComparer.Ordinal).Count(),
            coverage = CoverageMetadata(events, range),
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

    private List<object> TrendFromEvents(EventRow[] events, DateRange range, TimeSpan duration)
    {
        var zone = DateRangeResolver.FindTimeZone(range.TimeZoneId);
        var localFrom = TimeZoneInfo.ConvertTime(range.FromUtc, zone).DateTime.Date;
        var localTo = TimeZoneInfo.ConvertTime(range.ToUtc, zone).DateTime;
        var result = new List<object>();
        for (var start = localFrom; start < localTo; start = start.Add(duration))
        {
            var end = start.Add(duration);
            if (end > localTo) end = localTo;
            var startUtc = LocalToUtc(start, zone);
            var endUtc = LocalToUtc(end, zone);
            var bucket = events.Where(item => item.OccurredAtUtc >= startUtc && item.OccurredAtUtc < endUtc).ToArray();
            var summary = BuildSummary("bucket", start.ToString("O", CultureInfo.InvariantCulture), bucket);
            summary["bucketStartUtc"] = startUtc;
            summary["bucketEndUtc"] = endUtc;
            result.Add(summary);
        }

        return result;
    }

    private object[] ComparisonTreeFromEvents(EventRow[] events)
    {
        return events
            .GroupBy(item => string.IsNullOrWhiteSpace(item.Model) ? "模型未提供" : item.Model, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Sum(item => item.TotalTokens))
            .Select(group =>
            {
                var children = group
                    .GroupBy(item => string.IsNullOrWhiteSpace(item.Tool) ? "非工具" : item.Tool, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(child => child.Sum(item => item.TotalTokens))
                    .Select(child => ComparisonNode("tool", child.Key, child))
                    .ToArray();
                return (object)ComparisonNode("model", group.Key, group, children);
            })
            .ToArray();
    }

    public object? SessionTimeline(string id, string? cursor, int pageSize, bool revealContent)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        if (data.Query("SELECT session_id FROM sessions WHERE session_id = $id;", ("$id", id)).SingleOrDefault() is null)
        {
            return null;
        }

        var decodedCursor = DecodeCursor(cursor);
        var turns = data.Query(
            """
            SELECT turn_id, sequence, occurred_at_utc, effort
            FROM turns
            WHERE session_id = $sessionId
              AND ($cursorTime IS NULL OR occurred_at_utc > $cursorTime OR (occurred_at_utc = $cursorTime AND turn_id > $cursorId))
            ORDER BY occurred_at_utc, turn_id
            LIMIT $limit;
            """,
            ("$sessionId", id),
            ("$cursorTime", (object?)decodedCursor?.Time ?? DBNull.Value),
            ("$cursorId", (object?)decodedCursor?.Id ?? DBNull.Value),
            ("$limit", pageSize + 1));
        var hasMore = turns.Count > pageSize;
        var selected = turns.Take(pageSize).ToArray();
        var turnIds = selected.Select(row => String(row, "turn_id")).ToArray();
        var events = LoadTimelineEvents(id, turnIds, revealContent);
        var tokens = LoadTimelineTokens(turnIds);
        var result = selected.Select(turn =>
        {
            var turnId = String(turn, "turn_id");
            var turnEvents = events.TryGetValue(turnId, out var listed) ? listed : [];
            var turnTokens = tokens.TryGetValue(turnId, out var counts) ? counts : new Dictionary<string, long>(StringComparer.Ordinal);
            var accounting = turnEvents.Select(item => ToEventRow(item, turnTokens)).ToArray();
            var coverage = CostCoverage(accounting);
            return new
            {
                id = turnId,
                sequence = Convert.ToInt32(turn["sequence"], CultureInfo.InvariantCulture),
                occurredAtUtc = String(turn, "occurred_at_utc"),
                effort = NullableString(turn, "effort"),
                events = turnEvents,
                tokenUsage = turnTokens.Select(pair => new { tokenType = pair.Key, tokenCount = pair.Value }).ToArray(),
                tokens = turnTokens,
                costUsd = Cost(accounting),
                partialCostUsd = PartialCost(accounting),
                pricedTokenCount = coverage.PricedTokens,
                unpricedTokenCount = coverage.UnpricedTokens
            };
        }).ToArray();
        var nextCursor = hasMore && selected.Length > 0
            ? EncodeCursor(String(selected[^1], "occurred_at_utc"), String(selected[^1], "turn_id"))
            : null;
        return new { sessionId = id, items = result, nextCursor, hasMore };
    }

    private Dictionary<string, List<EventRow>> LoadRollupEvents(string[] sessionIds)
    {
        var result = sessionIds.ToDictionary(id => id, _ => new List<EventRow>(), StringComparer.Ordinal);
        if (sessionIds.Length == 0)
        {
            return result;
        }

        var parameters = sessionIds.Select((id, index) => ($"$session{index}", (object)id)).ToArray();
        var placeholders = string.Join(", ", parameters.Select(item => item.Item1));
        var facts = data.Query($"""
            SELECT f.turn_id, f.session_id, f.occurred_at_utc, f.source_id, f.model, f.tool,
                   s.adapter_kind, s.source_timezone, sess.workspace_id
            FROM turn_usage_facts AS f
            INNER JOIN sources AS s ON s.source_id = f.source_id
            INNER JOIN sessions AS sess ON sess.session_id = f.session_id
            WHERE f.session_id IN ({placeholders})
            ORDER BY f.occurred_at_utc, f.turn_id;
            """, parameters);
        var turnIds = facts.Select(row => String(row, "turn_id")).Distinct(StringComparer.Ordinal).ToArray();
        var tokens = LoadTimelineTokens(turnIds);
        foreach (var fact in facts)
        {
            var turnId = String(fact, "turn_id");
            var sessionId = String(fact, "session_id");
            var tokenMap = tokens.TryGetValue(turnId, out var counts) ? counts : new Dictionary<string, long>(StringComparer.Ordinal);
            result[sessionId].Add(new EventRow(
                String(fact, "turn_id"),
                String(fact, "source_id"),
                String(fact, "adapter_kind"),
                sessionId,
                turnId,
                DateTimeOffset.Parse(String(fact, "occurred_at_utc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                String(fact, "source_timezone"),
                "turn",
                string.Empty,
                string.Empty,
                String(fact, "model"),
                String(fact, "tool"),
                string.Empty,
                string.Empty,
                string.Empty,
                tokenMap,
                null,
                null,
                NullableString(fact, "workspace_id")));
        }

        return result;
    }

    private Dictionary<string, List<Dictionary<string, object?>>> LoadTimelineEvents(string sessionId, string[] turnIds, bool revealContent)
    {
        var result = turnIds.ToDictionary(id => id, _ => new List<Dictionary<string, object?>>(), StringComparer.Ordinal);
        if (turnIds.Length == 0)
        {
            return result;
        }

        var parameters = new List<(string Name, object Value)> { ("$sessionId", sessionId) };
        var placeholders = string.Join(", ", turnIds.Select((id, index) =>
        {
            var name = $"$turn{index}";
            parameters.Add((name, id));
            return name;
        }));
        var rows = data.Query($"""
            SELECT se.event_fingerprint, se.turn_id, se.event_type, se.occurred_at_utc,
                   se.model, se.tool, se.prompt, se.response, se.payload,
                   se.source_timezone, se.source_id, s.adapter_kind, sess.workspace_id
            FROM sub_events AS se
            INNER JOIN sources AS s ON s.source_id = se.source_id
            LEFT JOIN sessions AS sess ON sess.session_id = se.session_id
            WHERE se.session_id = $sessionId AND se.turn_id IN ({placeholders})
            ORDER BY se.occurred_at_utc, se.event_fingerprint;
            """, parameters.ToArray());
        foreach (var row in rows)
        {
            var fingerprint = String(row, "event_fingerprint");
            var prompt = String(row, "prompt");
            var response = String(row, "response");
            var payload = String(row, "payload");
            var item = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["event_fingerprint"] = fingerprint,
                ["event_type"] = String(row, "event_type"),
                ["occurred_at_utc"] = String(row, "occurred_at_utc"),
                ["source_id"] = String(row, "source_id"),
                ["adapter_kind"] = String(row, "adapter_kind"),
                ["source_timezone"] = String(row, "source_timezone"),
                ["workspace_id"] = NullableString(row, "workspace_id"),
                ["model"] = String(row, "model"),
                ["tool"] = String(row, "tool"),
                ["prompt"] = revealContent ? prompt : MaskContent(prompt),
                ["response"] = revealContent ? response : MaskContent(response),
                ["payload"] = revealContent ? payload : MaskContent(payload),
                ["contentMasked"] = !revealContent
            };
            result[String(row, "turn_id")].Add(item);
        }

        return result;
    }

    private Dictionary<string, Dictionary<string, long>> LoadTimelineTokens(string[] turnIds)
    {
        var result = turnIds.ToDictionary(id => id, _ => new Dictionary<string, long>(StringComparer.Ordinal), StringComparer.Ordinal);
        if (turnIds.Length == 0)
        {
            return result;
        }

        var parameters = turnIds.Select((id, index) => ($"$turn{index}", (object)id)).ToArray();
        var placeholders = string.Join(", ", parameters.Select(item => item.Item1));
        foreach (var row in data.Query($"SELECT turn_id, token_type, SUM(token_count) AS token_count FROM token_usages WHERE turn_id IN ({placeholders}) GROUP BY turn_id, token_type;", parameters))
        {
            result[String(row, "turn_id")][TokenTypeNormalizer.Normalize(String(row, "token_type"))] = Long(row, "token_count");
        }

        return result;
    }

    private static EventRow ToEventRow(Dictionary<string, object?> row, IReadOnlyDictionary<string, long> tokens)
    {
        return new EventRow(
            String(row, "event_fingerprint"),
            String(row, "source_id"),
            String(row, "adapter_kind"),
            null,
            null,
            DateTimeOffset.Parse(String(row, "occurred_at_utc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            String(row, "source_timezone"),
            String(row, "event_type"),
            String(row, "prompt"),
            String(row, "response"),
            String(row, "model"),
            String(row, "tool"),
            string.Empty,
            string.Empty,
            String(row, "payload"),
            tokens,
            null,
            null,
            NullableString(row, "workspace_id"));
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string EncodeCursor(string time, string id)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{time}\n{id}"))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static (string Time, string Id)? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
            var value = Encoding.UTF8.GetString(Convert.FromBase64String(padded)).Split('\n', 2);
            return value.Length == 2 && value[0].Length > 0 && value[1].Length > 0 ? (value[0], value[1]) : null;
        }
        catch (FormatException)
        {
            return null;
        }
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
                effort = NullableString(turn, "effort"),
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

    public object? EventContent(string sessionId, string fingerprint, string field)
    {
        var column = field.Trim().ToLowerInvariant() switch
        {
            "prompt" => "prompt",
            "response" => "response",
            "payload" => "payload",
            _ => null
        };
        if (column is null)
        {
            return null;
        }

        var row = data.Query($"SELECT event_fingerprint, {column} AS content FROM sub_events WHERE session_id = $sessionId AND event_fingerprint = $fingerprint;", ("$sessionId", sessionId), ("$fingerprint", fingerprint)).SingleOrDefault();
        if (row is null)
        {
            return null;
        }

        var content = Convert.ToString(row["content"], CultureInfo.InvariantCulture) ?? string.Empty;
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var preview = string.Join('\n', lines.Take(5));
        return new
        {
            eventFingerprint = String(row, "event_fingerprint"),
            field = column,
            content = preview,
            lineCount = lines.Length,
            truncated = lines.Length > 5
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

    private EventRow[] StatisticalEvents(DateRange range, DashboardFilter? filter)
    {
        return UniqueTurnEvents(Events(range, filter));
    }

    private static EventRow[] UniqueTurnEvents(IEnumerable<EventRow> events)
    {
        return events
            .GroupBy(item => item.TurnId ?? item.Fingerprint, StringComparer.Ordinal)
            .Select(group =>
            {
                var ordered = group.OrderBy(item => item.OccurredAtUtc).ThenBy(item => item.Fingerprint).ToArray();
                var modelEvent = ordered.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Model)) ?? ordered[0];
                var tool = ordered.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Tool))?.Tool ?? string.Empty;
                return modelEvent with { Tool = tool };
            })
            .ToArray();
    }

    private Dictionary<string, object?> ComparisonNode(string kind, string name, IEnumerable<EventRow> events, IReadOnlyList<object>? children = null)
    {
        var summary = BuildSummary("name", name, events);
        summary["kind"] = kind;
        if (children is not null)
        {
            summary["children"] = children;
        }

        return summary;
    }

    private static DateTimeOffset LocalToUtc(DateTime local, TimeZoneInfo zone)
        => new(DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone), DateTimeKind.Utc));

    private static class TrendInterval
    {
        private static readonly Dictionary<string, TimeSpan> Values = new(StringComparer.OrdinalIgnoreCase)
        {
            ["15m"] = TimeSpan.FromMinutes(15),
            ["30m"] = TimeSpan.FromMinutes(30),
            ["1h"] = TimeSpan.FromHours(1),
            ["3h"] = TimeSpan.FromHours(3),
            ["6h"] = TimeSpan.FromHours(6),
            ["1d"] = TimeSpan.FromDays(1),
            ["3d"] = TimeSpan.FromDays(3),
            ["7d"] = TimeSpan.FromDays(7)
        };

        public static bool TryParse(string? value, out TimeSpan duration)
        {
            duration = default;
            return value is not null && Values.TryGetValue(value, out duration);
        }
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
            ["uniqueSessionCount"] = array.Select(item => item.SessionId).Where(static value => value is not null).Distinct(StringComparer.Ordinal).Count(),
            ["turnCount"] = array.Select(item => item.TurnId).Where(static value => value is not null).Distinct(StringComparer.Ordinal).Count(),
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

    private static object CoverageMetadata(EventRow[] events, DateRange range)
    {
        var first = events.Length == 0 ? (DateTimeOffset?)null : events.Min(item => item.OccurredAtUtc);
        var last = events.Length == 0 ? (DateTimeOffset?)null : events.Max(item => item.OccurredAtUtc);
        return new
        {
            selected = new { fromUtc = range.FromUtc, toUtc = range.ToUtc, timeZoneId = range.TimeZoneId },
            eventCount = events.Length,
            firstEventAtUtc = first,
            lastEventAtUtc = last,
            hasEvents = events.Length > 0,
            sourceTimeZones = events.Select(item => item.SourceTimeZone).Where(static value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
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

    private IEnumerable<EventRow> ApplyFilter(IEnumerable<EventRow> events, DashboardFilter? filter)
    {
        if (filter is null)
        {
            return events;
        }

        var tokenType = string.IsNullOrWhiteSpace(filter.TokenType) ? null : CanonicalTokenType(filter.TokenType);
        var tagEntities = TagEntities(filter.Tag);
        return events
            .Where(item => string.IsNullOrWhiteSpace(filter.SourceId) || string.Equals(item.SourceId, filter.SourceId, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(filter.Tool) || string.Equals(item.Tool, filter.Tool, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(filter.Model) || string.Equals(item.Model, filter.Model, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(filter.WorkspaceId) || string.Equals(item.WorkspaceId, filter.WorkspaceId, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(filter.ProjectId) || string.Equals(item.WorkspaceId, filter.ProjectId, StringComparison.OrdinalIgnoreCase))
            .Where(item => tagEntities is null || tagEntities.Contains(item.SourceId) || item.SessionId is { } sessionId && tagEntities.Contains(sessionId) || item.WorkspaceId is { } workspaceId && tagEntities.Contains(workspaceId))
            .Select(item => tokenType is null
                ? item
                : item with { Tokens = item.Tokens.Where(pair => string.Equals(CanonicalTokenType(pair.Key), tokenType, StringComparison.Ordinal)).ToDictionary(pair => CanonicalTokenType(pair.Key), pair => pair.Value, StringComparer.Ordinal) })
            .Where(item => tokenType is null || item.Tokens.Count > 0);
    }

    private static bool HasNonSourceFilter(DashboardFilter filter) => !string.IsNullOrWhiteSpace(filter.Tool) || !string.IsNullOrWhiteSpace(filter.Model) || !string.IsNullOrWhiteSpace(filter.TokenType) || !string.IsNullOrWhiteSpace(filter.WorkspaceId) || !string.IsNullOrWhiteSpace(filter.ProjectId) || !string.IsNullOrWhiteSpace(filter.Tag);

    private HashSet<string>? TagEntities(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var parts = tag.Split('=', 2, StringSplitOptions.TrimEntries);
        var key = parts[0];
        var value = parts.Length > 1 ? parts[1] : null;
        return data.Query("""
            SELECT source_id AS entity_id FROM source_tags st INNER JOIN tags t ON t.tag_id = st.tag_id WHERE (t.tag_key = $key OR ($value IS NULL AND t.tag_value = $key)) AND ($value IS NULL OR t.tag_value = $value)
            UNION SELECT session_id FROM session_tags st INNER JOIN tags t ON t.tag_id = st.tag_id WHERE (t.tag_key = $key OR ($value IS NULL AND t.tag_value = $key)) AND ($value IS NULL OR t.tag_value = $value)
            UNION SELECT project_id FROM project_tags st INNER JOIN tags t ON t.tag_id = st.tag_id WHERE (t.tag_key = $key OR ($value IS NULL AND t.tag_value = $key)) AND ($value IS NULL OR t.tag_value = $value);
            """, ("$key", key), ("$value", (object?)value ?? DBNull.Value)).Select(row => String(row, "entity_id")).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

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
            return document.RootElement.TryGetProperty("mode", out var mode) && mode.ValueKind == JsonValueKind.String
                ? PricingMode.Normalize(mode.GetString())
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

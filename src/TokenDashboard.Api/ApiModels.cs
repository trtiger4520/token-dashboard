using TokenDashboard.Data;

namespace TokenDashboard.Api;

public sealed record SourceImportRequest(
    string Adapter,
    string? Path = null,
    string? FileName = null,
    string? Content = null,
    string? WorkspaceId = null,
    string? OwnerId = null);

public sealed record TagRequest(
    string Scope,
    string EntityId,
    string Key,
    string Value);

public sealed record PriceWriteRequest(
    string Provider,
    string Model,
    string TokenType,
    decimal UsdPerMillionTokens,
    string? Mode = "standard",
    long MinimumInputTokens = 0,
    long? MaximumInputTokens = null,
    DateTimeOffset? EffectiveFromUtc = null,
    DateTimeOffset? EffectiveToUtc = null,
    string? SourceName = null,
    string? SourceUrl = null);

public sealed record PriceDeactivateRequest(string Provider, string Model, string TokenType, string? Mode = null);

public sealed record PricingSuggestionDto(
    string CatalogModel,
    string CatalogMode,
    string CatalogTokenType,
    long MinimumInputTokens,
    long? MaximumInputTokens,
    decimal UsdPerMillionTokens,
    string EffectiveFrom,
    string SourceName,
    string SourceUrl,
    string Reason);

public sealed record UnknownPricingDto(
    string Provider,
    string Model,
    string Mode,
    string TokenType,
    DateTimeOffset EarliestEventUtc,
    DateTimeOffset LatestEventUtc,
    long TokenCount,
    PricingSuggestionDto? Suggestion);

public sealed record PricingEntryDto(
    string Provider,
    string Model,
    string Mode,
    string TokenType,
    long MinimumInputTokens,
    long? MaximumInputTokens,
    decimal UsdPerMillionTokens,
    string EffectiveFrom,
    string? EffectiveTo,
    string SourceName,
    string SourceUrl,
    bool IsOverride,
    int OverrideVersion = 1,
    string CreatedAtUtc = "",
    string CatalogVersion = "",
    string SourceKind = "");

public sealed record ExportRequest(
    string Format,
    bool IncludeContent = false,
    bool ConfirmIncludeContent = false,
    string? Preset = null,
    string? From = null,
    string? To = null,
    string? TimeZone = null);

public sealed record DeleteDataRequest(
    bool ClearAll = false,
    IReadOnlyList<string>? SessionIds = null,
    IReadOnlyList<string>? SourceIds = null);

public sealed record DashboardFilter(
    string? SourceId = null,
    string? Tool = null,
    string? Model = null,
    string? TokenType = null,
    string? WorkspaceId = null,
    string? ProjectId = null,
    string? Tag = null);

public sealed record BudgetRequest(
    string Name,
    decimal AmountUsd,
    string Period = "monthly",
    string? FromDate = null,
    string? ToDate = null,
    string? ProjectId = null,
    string? Tag = null,
    bool Enabled = true);

public sealed record BudgetDto(
    string Id,
    string Name,
    decimal AmountUsd,
    string Period,
    string FromDate,
    string? ToDate,
    string? ProjectId,
    string? Tag,
    bool Enabled);

public sealed record BudgetSummaryDto(
    string BudgetId,
    decimal SpentUsd,
    long Tokens,
    decimal? CostCoverage,
    decimal PercentUsed);

public sealed record EventRow(
    string Fingerprint,
    string SourceId,
    string AdapterKind,
    string? SessionId,
    string? TurnId,
    DateTimeOffset OccurredAtUtc,
    string SourceTimeZone,
    string EventType,
    string Prompt,
    string Response,
    string Model,
    string Tool,
    string Subagent,
    string Workflow,
    string Payload,
    IReadOnlyDictionary<string, long> Tokens,
    string? Mode = null,
    bool? CacheMetricsReported = null,
    string? WorkspaceId = null)
{
    public long InputTokens => Tokens.Where(static pair => TokenTypeNormalizer.IsCacheableInput(pair.Key)).Sum(static pair => pair.Value);

    public long CachedInputTokens => CacheReadTokens;

    public long CacheReadTokens => Tokens.Where(static pair => TokenTypeNormalizer.IsCacheRead(pair.Key)).Sum(static pair => pair.Value);

    public long OutputTokens => Tokens.TryGetValue("output", out var value) ? value : 0;

    public long TotalTokens => Tokens.Values.Sum();

    public IReadOnlyDictionary<string, long> TokenTypes => Tokens;

    public decimal? CacheHitRate
    {
        get
        {
            var total = InputTokens + CachedInputTokens;
            return total == 0 ? null : (decimal)CachedInputTokens / total;
        }
    }

    // Providers differ in whether cache counters are emitted.  Keep this fact
    // separate from a zero hit rate so the UI can show coverage honestly
    public bool CacheReported => CacheMetricsReported ?? Tokens.Keys.Any(static key =>
        TokenTypeNormalizer.IsCacheRead(key)
        || TokenTypeNormalizer.Normalize(key).Contains("cache", StringComparison.Ordinal));
}

public static class TokenTypeNormalizer
{
    public static string Normalize(string value) => value.Trim().ToLowerInvariant().Replace('_', '-');

    public static bool IsCacheRead(string value)
    {
        var normalized = Normalize(value);
        return normalized is "cached-input" or "cache-read";
    }

    public static bool IsCacheableInput(string value)
    {
        var normalized = Normalize(value);
        return normalized is "input" or "cacheable-input";
    }

    public static IReadOnlyList<string> PricingVariants(string value)
    {
        var normalized = Normalize(value);
        return normalized switch
        {
            "cached-input" or "cache-read" => [normalized, "cached-input", "cache-read"],
            "cache-write-input" or "cache-write" => [normalized, "cache-write-input", "cache-write"],
            "cacheable-input" => ["cacheable-input", "input"],
            _ => [normalized]
        };
    }
}

public sealed record PricingOverride(PriceCatalogEntry Entry, DateTimeOffset EffectiveFromUtc, DateTimeOffset? EffectiveToUtc);

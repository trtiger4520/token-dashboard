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
    bool IsOverride);

public sealed record ExportRequest(
    string Format,
    bool IncludeContent = true,
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
    string? TokenType = null);

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
    string? Mode = null)
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
            "cacheable-input" => ["cacheable-input", "input"],
            _ => [normalized]
        };
    }
}

public sealed record PricingOverride(PriceCatalogEntry Entry, DateTimeOffset EffectiveFromUtc, DateTimeOffset? EffectiveToUtc);

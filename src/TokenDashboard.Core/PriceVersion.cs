namespace TokenDashboard.Core;

public sealed record PriceVersion : ScopedContract
{
    private readonly string currency;

    public PriceVersion(
        string provider,
        string model,
        string mode,
        TokenType tokenType,
        decimal usdPerToken,
        long minimumInputTokens,
        long? maximumInputTokens,
        DateTimeOffset effectiveFromUtc,
        DateTimeOffset? effectiveToUtc = null,
        string? workspaceId = null,
        string? ownerId = null)
        : base(workspaceId, ownerId)
    {
        currency = "USD";
        Provider = ContractValidation.Required(provider, nameof(provider));
        Model = ContractValidation.Required(model, nameof(model));
        Mode = ContractValidation.Required(mode, nameof(mode));
        TokenType = tokenType;
        if (usdPerToken < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(usdPerToken), usdPerToken, "Price cannot be negative");
        }

        UsdPerToken = usdPerToken;
        MinimumInputTokens = ContractValidation.NonNegative(minimumInputTokens, nameof(minimumInputTokens));
        MaximumInputTokens = maximumInputTokens is null
            ? null
            : ContractValidation.NonNegative(maximumInputTokens.Value, nameof(maximumInputTokens));
        if (MaximumInputTokens is not null && MaximumInputTokens <= MinimumInputTokens)
        {
            throw new ArgumentException("Input token interval must have a positive duration", nameof(maximumInputTokens));
        }

        EffectiveFromUtc = ContractValidation.Utc(effectiveFromUtc, nameof(effectiveFromUtc));
        EffectiveToUtc = effectiveToUtc is null
            ? null
            : ContractValidation.Utc(effectiveToUtc.Value, nameof(effectiveToUtc));

        if (EffectiveToUtc is not null && EffectiveToUtc <= EffectiveFromUtc)
        {
            throw new ArgumentException("Price interval must have a positive duration", nameof(effectiveToUtc));
        }
    }

    public PriceVersion(
        string provider,
        string model,
        TokenType tokenType,
        decimal usdPerToken,
        DateTimeOffset effectiveFromUtc,
        DateTimeOffset? effectiveToUtc = null,
        string? workspaceId = null,
        string? ownerId = null)
        : this(
            provider,
            model,
            "standard",
            tokenType,
            usdPerToken,
            0,
            null,
            effectiveFromUtc,
            effectiveToUtc,
            workspaceId,
            ownerId)
    {
    }

    public string Provider { get; }

    public string Model { get; }

    public string Mode { get; }

    public TokenType TokenType { get; }

    public string Currency => currency;

    public decimal UsdPerToken { get; }

    public long MinimumInputTokens { get; }

    public long? MaximumInputTokens { get; }

    public DateTimeOffset EffectiveFromUtc { get; }

    public DateTimeOffset? EffectiveToUtc { get; }

    public bool IsEffectiveAt(DateTimeOffset atUtc)
    {
        var utc = ContractValidation.Utc(atUtc, nameof(atUtc));
        return utc >= EffectiveFromUtc && (EffectiveToUtc is null || utc < EffectiveToUtc);
    }

    public bool MatchesInputTokens(long totalInputTokens)
    {
        ContractValidation.NonNegative(totalInputTokens, nameof(totalInputTokens));
        return totalInputTokens >= MinimumInputTokens
            && (MaximumInputTokens is null || totalInputTokens < MaximumInputTokens);
    }

    public decimal CalculateUsd(long tokenCount)
    {
        ContractValidation.NonNegative(tokenCount, nameof(tokenCount));
        return UsdPerToken * tokenCount;
    }

    public static PriceVersion PerMillionTokens(
        string provider,
        string model,
        TokenType tokenType,
        decimal usdPerMillionTokens,
        DateTimeOffset effectiveFromUtc,
        DateTimeOffset? effectiveToUtc = null,
        string? workspaceId = null,
        string? ownerId = null)
    {
        return PerMillionTokens(
            provider,
            model,
            "standard",
            tokenType,
            usdPerMillionTokens,
            0,
            null,
            effectiveFromUtc,
            effectiveToUtc,
            workspaceId,
            ownerId);
    }

    public static PriceVersion PerMillionTokens(
        string provider,
        string model,
        string mode,
        TokenType tokenType,
        decimal usdPerMillionTokens,
        long minimumInputTokens,
        long? maximumInputTokens,
        DateTimeOffset effectiveFromUtc,
        DateTimeOffset? effectiveToUtc = null,
        string? workspaceId = null,
        string? ownerId = null)
    {
        if (usdPerMillionTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(usdPerMillionTokens), usdPerMillionTokens, "Price cannot be negative");
        }

        return new PriceVersion(
            provider,
            model,
            mode,
            tokenType,
            usdPerMillionTokens / 1_000_000m,
            minimumInputTokens,
            maximumInputTokens,
            effectiveFromUtc,
            effectiveToUtc,
            workspaceId,
            ownerId);
    }
}

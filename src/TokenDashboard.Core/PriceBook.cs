namespace TokenDashboard.Core;

public sealed class PriceBook
{
    private readonly IReadOnlyList<PriceVersion> versions;

    public PriceBook(IEnumerable<PriceVersion> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);
        this.versions = versions.ToArray();
    }

    public PriceVersion? Find(
        string provider,
        string model,
        string mode,
        TokenType tokenType,
        DateTimeOffset atUtc,
        long totalInputTokens)
    {
        var requiredProvider = ContractValidation.Required(provider, nameof(provider));
        var requiredModel = ContractValidation.Required(model, nameof(model));
        var requiredMode = ContractValidation.Required(mode, nameof(mode));
        var utc = ContractValidation.Utc(atUtc, nameof(atUtc));
        ContractValidation.NonNegative(totalInputTokens, nameof(totalInputTokens));

        return versions
            .Where(version =>
                string.Equals(version.Provider, requiredProvider, StringComparison.Ordinal)
                && string.Equals(version.Model, requiredModel, StringComparison.Ordinal)
                && string.Equals(version.Mode, requiredMode, StringComparison.Ordinal)
                && version.TokenType == tokenType
                && version.IsEffectiveAt(utc)
                && version.MatchesInputTokens(totalInputTokens))
            .OrderByDescending(version => version.EffectiveFromUtc)
            .ThenByDescending(version => version.MinimumInputTokens)
            .FirstOrDefault();
    }

    public PriceVersion? Find(
        string provider,
        string model,
        TokenType tokenType,
        DateTimeOffset atUtc,
        long totalInputTokens)
    {
        return Find(provider, model, "standard", tokenType, atUtc, totalInputTokens);
    }

    public decimal? CalculateUsd(
        string provider,
        string model,
        string mode,
        TokenUsage usage,
        DateTimeOffset atUtc)
    {
        ArgumentNullException.ThrowIfNull(usage);
        var totalInputTokens = usage.InputTokens;
        var total = 0m;
        foreach (var pair in usage.Counts)
        {
            if (pair.Value == 0)
            {
                continue;
            }

            var price = Find(provider, model, mode, pair.Key, atUtc, totalInputTokens);
            if (price is null)
            {
                return null;
            }

            total += price.CalculateUsd(pair.Value);
        }

        return total;
    }

    public decimal? CalculateUsd(
        string provider,
        string model,
        TokenUsage usage,
        DateTimeOffset atUtc)
    {
        return CalculateUsd(provider, model, "standard", usage, atUtc);
    }
}

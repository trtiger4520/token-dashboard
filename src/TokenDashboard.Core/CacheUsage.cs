namespace TokenDashboard.Core;

public sealed record CacheUsage
{
    public CacheUsage(long hitTokens, long missTokens, long writeTokens = 0)
    {
        HitTokens = ContractValidation.NonNegative(hitTokens, nameof(hitTokens));
        MissTokens = ContractValidation.NonNegative(missTokens, nameof(missTokens));
        WriteTokens = ContractValidation.NonNegative(writeTokens, nameof(writeTokens));
    }

    public long HitTokens { get; }

    public long MissTokens { get; }

    public long WriteTokens { get; }

    public long LookupTokens => checked(HitTokens + MissTokens);

    public decimal? HitRate => LookupTokens == 0 ? null : (decimal)HitTokens / LookupTokens;

    public static CacheUsage Empty { get; } = new(0, 0);

    public static CacheUsage FromHitsAndMisses(long hitTokens, long missTokens, long writeTokens = 0)
    {
        return new CacheUsage(hitTokens, missTokens, writeTokens);
    }
}

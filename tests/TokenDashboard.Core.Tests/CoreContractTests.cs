using TokenDashboard.Core;
using Xunit;

namespace TokenDashboard.Core.Tests;

public sealed class CoreContractTests
{
    private static readonly DateTimeOffset BaseUtc = new(2026, 7, 26, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ContractsRequireUtcAndPreserveWorkspaceAndOwnerScope()
    {
        var source = new Source(
            "source-1",
            "Local source",
            "codex",
            "Asia/Taipei",
            BaseUtc,
            "workspace-1",
            "owner-1");

        Assert.Equal(BaseUtc, source.CreatedAtUtc);
        Assert.Equal("Asia/Taipei", source.SourceTimeZone);
        Assert.Equal("workspace-1", source.WorkspaceId);
        Assert.Equal("owner-1", source.OwnerId);
        Assert.Throws<ArgumentException>(() => new Source(
            "source-1",
            "Local source",
            "codex",
            "Asia/Taipei",
            new DateTimeOffset(2026, 7, 26, 8, 0, 0, TimeSpan.FromHours(8))));
    }

    [Fact]
    public void CacheHitRateUsesHitsDividedByHitsPlusMisses()
    {
        var usage = CacheUsage.FromHitsAndMisses(75, 25, 10);

        Assert.Equal(100, usage.LookupTokens);
        Assert.Equal(0.75m, usage.HitRate);
        Assert.Null(CacheUsage.Empty.HitRate);
    }

    [Fact]
    public void EventFingerprintIsStableForRepeatedScansAndChangesForIdentityData()
    {
        var first = EventFingerprint.Create(
            "source-1",
            "turn.completed",
            BaseUtc,
            "Asia/Taipei",
            "{\"turn\":1}",
            "session-1",
            "turn-1",
            1);
        var repeated = EventFingerprint.Create(
            new EventFingerprintInput(
                "source-1",
                "turn.completed",
                BaseUtc,
                "Asia/Taipei",
                "{\"turn\":1}",
                "session-1",
                "turn-1",
                1));
        var changed = EventFingerprint.Create(
            "source-1",
            "turn.completed",
            BaseUtc,
            "Asia/Taipei",
            "{\"turn\":2}",
            "session-1",
            "turn-1",
            1);

        Assert.Equal(first, repeated);
        Assert.Equal(64, first.Value.Length);
        Assert.NotEqual(first, changed);
    }

    [Fact]
    public void PriceVersionsUseHalfOpenEffectiveIntervals()
    {
        var current = PriceVersion.PerMillionTokens(
            "provider-a",
            "model-a",
            TokenType.Input,
            2m,
            BaseUtc,
            BaseUtc.AddDays(1));
        var next = PriceVersion.PerMillionTokens(
            "provider-a",
            "model-a",
            TokenType.Input,
            3m,
            BaseUtc.AddDays(1));
        var book = new PriceBook([current, next]);

        Assert.Same(current, book.Find("provider-a", "model-a", TokenType.Input, BaseUtc.AddHours(12), 0));
        Assert.Same(next, book.Find("provider-a", "model-a", TokenType.Input, BaseUtc.AddDays(1), 0));
        Assert.Equal(2m, book.CalculateUsd(
            "provider-a",
            "model-a",
            TokenUsage.From((TokenType.Input, 1_000_000)),
            BaseUtc.AddHours(12)));
    }

    [Fact]
    public void UnknownPriceReturnsNullInsteadOfAnEstimate()
    {
        var book = new PriceBook([
            PriceVersion.PerMillionTokens("provider-a", "model-a", TokenType.Input, 2m, BaseUtc)
        ]);
        var usage = TokenUsage.From(
            (TokenType.Input, 1_000_000),
            (TokenType.Create("audio"), 1_000_000));

        Assert.Null(book.CalculateUsd("provider-a", "model-a", usage, BaseUtc));
    }

    [Fact]
    public void PriceMatchingIsolatedByProviderAndMode()
    {
        var book = new PriceBook([
            PriceVersion.PerMillionTokens("provider-a", "model-a", TokenType.Input, 2m, BaseUtc),
            PriceVersion.PerMillionTokens("provider-b", "model-a", TokenType.Input, 4m, BaseUtc),
            PriceVersion.PerMillionTokens("provider-a", "model-a", "fast", TokenType.Input, 6m, 0, null, BaseUtc)
        ]);

        Assert.Equal("provider-a", book.Find("provider-a", "model-a", "standard", TokenType.Input, BaseUtc, 10)?.Provider);
        Assert.Equal("provider-b", book.Find("provider-b", "model-a", "standard", TokenType.Input, BaseUtc, 10)?.Provider);
        Assert.Equal("fast", book.Find("provider-a", "model-a", "fast", TokenType.Input, BaseUtc, 10)?.Mode);
    }

    [Fact]
    public void PriceThresholdUsesHalfOpenRangesForStandardAndLongContextModes()
    {
        const long threshold = 1_000_000;
        var book = new PriceBook([
            PriceVersion.PerMillionTokens("provider-a", "model-a", "standard", TokenType.Input, 2m, 0, threshold + 1, BaseUtc),
            PriceVersion.PerMillionTokens("provider-a", "model-a", "long-context-1m", TokenType.Input, 3m, threshold + 1, null, BaseUtc)
        ]);

        Assert.Equal("standard", book.Find("provider-a", "model-a", "standard", TokenType.Input, BaseUtc, threshold)?.Mode);
        Assert.Equal("long-context-1m", book.Find("provider-a", "model-a", "long-context-1m", TokenType.Input, BaseUtc, threshold + 1)?.Mode);
        Assert.Null(book.Find("provider-a", "model-a", "standard", TokenType.Input, BaseUtc, threshold + 1));
    }

    [Fact]
    public void LatestEffectiveDateThenHighestMinimumInputSelectsMostSpecificRule()
    {
        var book = new PriceBook([
            PriceVersion.PerMillionTokens("provider-a", "model-a", TokenType.Input, 1m, BaseUtc),
            PriceVersion.PerMillionTokens("provider-a", "model-a", TokenType.Input, 2m, BaseUtc.AddDays(1)),
            PriceVersion.PerMillionTokens("provider-a", "model-a", "standard", TokenType.Input, 3m, 1_000, null, BaseUtc.AddDays(1))
        ]);

        Assert.Equal(1m, book.Find("provider-a", "model-a", TokenType.Input, BaseUtc.AddHours(1), 2)?.UsdPerToken * 1_000_000m);
        Assert.Equal(2m, book.Find("provider-a", "model-a", TokenType.Input, BaseUtc.AddDays(1), 500)?.UsdPerToken * 1_000_000m);
        Assert.Equal(3m, book.Find("provider-a", "model-a", TokenType.Input, BaseUtc.AddDays(1), 1_000)?.UsdPerToken * 1_000_000m);
    }

    [Fact]
    public void UnknownModeOrModelRemainsUnpriced()
    {
        var book = new PriceBook([
            PriceVersion.PerMillionTokens("provider-a", "model-a", TokenType.Input, 2m, BaseUtc)
        ]);

        Assert.Null(book.CalculateUsd("provider-a", "model-a", "future-mode", TokenUsage.From((TokenType.Input, 1)), BaseUtc));
        Assert.Null(book.CalculateUsd("provider-a", "future-model", TokenUsage.From((TokenType.Input, 1)), BaseUtc));
    }

    [Fact]
    public void SessionEndIsDerivedFromLastActivityWithThirtyMinutesInactivity()
    {
        var lastActivity = BaseUtc.AddHours(2);
        var session = new Session(
            "session-1",
            "source-1",
            BaseUtc,
            lastActivity,
            "Asia/Taipei");

        Assert.Equal(TimeSpan.FromMinutes(30), Session.InactivityTimeout);
        Assert.Equal(lastActivity.AddMinutes(30), session.DerivedEndedAtUtc);
        Assert.Equal(session.DerivedEndedAtUtc, Session.DeriveEndUtc(lastActivity));
        Assert.Throws<ArgumentException>(() => new Session(
            "session-1",
            "source-1",
            BaseUtc,
            lastActivity.AddHours(-3),
            "Asia/Taipei"));
    }

    [Fact]
    public void TokenTypesAcceptFutureProviderSpecificValues()
    {
        var providerSpecific = TokenType.Create("provider-specific-v2");
        var usage = TokenUsage.From((providerSpecific, 42));

        Assert.Equal(42, usage[providerSpecific]);
        Assert.Equal(42, usage.TotalTokens);
    }
}

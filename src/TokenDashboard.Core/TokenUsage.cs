using System.Collections.ObjectModel;

namespace TokenDashboard.Core;

public sealed record TokenUsage
{
    public TokenUsage(IEnumerable<KeyValuePair<TokenType, long>> counts)
    {
        ArgumentNullException.ThrowIfNull(counts);

        var copy = new Dictionary<TokenType, long>();
        foreach (var pair in counts)
        {
            ContractValidation.NonNegative(pair.Value, nameof(counts));
            copy[pair.Key] = pair.Value;
        }

        Counts = new ReadOnlyDictionary<TokenType, long>(copy);
    }

    public IReadOnlyDictionary<TokenType, long> Counts { get; }

    public long this[TokenType tokenType] => Counts.TryGetValue(tokenType, out var value) ? value : 0;

    public long TotalTokens => Counts.Values.Sum();

    public long InputTokens => this[TokenType.Input];

    public long OutputTokens => this[TokenType.Output];

    public long CachedInputTokens => this[TokenType.CachedInput];

    public static TokenUsage Empty { get; } = new([]);

    public static TokenUsage From(params (TokenType Type, long Count)[] counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        return new TokenUsage(counts.Select(pair => new KeyValuePair<TokenType, long>(pair.Type, pair.Count)));
    }
}

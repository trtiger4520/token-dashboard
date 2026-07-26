namespace TokenDashboard.Core;

public readonly record struct TokenType
{
    public TokenType(string value)
    {
        Value = ContractValidation.Required(value, nameof(value));
    }

    public string Value { get; }

    public static TokenType Input { get; } = new("input");

    public static TokenType Output { get; } = new("output");

    public static TokenType CachedInput { get; } = new("cached-input");

    public static TokenType Reasoning { get; } = new("reasoning");

    public static TokenType Create(string value) => new(value);

    public override string ToString() => Value;
}

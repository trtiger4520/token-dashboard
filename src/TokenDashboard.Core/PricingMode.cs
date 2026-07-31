namespace TokenDashboard.Core;

public static class PricingMode
{
    public const string Fast = "fast";

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim();
        return string.Equals(normalized, "priority", StringComparison.OrdinalIgnoreCase)
            ? Fast
            : normalized;
    }
}

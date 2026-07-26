namespace TokenDashboard.Core;

internal static class ContractValidation
{
    public static string Required(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value is required", parameterName);
        }

        return value.Trim();
    }

    public static string? Optional(string? value, string parameterName)
    {
        if (value is null)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value cannot be blank", parameterName)
            : value.Trim();
    }

    public static DateTimeOffset Utc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must be UTC", parameterName);
        }

        return value;
    }

    public static long NonNegative(long value, string parameterName)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Value cannot be negative");
        }

        return value;
    }
}

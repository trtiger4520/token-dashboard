using System.Globalization;
using Microsoft.Data.Sqlite;
using TokenDashboard.Core;

namespace TokenDashboard.Data;

public sealed record PriceQuote(bool IsPriced, decimal? Usd, string Model, TokenType TokenType)
{
    public static PriceQuote Unknown(string model, TokenType tokenType) => new(false, null, model, tokenType);
}

public sealed class HistoricalPricingService
{
    private readonly SqliteConnection connection;

    public HistoricalPricingService(SqliteConnection connection)
    {
        this.connection = connection ?? throw new ArgumentNullException(nameof(connection));
        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
        }

        SchemaMigrator.Migrate(connection);
    }

    public void Add(PriceVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO price_versions
                (price_version_id, provider, model, mode, token_type, minimum_input_tokens, maximum_input_tokens,
                 currency, usd_per_token, effective_from_utc, effective_to_utc)
            VALUES
                ($priceVersionId, $provider, $model, $mode, $tokenType, $minimumInputTokens, $maximumInputTokens,
                 'USD', $usdPerToken, $effectiveFromUtc, $effectiveToUtc);
            """;
        command.Parameters.AddWithValue("$priceVersionId", StableId(version));
        command.Parameters.AddWithValue("$provider", version.Provider);
        command.Parameters.AddWithValue("$model", version.Model);
        command.Parameters.AddWithValue("$mode", version.Mode);
        command.Parameters.AddWithValue("$tokenType", version.TokenType.Value);
        command.Parameters.AddWithValue("$minimumInputTokens", version.MinimumInputTokens);
        command.Parameters.AddWithValue("$maximumInputTokens", version.MaximumInputTokens is null ? DBNull.Value : version.MaximumInputTokens.Value);
        command.Parameters.AddWithValue("$usdPerToken", version.UsdPerToken.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$effectiveFromUtc", Utc(version.EffectiveFromUtc));
        command.Parameters.AddWithValue("$effectiveToUtc", version.EffectiveToUtc is null ? DBNull.Value : Utc(version.EffectiveToUtc.Value));
        command.ExecuteNonQuery();
    }

    public PriceQuote Calculate(
        string provider,
        string model,
        string mode,
        TokenType tokenType,
        long tokenCount,
        long totalInputTokens,
        DateTimeOffset eventUtc)
    {
        if (tokenCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tokenCount), tokenCount, "Token count cannot be negative");
        }

        if (totalInputTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalInputTokens), totalInputTokens, "Input token count cannot be negative");
        }

        var requiredProvider = Required(provider, nameof(provider));
        var requiredModel = Required(model, nameof(model));
        var requiredMode = Required(mode, nameof(mode));
        var utc = eventUtc.ToUniversalTime();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT usd_per_token
            FROM price_versions
            WHERE provider = $provider
              AND model = $model
              AND mode = $mode
              AND token_type = $tokenType
              AND minimum_input_tokens <= $totalInputTokens
              AND (maximum_input_tokens IS NULL OR $totalInputTokens < maximum_input_tokens)
              AND effective_from_utc <= $eventUtc
              AND (effective_to_utc IS NULL OR effective_to_utc > $eventUtc)
            ORDER BY effective_from_utc DESC, minimum_input_tokens DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$provider", requiredProvider);
        command.Parameters.AddWithValue("$model", requiredModel);
        command.Parameters.AddWithValue("$mode", requiredMode);
        command.Parameters.AddWithValue("$tokenType", tokenType.Value);
        command.Parameters.AddWithValue("$totalInputTokens", totalInputTokens);
        command.Parameters.AddWithValue("$eventUtc", Utc(utc));
        var value = command.ExecuteScalar();
        if (value is null || value is DBNull)
        {
            return PriceQuote.Unknown(requiredModel, tokenType);
        }

        var usdPerToken = decimal.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);
        return new PriceQuote(true, usdPerToken * tokenCount, requiredModel, tokenType);
    }

    public PriceQuote Calculate(
        string provider,
        string model,
        TokenType tokenType,
        long tokenCount,
        long totalInputTokens,
        DateTimeOffset eventUtc)
    {
        return Calculate(provider, model, "standard", tokenType, tokenCount, totalInputTokens, eventUtc);
    }

    private static string StableId(PriceVersion version) => string.Join(
        ":",
        version.Provider,
        version.Model,
        version.Mode,
        version.TokenType.Value,
        version.MinimumInputTokens.ToString(CultureInfo.InvariantCulture),
        version.MaximumInputTokens?.ToString(CultureInfo.InvariantCulture) ?? "*",
        Utc(version.EffectiveFromUtc));

    private static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string Required(string value, string parameterName)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value is required", parameterName)
            : value.Trim();
    }
}

using Microsoft.Data.Sqlite;
using TokenDashboard.Core;
using TokenDashboard.Data;
using Xunit;

namespace TokenDashboard.Data.Tests;

public sealed class DataStorageTests
{
    [Fact]
    public void SchemaMigratorIsReentrantAndCreatesVersionedSchema()
    {
        using var connection = OpenConnection();

        SchemaMigrator.Migrate(connection);
        SchemaMigrator.Migrate(connection);

        Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM schema_versions;"));
        Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'sources';"));
        Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'imports';"));
        Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'search_index';"));
    }

    [Fact]
    public void SchemaEnforcesForeignKeysUniqueFingerprintsAndIndexes()
    {
        using var connection = OpenConnection();

        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO sessions (session_id, source_id, started_at_utc, last_activity_at_utc, source_timezone) VALUES ('session', 'missing', '2026-01-01T00:00:00.0000000+00:00', '2026-01-01T00:00:00.0000000+00:00', 'UTC');"));
        Execute(connection, "INSERT INTO sources (source_id, adapter_kind, name, source_timezone, created_at_utc) VALUES ('source', 'CodexCli', 'Codex', 'UTC', '2026-01-01T00:00:00.0000000+00:00');");
        Execute(connection, "INSERT INTO sub_events (sub_event_id, source_id, event_type, occurred_at_utc, source_timezone, payload, event_fingerprint) VALUES ('event-1', 'source', 'turn.completed', '2026-01-01T00:00:00.0000000+00:00', 'UTC', '{}', 'fingerprint');");

        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO sub_events (sub_event_id, source_id, event_type, occurred_at_utc, source_timezone, payload, event_fingerprint) VALUES ('event-2', 'source', 'turn.completed', '2026-01-01T00:00:00.0000000+00:00', 'UTC', '{}', 'fingerprint');"));
        Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_sub_events_source_occurred';"));
        Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_price_versions_provider_model_mode_threshold_interval';"));
    }

    [Fact]
    public void FtsSupportsInsertUpdateDeleteAndSearchesAllRequiredFields()
    {
        using var connection = OpenConnection();
        var document = new SearchDocument(
            "item-1",
            "source-1",
            "session-1",
            "turn-1",
            "promptneedle",
            "response needle",
            "tool needle",
            "subagent needle",
            "workflow needle",
            "model needle",
            "tag:needle");

        FtsIndexingService.Upsert(connection, document);

        foreach (var query in new[] { "promptneedle", "response", "tool", "subagent", "workflow", "model", "needle" })
        {
            Assert.Single(FtsIndexingService.Search(connection, query));
        }

        FtsIndexingService.Upsert(connection, document with { Prompt = "replacement" });
        Assert.Empty(FtsIndexingService.Search(connection, "promptneedle"));
        Assert.Single(FtsIndexingService.Search(connection, "replacement"));

        FtsIndexingService.Delete(connection, "item-1");
        Assert.Empty(FtsIndexingService.Search(connection, "replacement"));
    }

    [Fact]
    public void FtsRebuildsFromCanonicalSubEvents()
    {
        using var connection = OpenConnection();
        Execute(connection, "INSERT INTO sources (source_id, adapter_kind, name, source_timezone, created_at_utc) VALUES ('source', 'CodexCli', 'Codex', 'UTC', '2026-01-01T00:00:00.0000000+00:00');");
        Execute(connection, "INSERT INTO sub_events (sub_event_id, source_id, event_type, occurred_at_utc, source_timezone, payload, prompt, response, model, event_fingerprint) VALUES ('event', 'source', 'turn.completed', '2026-01-01T00:00:00.0000000+00:00', 'UTC', '{}', 'rebuild prompt', 'rebuild response', 'rebuild model', 'event-fingerprint');");
        Execute(connection, "INSERT INTO tags (tag_id, tag_key, tag_value, created_at_utc) VALUES ('tag', 'kind', 'rebuild', '2026-01-01T00:00:00.0000000+00:00');");
        Execute(connection, "INSERT INTO source_tags (source_id, tag_id) VALUES ('source', 'tag');");

        FtsIndexingService.Rebuild(connection);

        Assert.Single(FtsIndexingService.Search(connection, "rebuild"));
        Assert.Single(FtsIndexingService.Search(connection, "kind"));
    }

    [Fact]
    public void FourAdaptersExposeThreePlatformCandidatesAndCapabilities()
    {
        var options = new SourceDiscoveryOptions(
            HostPlatform.Windows,
            @"C:\SyntheticHome",
            @"C:\SyntheticAppData",
            [@"C:\Custom\source"]);
        var adapters = new ILogSourceAdapter[]
        {
            new ClaudeCodeAppAdapter(),
            new ClaudeCodeCliAdapter(),
            new CodexAppAdapter(),
            new CodexCliAdapter()
        };

        foreach (var adapter in adapters)
        {
            var paths = adapter.DiscoverPaths(options);
            Assert.Contains(paths, candidate => candidate.Path == @"C:\Custom\source" && !candidate.IsDefault);
            Assert.NotEmpty(paths);
            Assert.Equal(AdapterCapabilityStatus.Available, adapter.GetCapabilities().Status);
            Assert.Contains("json", adapter.GetCapabilities().Formats);
            Assert.Contains("jsonl", adapter.GetCapabilities().Formats);
            Assert.Contains("csv", adapter.GetCapabilities().Formats);
        }

        var claudeAppPaths = new ClaudeCodeAppAdapter().DiscoverPaths(options).Select(candidate => candidate.Path).ToArray();
        Assert.Contains(@"C:\SyntheticHome\.claude\projects", claudeAppPaths);
        Assert.Contains(@"C:\SyntheticHome\.claude\sessions", claudeAppPaths);
        Assert.Contains(@"C:\SyntheticAppData\Claude", claudeAppPaths);
        var codexPaths = new CodexCliAdapter().DiscoverPaths(options).Select(candidate => candidate.Path).ToArray();
        Assert.Contains(@"C:\SyntheticHome\.codex\sessions", codexPaths);
        Assert.Contains(@"C:\SyntheticHome\.codex\archived_sessions", codexPaths);
    }

    [Fact]
    public void JsonJsonlAndCsvUseTheSameNormalizationPipeline()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var claude = new ClaudeCodeAppAdapter().Parse(Fixture("claude-code-synthetic.json"), cancellationToken);
        var codexJsonl = new CodexCliAdapter().Parse(Fixture("codex-synthetic.jsonl"), cancellationToken);
        var codexCsv = new CodexAppAdapter().Parse(Fixture("codex-synthetic.csv"), cancellationToken);

        Assert.Single(claude.Events);
        Assert.Empty(claude.Errors);
        Assert.Equal("claude-synthetic", claude.Events[0].Model);
        Assert.Equal(120, claude.Events[0].TokenCounts[TokenType.Input]);
        Assert.Equal("Asia/Taipei", claude.Events[0].SourceTimeZone);

        Assert.Equal(2, codexJsonl.Events.Count);
        Assert.Single(codexJsonl.Errors);
        Assert.Equal(AdapterCapabilityStatus.ParseFallback, codexJsonl.Status);
        Assert.Equal("codex-synthetic", codexJsonl.Events[0].Model);
        Assert.Single(codexCsv.Events);
        Assert.Empty(codexCsv.Errors);
        Assert.Equal("cost-review", codexCsv.Events[0].Workflow);
    }

    [Fact]
    public void UnknownExtensionUsesTolerantFallback()
    {
        var path = Path.Combine(Path.GetTempPath(), $"token-dashboard-fallback-{Guid.NewGuid():N}.log");
        File.WriteAllText(path, "{\"source_id\":\"fallback\",\"occurred_at_utc\":\"2026-01-01T00:00:00Z\",\"future\":true}");
        try
        {
            var result = new CodexCliAdapter().Parse(path, TestContext.Current.CancellationToken);

            Assert.Single(result.Events);
            Assert.Equal(AdapterCapabilityStatus.ParseFallback, result.Status);
            Assert.Empty(result.Errors);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ImportIsIdempotentReportsPartialCorruptionAndIndexesConversation()
    {
        using var connection = OpenConnection();
        var service = new ImportService(connection);
        var adapter = new CodexCliAdapter();

        var first = service.Import("import-jsonl", Fixture("codex-synthetic.jsonl"), adapter);
        var second = service.Import("import-jsonl", Fixture("codex-synthetic.jsonl"), adapter);

        Assert.Equal(2, first.ImportedEventCount);
        Assert.Single(first.Errors);
        Assert.Equal(2, second.DuplicateEventCount);
        Assert.Equal(2L, Scalar<long>(connection, "SELECT COUNT(*) FROM sub_events WHERE source_id = 'codex-cli-public';"));
        Assert.NotEmpty(FtsIndexingService.Search(connection, "migration"));
        Assert.NotEmpty(FtsIndexingService.Search(connection, "dotnet-test"));
        Assert.Equal(3L, Scalar<long>(connection, "SELECT COUNT(*) FROM token_usages;"));
        Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM imports;"));
    }

    [Fact]
    public void HistoricalPricingUsesHalfOpenIntervalsAndPreservesUnknownPrice()
    {
        using var connection = OpenConnection();
        var pricing = new HistoricalPricingService(connection);
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        pricing.Add(PriceVersion.PerMillionTokens("provider-a", "model-a", TokenType.Input, 2m, start, start.AddDays(1)));
        pricing.Add(PriceVersion.PerMillionTokens("provider-a", "model-a", TokenType.Input, 3m, start.AddDays(1)));

        var previous = pricing.Calculate("provider-a", "model-a", TokenType.Input, 1_000_000, 0, start.AddHours(12));
        var current = pricing.Calculate("provider-a", "model-a", TokenType.Input, 1_000_000, 0, start.AddDays(1));
        var unknown = pricing.Calculate("provider-a", "model-missing", TokenType.Input, 1, 0, start);

        Assert.True(previous.IsPriced);
        Assert.Equal(2m, previous.Usd);
        Assert.Equal(3m, current.Usd);
        Assert.False(unknown.IsPriced);
        Assert.Null(unknown.Usd);
    }

    [Fact]
    public void HistoricalPricingMatchesProviderModeTimeAndInputThreshold()
    {
        using var connection = OpenConnection();
        var pricing = new HistoricalPricingService(connection);
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        pricing.Add(PriceVersion.PerMillionTokens("provider-a", "model-a", "standard", TokenType.Input, 2m, 0, 1_000_001, start));
        pricing.Add(PriceVersion.PerMillionTokens("provider-a", "model-a", "fast", TokenType.Input, 4m, 0, null, start));
        pricing.Add(PriceVersion.PerMillionTokens("provider-a", "model-a", "long-context-1m", TokenType.Input, 3m, 1_000_001, null, start));
        pricing.Add(PriceVersion.PerMillionTokens("provider-a", "model-a", "standard", TokenType.Input, 5m, 0, null, start.AddDays(1)));

        var standard = pricing.Calculate("provider-a", "model-a", "standard", TokenType.Input, 1, 1_000_000, start);
        var longContext = pricing.Calculate("provider-a", "model-a", "long-context-1m", TokenType.Input, 1, 1_000_001, start);
        var fast = pricing.Calculate("provider-a", "model-a", "fast", TokenType.Input, 1, 1_000_001, start);
        var latest = pricing.Calculate("provider-a", "model-a", "standard", TokenType.Input, 1, 1_000_000, start.AddDays(1));
        var otherProvider = pricing.Calculate("provider-b", "model-a", "standard", TokenType.Input, 1, 1_000_000, start);
        var otherMode = pricing.Calculate("provider-a", "model-a", "unknown-mode", TokenType.Input, 1, 1_000_000, start);

        Assert.Equal(2m / 1_000_000m, standard.Usd);
        Assert.Equal(3m / 1_000_000m, longContext.Usd);
        Assert.Equal(4m / 1_000_000m, fast.Usd);
        Assert.Equal(5m / 1_000_000m, latest.Usd);
        Assert.False(otherProvider.IsPriced);
        Assert.False(otherMode.IsPriced);
    }

    [Fact]
    public void SchemaMigratesVersionOnePriceTableToVersionTwoAndRemainsReentrant()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection, """
            CREATE TABLE schema_versions (version INTEGER PRIMARY KEY, applied_at_utc TEXT NOT NULL);
            INSERT INTO schema_versions (version, applied_at_utc) VALUES (1, '2026-01-01T00:00:00.0000000+00:00');
            CREATE TABLE price_versions
            (
                price_version_id TEXT PRIMARY KEY,
                model TEXT NOT NULL,
                token_type TEXT NOT NULL,
                currency TEXT NOT NULL,
                usd_per_token TEXT NOT NULL,
                effective_from_utc TEXT NOT NULL,
                effective_to_utc TEXT
            );
            CREATE INDEX ix_price_versions_model_interval ON price_versions (model, token_type, effective_from_utc, effective_to_utc);
            """);

        SchemaMigrator.Migrate(connection);
        SchemaMigrator.Migrate(connection);

        Assert.Equal(SchemaMigrator.CurrentVersion, Scalar<long>(connection, "SELECT MAX(version) FROM schema_versions;"));
        Assert.Equal(4L, Scalar<long>(connection, "SELECT COUNT(*) FROM schema_versions;"));
        Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM pragma_table_info('price_versions') WHERE name = 'provider';"));
        Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM pragma_table_info('price_versions') WHERE name = 'mode';"));
        Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM pragma_table_info('price_versions') WHERE name = 'minimum_input_tokens';"));
        Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM pragma_table_info('price_versions') WHERE name = 'maximum_input_tokens';"));
        Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM pragma_table_info('price_versions') WHERE name = 'source_name';"));
        Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM pragma_table_info('price_versions') WHERE name = 'source_url';"));
        Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_price_versions_provider_model_mode_threshold_interval';"));
        Assert.Equal(0L, Scalar<long>(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_price_versions_model_interval';"));
        Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'project_tags';"));
    }

    private static SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        SchemaMigrator.Migrate(connection);
        return connection;
    }

    private static T Scalar<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "public", name);
}

using Microsoft.Data.Sqlite;

namespace TokenDashboard.Data;

public sealed class SchemaMigrator
{
    public const int CurrentVersion = 7;

    public static void Migrate(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
        }

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();

        using var transaction = connection.BeginTransaction();
        Execute(transaction, """
            CREATE TABLE IF NOT EXISTS schema_versions
            (
                version INTEGER PRIMARY KEY,
                applied_at_utc TEXT NOT NULL
            );
            """);

        var version = ReadVersion(transaction);
        if (version == 0)
        {
            ApplyLatestSchema(transaction);
            ApplyVersionSeven(transaction);
            Execute(transaction, "INSERT INTO schema_versions (version, applied_at_utc) VALUES (7, $appliedAtUtc);", ("$appliedAtUtc", UtcNow()));
        }
        else
        {
            if (version < 2)
            {
                ApplyVersionTwo(transaction);
                Execute(transaction, "INSERT INTO schema_versions (version, applied_at_utc) VALUES (2, $appliedAtUtc);", ("$appliedAtUtc", UtcNow()));
                version = 2;
            }

            if (version < 3)
            {
                ApplyVersionThree(transaction);
                Execute(transaction, "INSERT INTO schema_versions (version, applied_at_utc) VALUES (3, $appliedAtUtc);", ("$appliedAtUtc", UtcNow()));
                version = 3;
            }

            if (version < 4)
            {
                ApplyVersionFour(transaction);
                Execute(transaction, "INSERT INTO schema_versions (version, applied_at_utc) VALUES (4, $appliedAtUtc);", ("$appliedAtUtc", UtcNow()));
            }

            if (version < 5)
            {
                ApplyVersionFive(transaction);
                Execute(transaction, "INSERT INTO schema_versions (version, applied_at_utc) VALUES (5, $appliedAtUtc);", ("$appliedAtUtc", UtcNow()));
                version = 5;
            }

            if (version < 6)
            {
                ApplyVersionSix(transaction);
                Execute(transaction, "INSERT INTO schema_versions (version, applied_at_utc) VALUES (6, $appliedAtUtc);", ("$appliedAtUtc", UtcNow()));
                version = 6;
            }

            if (version < 7)
            {
                ApplyVersionSeven(transaction);
                Execute(transaction, "UPDATE schema_versions SET version = 7, applied_at_utc = $appliedAtUtc WHERE version = 6;", ("$appliedAtUtc", UtcNow()));
                Execute(transaction, "INSERT INTO schema_versions (version, applied_at_utc) SELECT 7, $appliedAtUtc WHERE NOT EXISTS (SELECT 1 FROM schema_versions WHERE version = 7);", ("$appliedAtUtc", UtcNow()));
            }
        }

        transaction.Commit();
    }

    private static void ApplyLatestSchema(SqliteTransaction transaction)
    {
        Execute(transaction, """
            CREATE TABLE IF NOT EXISTS sources
            (
                source_id TEXT PRIMARY KEY,
                adapter_kind TEXT NOT NULL,
                name TEXT NOT NULL,
                source_path TEXT,
                source_timezone TEXT NOT NULL,
                created_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS sessions
            (
                session_id TEXT PRIMARY KEY,
                source_id TEXT NOT NULL,
                started_at_utc TEXT NOT NULL,
                last_activity_at_utc TEXT NOT NULL,
                source_timezone TEXT NOT NULL,
                workspace_id TEXT,
                owner_id TEXT,
                FOREIGN KEY (source_id) REFERENCES sources (source_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS turns
            (
                turn_id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                sequence INTEGER NOT NULL,
                occurred_at_utc TEXT NOT NULL,
                source_timezone TEXT NOT NULL,
                effort TEXT,
                FOREIGN KEY (session_id) REFERENCES sessions (session_id) ON DELETE CASCADE,
                UNIQUE (session_id, sequence)
            );

            CREATE TABLE IF NOT EXISTS contents
            (
                content_id TEXT PRIMARY KEY,
                turn_id TEXT NOT NULL,
                role TEXT NOT NULL,
                body TEXT NOT NULL,
                occurred_at_utc TEXT NOT NULL,
                source_timezone TEXT NOT NULL,
                FOREIGN KEY (turn_id) REFERENCES turns (turn_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS sub_events
            (
                sub_event_id TEXT PRIMARY KEY,
                source_id TEXT NOT NULL,
                session_id TEXT,
                turn_id TEXT,
                event_type TEXT NOT NULL,
                occurred_at_utc TEXT NOT NULL,
                source_timezone TEXT NOT NULL,
                payload TEXT NOT NULL,
                prompt TEXT NOT NULL DEFAULT '',
                response TEXT NOT NULL DEFAULT '',
                model TEXT NOT NULL DEFAULT '',
                tool TEXT NOT NULL DEFAULT '',
                subagent TEXT NOT NULL DEFAULT '',
                workflow TEXT NOT NULL DEFAULT '',
                cache_metrics_reported INTEGER NOT NULL DEFAULT 0 CHECK (cache_metrics_reported IN (0, 1)),
                event_fingerprint TEXT NOT NULL UNIQUE,
                FOREIGN KEY (source_id) REFERENCES sources (source_id) ON DELETE CASCADE,
                FOREIGN KEY (session_id) REFERENCES sessions (session_id) ON DELETE CASCADE,
                FOREIGN KEY (turn_id) REFERENCES turns (turn_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS token_usages
            (
                token_usage_id TEXT PRIMARY KEY,
                turn_id TEXT NOT NULL,
                token_type TEXT NOT NULL,
                token_count INTEGER NOT NULL CHECK (token_count >= 0),
                FOREIGN KEY (turn_id) REFERENCES turns (turn_id) ON DELETE CASCADE,
                UNIQUE (turn_id, token_type, token_usage_id)
            );

            CREATE TABLE IF NOT EXISTS tags
            (
                tag_id TEXT PRIMARY KEY,
                tag_key TEXT NOT NULL,
                tag_value TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                UNIQUE (tag_key, tag_value)
            );

            CREATE TABLE IF NOT EXISTS source_tags
            (
                source_id TEXT NOT NULL,
                tag_id TEXT NOT NULL,
                PRIMARY KEY (source_id, tag_id),
                FOREIGN KEY (source_id) REFERENCES sources (source_id) ON DELETE CASCADE,
                FOREIGN KEY (tag_id) REFERENCES tags (tag_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS session_tags
            (
                session_id TEXT NOT NULL,
                tag_id TEXT NOT NULL,
                PRIMARY KEY (session_id, tag_id),
                FOREIGN KEY (session_id) REFERENCES sessions (session_id) ON DELETE CASCADE,
                FOREIGN KEY (tag_id) REFERENCES tags (tag_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS project_tags
            (
                project_id TEXT NOT NULL,
                tag_id TEXT NOT NULL,
                PRIMARY KEY (project_id, tag_id),
                FOREIGN KEY (tag_id) REFERENCES tags (tag_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS price_versions
            (
                price_version_id TEXT PRIMARY KEY,
                provider TEXT NOT NULL,
                model TEXT NOT NULL,
                mode TEXT NOT NULL DEFAULT 'standard',
                token_type TEXT NOT NULL,
                minimum_input_tokens INTEGER NOT NULL DEFAULT 0 CHECK (minimum_input_tokens >= 0),
                maximum_input_tokens INTEGER,
                currency TEXT NOT NULL CHECK (currency = 'USD'),
                usd_per_token TEXT NOT NULL,
                effective_from_utc TEXT NOT NULL,
                effective_to_utc TEXT,
                source_name TEXT NOT NULL DEFAULT '',
                source_url TEXT NOT NULL DEFAULT '',
                override_version INTEGER NOT NULL DEFAULT 1,
                created_at_utc TEXT NOT NULL DEFAULT '',
                catalog_version TEXT NOT NULL DEFAULT '',
                source_kind TEXT NOT NULL DEFAULT 'local-override',
                CHECK (maximum_input_tokens IS NULL OR maximum_input_tokens > minimum_input_tokens),
                CHECK (effective_to_utc IS NULL OR effective_to_utc > effective_from_utc)
            );

            CREATE TABLE IF NOT EXISTS imports
            (
                import_id TEXT PRIMARY KEY,
                source_id TEXT NOT NULL,
                imported_at_utc TEXT NOT NULL,
                source_timezone TEXT NOT NULL,
                scan_fingerprint TEXT NOT NULL UNIQUE,
                status TEXT NOT NULL,
                valid_event_count INTEGER NOT NULL DEFAULT 0,
                error_count INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY (source_id) REFERENCES sources (source_id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS ix_sessions_source_started
                ON sessions (source_id, started_at_utc);
            CREATE INDEX IF NOT EXISTS ix_sessions_last_activity
                ON sessions (last_activity_at_utc);
            CREATE INDEX IF NOT EXISTS ix_turns_session_occurred
                ON turns (session_id, occurred_at_utc);
            CREATE INDEX IF NOT EXISTS ix_sub_events_source_occurred
                ON sub_events (source_id, occurred_at_utc);
            CREATE INDEX IF NOT EXISTS ix_sub_events_session_turn
                ON sub_events (session_id, turn_id);
            CREATE INDEX IF NOT EXISTS ix_sub_events_model_occurred
                ON sub_events (model, occurred_at_utc);
            CREATE INDEX IF NOT EXISTS ix_price_versions_provider_model_mode_threshold_interval
                ON price_versions
                    (provider, model, mode, token_type, minimum_input_tokens, maximum_input_tokens,
                     effective_from_utc, effective_to_utc);
            CREATE INDEX IF NOT EXISTS ix_imports_source_imported
                ON imports (source_id, imported_at_utc);

            CREATE VIRTUAL TABLE IF NOT EXISTS search_index USING fts5
            (
                item_id UNINDEXED,
                source_id UNINDEXED,
                session_id UNINDEXED,
                turn_id UNINDEXED,
                prompt,
                response,
                tool,
                subagent,
                workflow,
                model,
                tags,
                tokenize = 'unicode61'
            );
            """);
    }

    private static void ApplyVersionTwo(SqliteTransaction transaction)
    {
        AddColumnIfMissing(transaction, "provider", "TEXT NOT NULL DEFAULT 'unknown'");
        AddColumnIfMissing(transaction, "mode", "TEXT NOT NULL DEFAULT 'standard'");
        AddColumnIfMissing(transaction, "minimum_input_tokens", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(transaction, "maximum_input_tokens", "INTEGER");
        Execute(transaction, "DROP INDEX IF EXISTS ix_price_versions_model_interval;");
        Execute(transaction, """
            CREATE INDEX IF NOT EXISTS ix_price_versions_provider_model_mode_threshold_interval
                ON price_versions
                    (provider, model, mode, token_type, minimum_input_tokens, maximum_input_tokens,
                     effective_from_utc, effective_to_utc);
            """);
    }

    private static void ApplyVersionThree(SqliteTransaction transaction)
    {
        Execute(transaction, """
            CREATE TABLE IF NOT EXISTS project_tags
            (
                project_id TEXT NOT NULL,
                tag_id TEXT NOT NULL,
                PRIMARY KEY (project_id, tag_id),
                FOREIGN KEY (tag_id) REFERENCES tags (tag_id) ON DELETE CASCADE
            );
            """);
    }

    private static void ApplyVersionFour(SqliteTransaction transaction)
    {
        AddColumnIfMissing(transaction, "source_name", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(transaction, "source_url", "TEXT NOT NULL DEFAULT ''");
    }

    private static void ApplyVersionFive(SqliteTransaction transaction)
    {
        if (TableExists(transaction, "sub_events"))
        {
            AddColumnIfMissing(transaction, "sub_events", "cache_metrics_reported", "INTEGER NOT NULL DEFAULT 0 CHECK (cache_metrics_reported IN (0, 1))");
            Execute(transaction, "UPDATE sub_events SET cache_metrics_reported = CASE WHEN lower(payload) LIKE '%cache%' THEN 1 ELSE 0 END WHERE cache_metrics_reported = 0;");
        }
        AddColumnIfMissing(transaction, "price_versions", "override_version", "INTEGER NOT NULL DEFAULT 1");
        AddColumnIfMissing(transaction, "price_versions", "created_at_utc", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(transaction, "price_versions", "catalog_version", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(transaction, "price_versions", "source_kind", "TEXT NOT NULL DEFAULT 'local-override'");
        Execute(transaction, "UPDATE price_versions SET override_version = 1 WHERE override_version IS NULL OR override_version < 1;");
        Execute(transaction, "UPDATE price_versions SET created_at_utc = effective_from_utc WHERE created_at_utc = '';");
    }

    private static void ApplyVersionSix(SqliteTransaction transaction)
    {
        if (TableExists(transaction, "turns"))
        {
            AddColumnIfMissing(transaction, "turns", "effort", "TEXT");
        }
    }

    private static void ApplyVersionSeven(SqliteTransaction transaction)
    {
        Execute(transaction, """
            CREATE TABLE IF NOT EXISTS budgets
            (
                budget_id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                amount_usd REAL NOT NULL,
                period TEXT NOT NULL CHECK (period IN ('daily', 'monthly', 'custom')),
                from_date TEXT NOT NULL,
                to_date TEXT,
                project_id TEXT,
                tag TEXT,
                enabled INTEGER NOT NULL DEFAULT 1 CHECK (enabled IN (0, 1)),
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                CHECK (amount_usd >= 0),
                CHECK (to_date IS NULL OR to_date >= from_date)
            );
            CREATE INDEX IF NOT EXISTS ix_budgets_period_dates ON budgets (enabled, from_date, to_date);
            """);
    }

    private static void AddColumnIfMissing(SqliteTransaction transaction, string columnName, string definition)
        => AddColumnIfMissing(transaction, "price_versions", columnName, definition);

    private static void AddColumnIfMissing(SqliteTransaction transaction, string tableName, string columnName, string definition)
    {
        using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({tableName});";
        var exists = false;
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (exists)
        {
            return;
        }

        Execute(transaction, $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition};");
    }

    private static bool TableExists(SqliteTransaction transaction, string tableName)
    {
        using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $tableName LIMIT 1;";
        command.Parameters.AddWithValue("$tableName", tableName);
        return command.ExecuteScalar() is not null;
    }

    private static int ReadVersion(SqliteTransaction transaction)
    {
        using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_versions;";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Execute(SqliteTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        command.ExecuteNonQuery();
    }

    private static string UtcNow() => DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
}

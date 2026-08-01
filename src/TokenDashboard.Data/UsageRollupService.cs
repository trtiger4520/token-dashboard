using Microsoft.Data.Sqlite;

namespace TokenDashboard.Data;

public static class UsageRollupService
{
    public static void Rebuild(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
        }

        using var transaction = connection.BeginTransaction();
        Execute(transaction, "DELETE FROM turn_usage_tokens; DELETE FROM turn_usage_facts; DELETE FROM session_usage_rollups; DELETE FROM daily_usage_rollups;");
        cancellationToken.ThrowIfCancellationRequested();

        Execute(transaction, """
            INSERT INTO turn_usage_tokens (turn_id, token_type, token_count)
            SELECT turn_id, token_type, SUM(token_count)
            FROM token_usages
            GROUP BY turn_id, token_type;
            """);

        Execute(transaction, """
            WITH ranked_events AS
            (
                SELECT
                    se.turn_id,
                    se.event_fingerprint,
                    se.model,
                    (
                        SELECT tool
                        FROM sub_events AS tool_event
                        WHERE tool_event.turn_id = se.turn_id
                          AND trim(tool_event.tool) <> ''
                        ORDER BY tool_event.occurred_at_utc, tool_event.event_fingerprint
                        LIMIT 1
                    ) AS first_tool,
                    ROW_NUMBER() OVER
                    (
                        PARTITION BY se.turn_id
                        ORDER BY CASE WHEN trim(se.model) <> '' THEN 0 ELSE 1 END,
                                 se.occurred_at_utc,
                                 se.event_fingerprint
                    ) AS row_number
                FROM sub_events AS se
                WHERE se.turn_id IS NOT NULL
            ),
            event_counts AS
            (
                SELECT turn_id, COUNT(*) AS event_count
                FROM sub_events
                WHERE turn_id IS NOT NULL
                GROUP BY turn_id
            ),
            token_counts AS
            (
                SELECT turn_id, SUM(token_count) AS total_tokens
                FROM token_usages
                GROUP BY turn_id
            )
            INSERT INTO turn_usage_facts
            (
                turn_id, session_id, occurred_at_utc, source_id, model, tool,
                event_count, accounting_event_fingerprint, total_tokens,
                cost_coverage_tokens, refreshed_at_utc
            )
            SELECT
                t.turn_id,
                t.session_id,
                t.occurred_at_utc,
                s.source_id,
                COALESCE(r.model, ''),
                COALESCE(r.first_tool, ''),
                COALESCE(ec.event_count, 0),
                r.event_fingerprint,
                COALESCE(tc.total_tokens, 0),
                COALESCE(tc.total_tokens, 0),
                $now
            FROM turns AS t
            INNER JOIN sessions AS s ON s.session_id = t.session_id
            LEFT JOIN ranked_events AS r ON r.turn_id = t.turn_id AND r.row_number = 1
            LEFT JOIN event_counts AS ec ON ec.turn_id = t.turn_id
            LEFT JOIN token_counts AS tc ON tc.turn_id = t.turn_id;
            """, ("$now", UtcNow()));
        cancellationToken.ThrowIfCancellationRequested();

        Execute(transaction, """
            INSERT INTO session_usage_rollups
            (
                session_id, source_id, started_at_utc, last_activity_at_utc,
                event_count, turn_count, total_tokens, priced_tokens,
                unpriced_tokens, refreshed_at_utc
            )
            SELECT
                s.session_id,
                s.source_id,
                s.started_at_utc,
                s.last_activity_at_utc,
                COALESCE(events.event_count, 0),
                COALESCE(turns.turn_count, 0),
                COALESCE(tokens.total_tokens, 0),
                0,
                COALESCE(tokens.total_tokens, 0),
                $now
            FROM sessions AS s
            LEFT JOIN
            (
                SELECT session_id, COUNT(*) AS event_count
                FROM sub_events
                WHERE session_id IS NOT NULL
                GROUP BY session_id
            ) AS events ON events.session_id = s.session_id
            LEFT JOIN
            (
                SELECT session_id, COUNT(*) AS turn_count
                FROM turns
                GROUP BY session_id
            ) AS turns ON turns.session_id = s.session_id
            LEFT JOIN
            (
                SELECT session_id, SUM(total_tokens) AS total_tokens
                FROM turn_usage_facts
                GROUP BY session_id
            ) AS tokens ON tokens.session_id = s.session_id;
            """, ("$now", UtcNow()));
        cancellationToken.ThrowIfCancellationRequested();

        Execute(transaction, """
            INSERT INTO daily_usage_rollups
            (
                bucket_date, source_id, model, tool, event_count,
                turn_count, total_tokens, refreshed_at_utc
            )
            SELECT
                substr(facts.occurred_at_utc, 1, 10),
                facts.source_id,
                COALESCE(facts.model, ''),
                COALESCE(facts.tool, ''),
                COALESCE(SUM(facts.event_count), 0),
                COUNT(*),
                COALESCE(SUM(facts.total_tokens), 0),
                $now
            FROM turn_usage_facts AS facts
            GROUP BY substr(facts.occurred_at_utc, 1, 10), facts.source_id, facts.model, facts.tool;
            """, ("$now", UtcNow()));

        transaction.Commit();
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

using Microsoft.Data.Sqlite;

namespace TokenDashboard.Data;

public sealed record SearchDocument(
    string ItemId,
    string SourceId,
    string? SessionId,
    string? TurnId,
    string Prompt,
    string Response,
    string Tool,
    string Subagent,
    string Workflow,
    string Model,
    string Tags);

public sealed record SearchResult(
    string ItemId,
    string SourceId,
    string? SessionId,
    string? TurnId,
    double Rank);

public sealed class FtsIndexingService
{
    public static void Upsert(SqliteConnection connection, SearchDocument document, SqliteTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(document);
        Delete(connection, document.ItemId, transaction);
        Execute(
            connection,
            transaction,
            """
            INSERT INTO search_index
                (item_id, source_id, session_id, turn_id, prompt, response, tool, subagent, workflow, model, tags)
            VALUES
                ($itemId, $sourceId, $sessionId, $turnId, $prompt, $response, $tool, $subagent, $workflow, $model, $tags);
            """,
            ("$itemId", document.ItemId),
            ("$sourceId", document.SourceId),
            ("$sessionId", (object?)document.SessionId ?? DBNull.Value),
            ("$turnId", (object?)document.TurnId ?? DBNull.Value),
            ("$prompt", document.Prompt),
            ("$response", document.Response),
            ("$tool", document.Tool),
            ("$subagent", document.Subagent),
            ("$workflow", document.Workflow),
            ("$model", document.Model),
            ("$tags", document.Tags));
    }

    public static void Delete(SqliteConnection connection, string itemId, SqliteTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var requiredItemId = Required(itemId, nameof(itemId));
        using var find = connection.CreateCommand();
        find.Transaction = transaction;
        find.CommandText = "SELECT rowid FROM search_index WHERE item_id = $itemId;";
        find.Parameters.AddWithValue("$itemId", requiredItemId);
        var rowIds = new List<long>();
        using (var reader = find.ExecuteReader())
        {
            while (reader.Read())
            {
                rowIds.Add(reader.GetInt64(0));
            }
        }

        foreach (var rowId in rowIds)
        {
            Execute(connection, transaction, "DELETE FROM search_index WHERE rowid = $rowId;", ("$rowId", rowId));
        }
    }

    public static void Rebuild(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Execute(connection, null, "DELETE FROM search_index;");
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                se.event_fingerprint,
                se.source_id,
                se.session_id,
                se.turn_id,
                se.prompt,
                se.response,
                se.tool,
                se.subagent,
                se.workflow,
                se.model,
                TRIM(
                    COALESCE((
                        SELECT GROUP_CONCAT(source_tag.tag_key || ':' || source_tag.tag_value, ' ')
                        FROM source_tags AS source_link
                        INNER JOIN tags AS source_tag ON source_tag.tag_id = source_link.tag_id
                        WHERE source_link.source_id = se.source_id
                    ), '') || ' ' ||
                    COALESCE((
                        SELECT GROUP_CONCAT(session_tag.tag_key || ':' || session_tag.tag_value, ' ')
                        FROM session_tags AS session_link
                        INNER JOIN tags AS session_tag ON session_tag.tag_id = session_link.tag_id
                        WHERE session_link.session_id = se.session_id
                    ), '')
                )
            FROM sub_events AS se
            GROUP BY
                se.event_fingerprint,
                se.source_id,
                se.session_id,
                se.turn_id,
                se.prompt,
                se.response,
                se.tool,
                se.subagent,
                se.workflow,
                se.model;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            Upsert(connection, new SearchDocument(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10)));
        }
    }

    public static IReadOnlyList<SearchResult> Search(SqliteConnection connection, string query, int limit = 50)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var requiredQuery = Required(query, nameof(query));
        if (limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "Limit must be between 1 and 500");
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT item_id, source_id, session_id, turn_id, bm25(search_index)
            FROM search_index
            WHERE search_index MATCH $query
            ORDER BY bm25(search_index)
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$query", $"\"{requiredQuery.Replace("\"", "\"\"", StringComparison.Ordinal)}\"");
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var results = new List<SearchResult>();
        while (reader.Read())
        {
            results.Add(new SearchResult(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetDouble(4)));
        }

        return results;
    }

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        command.ExecuteNonQuery();
    }

    private static string Required(string value, string parameterName)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value is required", parameterName)
            : value.Trim();
    }
}

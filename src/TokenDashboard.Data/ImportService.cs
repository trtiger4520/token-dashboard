using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TokenDashboard.Core;

namespace TokenDashboard.Data;

public sealed record ImportSummary(
    string ImportId,
    int ParsedEventCount,
    int ImportedEventCount,
    int DuplicateEventCount,
    IReadOnlyList<ParseError> Errors,
    AdapterCapabilityStatus Status);

public sealed class ImportService
{
    private readonly SqliteConnection connection;

    public ImportService(SqliteConnection connection)
    {
        this.connection = connection ?? throw new ArgumentNullException(nameof(connection));
        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
        }

        SchemaMigrator.Migrate(connection);
    }

    public ImportSummary Import(
        string importId,
        string path,
        ILogSourceAdapter adapter,
        string? sourcePath = null,
        string? workspaceId = null,
        string? ownerId = null)
    {
        var requiredImportId = Required(importId, nameof(importId));
        ArgumentNullException.ThrowIfNull(adapter);
        var parse = adapter.Parse(path);
        var sourceId = parse.Events.Count > 0 ? parse.Events[0].SourceId : adapter.Kind.ToString();
        var scanFingerprint = BuildScanFingerprint(sourceId, parse.Events, path);
        var imported = 0;
        var duplicates = 0;

        using var transaction = connection.BeginTransaction();
        if (parse.Events.Count == 0)
        {
            Execute(transaction, """
                INSERT OR IGNORE INTO sources
                    (source_id, adapter_kind, name, source_path, source_timezone, created_at_utc)
                VALUES
                    ($sourceId, $adapterKind, $name, $sourcePath, 'UTC', $createdAtUtc);
                """,
                ("$sourceId", sourceId),
                ("$adapterKind", adapter.Kind.ToString()),
                ("$name", adapter.Kind.ToString()),
                ("$sourcePath", path),
                ("$createdAtUtc", Utc(DateTimeOffset.UtcNow)));
        }

        foreach (var item in parse.Events)
        {
            UpsertSource(transaction, item, sourcePath ?? path);
            if (ExistsEvent(transaction, item.EventFingerprint.Value))
            {
                duplicates++;
                continue;
            }

            if (item.SessionId is not null)
            {
                UpsertSession(transaction, item, workspaceId, ownerId);
            }

            if (item.SessionId is not null && item.TurnId is not null)
            {
                UpsertTurn(transaction, item);
            }

            InsertSubEvent(transaction, item);
            if (item.SessionId is not null && item.TurnId is not null)
            {
                InsertContents(transaction, item);
                InsertTokens(transaction, item);
            }

            InsertTags(transaction, item);
            FtsIndexingService.Upsert(connection, CreateSearchDocument(item), transaction);
            imported++;
        }

        UpsertImport(transaction, requiredImportId, sourceId, scanFingerprint, parse, imported);
        transaction.Commit();

        return new ImportSummary(requiredImportId, parse.Events.Count, imported, duplicates, parse.Errors, parse.Status);
    }

    private static void UpsertSource(SqliteTransaction transaction, NormalizedEvent item, string sourcePath)
    {
        Execute(transaction, """
            INSERT OR IGNORE INTO sources
                (source_id, adapter_kind, name, source_path, source_timezone, created_at_utc)
            VALUES
                ($sourceId, $adapterKind, $name, $sourcePath, $sourceTimezone, $createdAtUtc);
            """,
            ("$sourceId", item.SourceId),
            ("$adapterKind", item.AdapterKind.ToString()),
            ("$name", item.AdapterKind.ToString()),
            ("$sourcePath", sourcePath),
            ("$sourceTimezone", item.SourceTimeZone),
            ("$createdAtUtc", Utc(item.OccurredAtUtc)));
    }

    private static void UpsertSession(SqliteTransaction transaction, NormalizedEvent item, string? workspaceId, string? ownerId)
    {
        Execute(transaction, """
            INSERT OR IGNORE INTO sessions
                (session_id, source_id, started_at_utc, last_activity_at_utc, source_timezone, workspace_id, owner_id)
            VALUES
                ($sessionId, $sourceId, $occurredAtUtc, $occurredAtUtc, $sourceTimezone, $workspaceId, $ownerId);
            UPDATE sessions
            SET last_activity_at_utc = MAX(last_activity_at_utc, $occurredAtUtc)
            WHERE session_id = $sessionId;
            """,
            ("$sessionId", item.SessionId!),
            ("$sourceId", item.SourceId),
            ("$occurredAtUtc", Utc(item.OccurredAtUtc)),
            ("$sourceTimezone", item.SourceTimeZone),
            ("$workspaceId", (object?)workspaceId ?? DBNull.Value),
            ("$ownerId", (object?)ownerId ?? DBNull.Value));
    }

    private static void UpsertTurn(SqliteTransaction transaction, NormalizedEvent item)
    {
        Execute(transaction, """
            INSERT OR IGNORE INTO turns
                (turn_id, session_id, sequence, occurred_at_utc, source_timezone)
            VALUES
                ($turnId, $sessionId, $sequence, $occurredAtUtc, $sourceTimezone);
            """,
            ("$turnId", item.TurnId!),
            ("$sessionId", item.SessionId!),
            ("$sequence", item.Sequence),
            ("$occurredAtUtc", Utc(item.OccurredAtUtc)),
            ("$sourceTimezone", item.SourceTimeZone));

        Execute(transaction, """
            INSERT OR IGNORE INTO turns
                (turn_id, session_id, sequence, occurred_at_utc, source_timezone)
            SELECT
                $turnId,
                $sessionId,
                COALESCE(MAX(sequence) + 1, 0),
                $occurredAtUtc,
                $sourceTimezone
            FROM turns
            WHERE session_id = $sessionId
              AND NOT EXISTS (SELECT 1 FROM turns WHERE turn_id = $turnId);
            """,
            ("$turnId", item.TurnId!),
            ("$sessionId", item.SessionId!),
            ("$occurredAtUtc", Utc(item.OccurredAtUtc)),
            ("$sourceTimezone", item.SourceTimeZone));
    }

    private static void InsertSubEvent(SqliteTransaction transaction, NormalizedEvent item)
    {
        Execute(transaction, """
            INSERT INTO sub_events
                (sub_event_id, source_id, session_id, turn_id, event_type, occurred_at_utc, source_timezone, payload,
                 prompt, response, model, tool, subagent, workflow, event_fingerprint)
            VALUES
                ($subEventId, $sourceId, $sessionId, $turnId, $eventType, $occurredAtUtc, $sourceTimezone, $payload,
                 $prompt, $response, $model, $tool, $subagent, $workflow, $fingerprint);
            """,
            ("$subEventId", item.EventFingerprint.Value),
            ("$sourceId", item.SourceId),
            ("$sessionId", (object?)item.SessionId ?? DBNull.Value),
            ("$turnId", (object?)item.TurnId ?? DBNull.Value),
            ("$eventType", item.EventType),
            ("$occurredAtUtc", Utc(item.OccurredAtUtc)),
            ("$sourceTimezone", item.SourceTimeZone),
            ("$payload", item.Payload),
            ("$prompt", item.Prompt),
            ("$response", item.Response),
            ("$model", item.Model),
            ("$tool", item.Tool),
            ("$subagent", item.Subagent),
            ("$workflow", item.Workflow),
            ("$fingerprint", item.EventFingerprint.Value));
    }

    private static void InsertContents(SqliteTransaction transaction, NormalizedEvent item)
    {
        InsertContent(transaction, item, "prompt", item.Prompt);
        InsertContent(transaction, item, "response", item.Response);
    }

    private static void InsertContent(SqliteTransaction transaction, NormalizedEvent item, string role, string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return;
        }

        Execute(transaction, """
            INSERT OR IGNORE INTO contents
                (content_id, turn_id, role, body, occurred_at_utc, source_timezone)
            VALUES
                ($contentId, $turnId, $role, $body, $occurredAtUtc, $sourceTimezone);
            """,
            ("$contentId", $"{item.EventFingerprint.Value}:{role}"),
            ("$turnId", item.TurnId!),
            ("$role", role),
            ("$body", body),
            ("$occurredAtUtc", Utc(item.OccurredAtUtc)),
            ("$sourceTimezone", item.SourceTimeZone));
    }

    private static void InsertTokens(SqliteTransaction transaction, NormalizedEvent item)
    {
        foreach (var pair in item.TokenCounts)
        {
            Execute(transaction, """
                INSERT OR IGNORE INTO token_usages
                    (token_usage_id, turn_id, token_type, token_count)
                VALUES
                    ($tokenUsageId, $turnId, $tokenType, $tokenCount);
                """,
                ("$tokenUsageId", $"{item.EventFingerprint.Value}:{pair.Key.Value}"),
                ("$turnId", item.TurnId!),
                ("$tokenType", pair.Key.Value),
                ("$tokenCount", pair.Value));
        }
    }

    private static void InsertTags(SqliteTransaction transaction, NormalizedEvent item)
    {
        foreach (var (key, value) in item.Tags)
        {
            var tagId = StableId($"{key}\n{value}");
            Execute(transaction, """
                INSERT OR IGNORE INTO tags (tag_id, tag_key, tag_value, created_at_utc)
                VALUES ($tagId, $tagKey, $tagValue, $createdAtUtc);
                INSERT OR IGNORE INTO source_tags (source_id, tag_id)
                VALUES ($sourceId, $tagId);
                """,
                ("$tagId", tagId),
                ("$tagKey", key),
                ("$tagValue", value),
                ("$createdAtUtc", Utc(item.OccurredAtUtc)),
                ("$sourceId", item.SourceId));
            if (item.SessionId is not null)
            {
                Execute(transaction, "INSERT OR IGNORE INTO session_tags (session_id, tag_id) VALUES ($sessionId, $tagId);", ("$sessionId", item.SessionId), ("$tagId", tagId));
            }
        }
    }

    private static void UpsertImport(SqliteTransaction transaction, string importId, string sourceId, string scanFingerprint, ParseResult parse, int imported)
    {
        Execute(transaction, """
            INSERT OR IGNORE INTO imports
                (import_id, source_id, imported_at_utc, source_timezone, scan_fingerprint, status, valid_event_count, error_count)
            VALUES
                ($importId, $sourceId, $importedAtUtc, $sourceTimezone, $scanFingerprint, $status, $validEventCount, $errorCount)
            ;
            UPDATE imports
            SET status = $status,
                valid_event_count = $validEventCount,
                error_count = $errorCount
            WHERE import_id = $importId;
            """,
            ("$importId", importId),
            ("$sourceId", sourceId),
            ("$importedAtUtc", Utc(DateTimeOffset.UtcNow)),
            ("$sourceTimezone", parse.Events.Count > 0 ? parse.Events[0].SourceTimeZone : "UTC"),
            ("$scanFingerprint", scanFingerprint),
            ("$status", parse.Status.ToString()),
            ("$validEventCount", imported),
            ("$errorCount", parse.Errors.Count));
    }

    private bool ExistsEvent(SqliteTransaction transaction, string fingerprint)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM sub_events WHERE event_fingerprint = $fingerprint);";
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private static SearchDocument CreateSearchDocument(NormalizedEvent item)
    {
        var tags = new StringBuilder();
        foreach (var tag in item.Tags)
        {
            if (tags.Length > 0)
            {
                tags.Append(' ');
            }

            tags.Append(tag.Key).Append(':').Append(tag.Value);
        }

        return new SearchDocument(
            item.EventFingerprint.Value,
            item.SourceId,
            item.SessionId,
            item.TurnId,
            item.Prompt,
            item.Response,
            item.Tool,
            item.Subagent,
            item.Workflow,
            item.Model,
            tags.ToString());
    }

    private static string BuildScanFingerprint(string sourceId, IReadOnlyList<NormalizedEvent> events, string path)
    {
        var value = events.Count == 0 ? path : string.Join('|', events.Select(item => item.EventFingerprint.Value));
        return EventFingerprint.Create(sourceId, "import.scan", DateTimeOffset.UnixEpoch, "UTC", value).Value;
    }

    private static string StableId(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string Required(string value, string parameterName)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value is required", parameterName)
            : value.Trim();
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
}

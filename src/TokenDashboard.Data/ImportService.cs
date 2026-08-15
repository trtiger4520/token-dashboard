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

public readonly record struct ImportProgress(int TotalEvents, int ProcessedEvents);

public sealed class ImportService
{
    private const int ProgressEventInterval = 200;

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
        string? ownerId = null,
        Action<ImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var requiredImportId = Required(importId, nameof(importId));
        ArgumentNullException.ThrowIfNull(adapter);
        var parse = adapter.Parse(path, cancellationToken);
        var sourceId = parse.Events.Count > 0 ? parse.Events[0].SourceId : adapter.Kind.ToString();
        var scanFingerprint = BuildScanFingerprint(sourceId, parse.Events, path);
        var imported = 0;
        var duplicates = 0;
        progress?.Invoke(new ImportProgress(parse.Events.Count, 0));

        using var transaction = connection.BeginTransaction();
        using var commands = new ImportCommands(connection, transaction);
        if (parse.Events.Count == 0)
        {
            Execute(commands.InsertSource,
                ("$sourceId", sourceId),
                ("$adapterKind", adapter.Kind.ToString()),
                ("$name", adapter.Kind.ToString()),
                ("$sourcePath", path),
                ("$sourceTimezone", "UTC"),
                ("$createdAtUtc", Utc(DateTimeOffset.UtcNow)));
        }

        var state = new ImportState();
        var processed = 0;
        foreach (var item in parse.Events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UpsertSource(commands, state, item, sourcePath ?? path);
            if (item.SessionId is not null)
            {
                UpsertSession(commands, state, item, workspaceId, ownerId);
            }

            if (item.SessionId is not null && item.TurnId is not null)
            {
                UpsertTurn(commands, state, item);
            }

            if (ExistsEvent(commands, item.EventFingerprint.Value))
            {
                duplicates++;
            }
            else
            {
                InsertSubEvent(commands, item);
                if (item.SessionId is not null && item.TurnId is not null)
                {
                    InsertContents(commands, item);
                    InsertTokens(commands, item);
                }

                InsertTags(commands, state, item);
                InsertSearchDocument(commands, item);
                imported++;
            }

            processed++;
            if (progress is not null && processed % ProgressEventInterval == 0)
            {
                progress(new ImportProgress(parse.Events.Count, processed));
            }
        }

        FlushSessionActivity(commands, state);
        UpsertImport(commands, requiredImportId, sourceId, scanFingerprint, parse, imported);
        transaction.Commit();
        progress?.Invoke(new ImportProgress(parse.Events.Count, processed));

        return new ImportSummary(requiredImportId, parse.Events.Count, imported, duplicates, parse.Errors, parse.Status);
    }

    private static void UpsertSource(ImportCommands commands, ImportState state, NormalizedEvent item, string sourcePath)
    {
        if (!state.Sources.Add(item.SourceId))
        {
            return;
        }

        Execute(commands.InsertSource,
            ("$sourceId", item.SourceId),
            ("$adapterKind", item.AdapterKind.ToString()),
            ("$name", item.AdapterKind.ToString()),
            ("$sourcePath", sourcePath),
            ("$sourceTimezone", item.SourceTimeZone),
            ("$createdAtUtc", Utc(item.OccurredAtUtc)));
    }

    private static void UpsertSession(ImportCommands commands, ImportState state, NormalizedEvent item, string? workspaceId, string? ownerId)
    {
        var occurredAtUtc = Utc(item.OccurredAtUtc);
        if (state.Sessions.TryGetValue(item.SessionId!, out var lastActivity))
        {
            if (string.CompareOrdinal(occurredAtUtc, lastActivity) > 0)
            {
                state.Sessions[item.SessionId!] = occurredAtUtc;
            }

            return;
        }

        state.Sessions[item.SessionId!] = occurredAtUtc;
        Execute(commands.InsertSession,
            ("$sessionId", item.SessionId!),
            ("$sourceId", item.SourceId),
            ("$occurredAtUtc", occurredAtUtc),
            ("$sourceTimezone", item.SourceTimeZone),
            ("$workspaceId", (object?)workspaceId ?? DBNull.Value),
            ("$ownerId", (object?)ownerId ?? DBNull.Value));
    }

    private static void FlushSessionActivity(ImportCommands commands, ImportState state)
    {
        foreach (var (sessionId, lastActivity) in state.Sessions)
        {
            Execute(commands.UpdateSessionActivity,
                ("$sessionId", sessionId),
                ("$occurredAtUtc", lastActivity));
        }
    }

    private static void UpsertTurn(ImportCommands commands, ImportState state, NormalizedEvent item)
    {
        var effort = string.IsNullOrWhiteSpace(item.Effort) ? null : item.Effort;
        if (state.Turns.TryGetValue(item.TurnId!, out var hasEffort))
        {
            if (!hasEffort && effort is not null)
            {
                state.Turns[item.TurnId!] = true;
                Execute(commands.UpdateTurnEffort, ("$turnId", item.TurnId!), ("$effort", effort));
            }

            return;
        }

        state.Turns[item.TurnId!] = effort is not null;
        var occurredAtUtc = Utc(item.OccurredAtUtc);
        Execute(commands.InsertTurn,
            ("$turnId", item.TurnId!),
            ("$sessionId", item.SessionId!),
            ("$sequence", item.Sequence),
            ("$occurredAtUtc", occurredAtUtc),
            ("$sourceTimezone", item.SourceTimeZone),
            ("$effort", (object?)effort ?? DBNull.Value));

        Execute(commands.InsertTurnWithNextSequence,
            ("$turnId", item.TurnId!),
            ("$sessionId", item.SessionId!),
            ("$occurredAtUtc", occurredAtUtc),
            ("$sourceTimezone", item.SourceTimeZone),
            ("$effort", (object?)effort ?? DBNull.Value));

        if (effort is not null)
        {
            Execute(commands.UpdateTurnEffort, ("$turnId", item.TurnId!), ("$effort", effort));
        }
    }

    private static void InsertSubEvent(ImportCommands commands, NormalizedEvent item)
    {
        Execute(commands.InsertSubEventCommand,
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
            ("$cacheMetricsReported", item.CacheMetricsReported ? 1 : 0),
            ("$fingerprint", item.EventFingerprint.Value));
    }

    private static void InsertContents(ImportCommands commands, NormalizedEvent item)
    {
        InsertContent(commands, item, "prompt", item.Prompt);
        InsertContent(commands, item, "response", item.Response);
    }

    private static void InsertContent(ImportCommands commands, NormalizedEvent item, string role, string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return;
        }

        Execute(commands.InsertContentCommand,
            ("$contentId", $"{item.EventFingerprint.Value}:{role}"),
            ("$turnId", item.TurnId!),
            ("$role", role),
            ("$body", body),
            ("$occurredAtUtc", Utc(item.OccurredAtUtc)),
            ("$sourceTimezone", item.SourceTimeZone));
    }

    private static void InsertTokens(ImportCommands commands, NormalizedEvent item)
    {
        foreach (var pair in item.TokenCounts)
        {
            Execute(commands.InsertTokenUsage,
                ("$tokenUsageId", $"{item.EventFingerprint.Value}:{pair.Key.Value}"),
                ("$turnId", item.TurnId!),
                ("$tokenType", pair.Key.Value),
                ("$tokenCount", pair.Value));
        }
    }

    private static void InsertTags(ImportCommands commands, ImportState state, NormalizedEvent item)
    {
        foreach (var (key, value) in item.Tags)
        {
            var cacheKey = $"{key}\n{value}";
            if (!state.TagIds.TryGetValue(cacheKey, out var tagId))
            {
                tagId = StableId(cacheKey);
                state.TagIds[cacheKey] = tagId;
                Execute(commands.InsertTag,
                    ("$tagId", tagId),
                    ("$tagKey", key),
                    ("$tagValue", value),
                    ("$createdAtUtc", Utc(item.OccurredAtUtc)));
            }

            if (state.SourceTags.Add($"{item.SourceId}\n{tagId}"))
            {
                Execute(commands.InsertSourceTag, ("$sourceId", item.SourceId), ("$tagId", tagId));
            }

            if (item.SessionId is not null && state.SessionTags.Add($"{item.SessionId}\n{tagId}"))
            {
                Execute(commands.InsertSessionTag, ("$sessionId", item.SessionId), ("$tagId", tagId));
            }
        }
    }

    private static void InsertSearchDocument(ImportCommands commands, NormalizedEvent item)
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

        Execute(commands.InsertSearchIndex,
            ("$itemId", item.EventFingerprint.Value),
            ("$sourceId", item.SourceId),
            ("$sessionId", (object?)item.SessionId ?? DBNull.Value),
            ("$turnId", (object?)item.TurnId ?? DBNull.Value),
            ("$prompt", item.Prompt),
            ("$response", item.Response),
            ("$tool", item.Tool),
            ("$subagent", item.Subagent),
            ("$workflow", item.Workflow),
            ("$model", item.Model),
            ("$tags", tags.ToString()));
    }

    private static void UpsertImport(ImportCommands commands, string importId, string sourceId, string scanFingerprint, ParseResult parse, int imported)
    {
        Execute(commands.InsertImport,
            ("$importId", importId),
            ("$sourceId", sourceId),
            ("$importedAtUtc", Utc(DateTimeOffset.UtcNow)),
            ("$sourceTimezone", parse.Events.Count > 0 ? parse.Events[0].SourceTimeZone : "UTC"),
            ("$scanFingerprint", scanFingerprint),
            ("$status", parse.Status.ToString()),
            ("$validEventCount", imported),
            ("$errorCount", parse.Errors.Count));

        Execute(commands.UpdateImport,
            ("$importId", importId),
            ("$status", parse.Status.ToString()),
            ("$validEventCount", imported),
            ("$errorCount", parse.Errors.Count));
    }

    private static bool ExistsEvent(ImportCommands commands, string fingerprint)
    {
        var command = commands.ExistsEventCommand;
        Bind(command, ("$fingerprint", fingerprint));
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
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

    private static void Execute(SqliteCommand command, params (string Name, object Value)[] parameters)
    {
        Bind(command, parameters);
        command.ExecuteNonQuery();
    }

    private static void Bind(SqliteCommand command, params (string Name, object Value)[] parameters)
    {
        foreach (var parameter in parameters)
        {
            if (command.Parameters.Contains(parameter.Name))
            {
                command.Parameters[parameter.Name].Value = parameter.Value;
            }
            else
            {
                command.Parameters.AddWithValue(parameter.Name, parameter.Value);
            }
        }
    }

    private sealed class ImportState
    {
        public HashSet<string> Sources { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> Sessions { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, bool> Turns { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> TagIds { get; } = new(StringComparer.Ordinal);

        public HashSet<string> SourceTags { get; } = new(StringComparer.Ordinal);

        public HashSet<string> SessionTags { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// Holds one prepared command per statement for the duration of a single import so that the
    /// per-event write path reuses compiled statements instead of parsing SQL for every row.
    /// </summary>
    private sealed class ImportCommands : IDisposable
    {
        private readonly List<SqliteCommand> commands = [];
        private readonly SqliteConnection connection;
        private readonly SqliteTransaction transaction;

        public ImportCommands(SqliteConnection connection, SqliteTransaction transaction)
        {
            this.connection = connection;
            this.transaction = transaction;
            InsertSource = Create("""
                INSERT OR IGNORE INTO sources
                    (source_id, adapter_kind, name, source_path, source_timezone, created_at_utc)
                VALUES
                    ($sourceId, $adapterKind, $name, $sourcePath, $sourceTimezone, $createdAtUtc);
                """);
            InsertSession = Create("""
                INSERT OR IGNORE INTO sessions
                    (session_id, source_id, started_at_utc, last_activity_at_utc, source_timezone, workspace_id, owner_id)
                VALUES
                    ($sessionId, $sourceId, $occurredAtUtc, $occurredAtUtc, $sourceTimezone, $workspaceId, $ownerId);
                """);
            UpdateSessionActivity = Create("""
                UPDATE sessions
                SET last_activity_at_utc = MAX(last_activity_at_utc, $occurredAtUtc)
                WHERE session_id = $sessionId;
                """);
            InsertTurn = Create("""
                INSERT OR IGNORE INTO turns
                    (turn_id, session_id, sequence, occurred_at_utc, source_timezone, effort)
                VALUES
                    ($turnId, $sessionId, $sequence, $occurredAtUtc, $sourceTimezone, $effort);
                """);
            InsertTurnWithNextSequence = Create("""
                INSERT OR IGNORE INTO turns
                    (turn_id, session_id, sequence, occurred_at_utc, source_timezone, effort)
                SELECT
                    $turnId,
                    $sessionId,
                    COALESCE(MAX(sequence) + 1, 0),
                    $occurredAtUtc,
                    $sourceTimezone,
                    $effort
                FROM turns
                WHERE session_id = $sessionId
                  AND NOT EXISTS (SELECT 1 FROM turns WHERE turn_id = $turnId);
                """);
            UpdateTurnEffort = Create("UPDATE turns SET effort = COALESCE(effort, $effort) WHERE turn_id = $turnId AND $effort IS NOT NULL;");
            ExistsEventCommand = Create("SELECT EXISTS (SELECT 1 FROM sub_events WHERE event_fingerprint = $fingerprint);");
            InsertSubEventCommand = Create("""
                INSERT INTO sub_events
                    (sub_event_id, source_id, session_id, turn_id, event_type, occurred_at_utc, source_timezone, payload,
                     prompt, response, model, tool, subagent, workflow, event_fingerprint, cache_metrics_reported)
                VALUES
                    ($subEventId, $sourceId, $sessionId, $turnId, $eventType, $occurredAtUtc, $sourceTimezone, $payload,
                     $prompt, $response, $model, $tool, $subagent, $workflow, $fingerprint, $cacheMetricsReported);
                """);
            InsertContentCommand = Create("""
                INSERT OR IGNORE INTO contents
                    (content_id, turn_id, role, body, occurred_at_utc, source_timezone)
                VALUES
                    ($contentId, $turnId, $role, $body, $occurredAtUtc, $sourceTimezone);
                """);
            InsertTokenUsage = Create("""
                INSERT OR IGNORE INTO token_usages
                    (token_usage_id, turn_id, token_type, token_count)
                VALUES
                    ($tokenUsageId, $turnId, $tokenType, $tokenCount);
                """);
            InsertTag = Create("""
                INSERT OR IGNORE INTO tags (tag_id, tag_key, tag_value, created_at_utc)
                VALUES ($tagId, $tagKey, $tagValue, $createdAtUtc);
                """);
            InsertSourceTag = Create("INSERT OR IGNORE INTO source_tags (source_id, tag_id) VALUES ($sourceId, $tagId);");
            InsertSessionTag = Create("INSERT OR IGNORE INTO session_tags (session_id, tag_id) VALUES ($sessionId, $tagId);");
            InsertSearchIndex = Create(FtsIndexingService.InsertSql);
            InsertImport = Create("""
                INSERT OR IGNORE INTO imports
                    (import_id, source_id, imported_at_utc, source_timezone, scan_fingerprint, status, valid_event_count, error_count)
                VALUES
                    ($importId, $sourceId, $importedAtUtc, $sourceTimezone, $scanFingerprint, $status, $validEventCount, $errorCount);
                """);
            UpdateImport = Create("""
                UPDATE imports
                SET status = $status,
                    valid_event_count = $validEventCount,
                    error_count = $errorCount
                WHERE import_id = $importId;
                """);
        }

        public SqliteCommand InsertSource { get; }

        public SqliteCommand InsertSession { get; }

        public SqliteCommand UpdateSessionActivity { get; }

        public SqliteCommand InsertTurn { get; }

        public SqliteCommand InsertTurnWithNextSequence { get; }

        public SqliteCommand UpdateTurnEffort { get; }

        public SqliteCommand ExistsEventCommand { get; }

        public SqliteCommand InsertSubEventCommand { get; }

        public SqliteCommand InsertContentCommand { get; }

        public SqliteCommand InsertTokenUsage { get; }

        public SqliteCommand InsertTag { get; }

        public SqliteCommand InsertSourceTag { get; }

        public SqliteCommand InsertSessionTag { get; }

        public SqliteCommand InsertSearchIndex { get; }

        public SqliteCommand InsertImport { get; }

        public SqliteCommand UpdateImport { get; }

        public void Dispose()
        {
            foreach (var command in commands)
            {
                command.Dispose();
            }
        }

        private SqliteCommand Create(string sql)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            commands.Add(command);
            return command;
        }
    }
}

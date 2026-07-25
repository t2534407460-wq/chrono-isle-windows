using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ChronoIsle.App.Services.Domain;

namespace ChronoIsle.App.Services.Persistence;

public sealed record LifeSchemaMigrationResult(
    int SchemaVersion,
    int MigratedItems,
    int MigratedRecurrenceRules);

/// <summary>
/// Creates the canonical life-item schema and imports the four version-one tables.
/// The legacy tables remain untouched until the application has completed its cutover.
/// </summary>
public sealed class LifeSchemaMigrator
{
    public const int CurrentVersion = 1;

    readonly string assumedIanaTimeZoneId;
    readonly Func<DateTimeOffset> utcNow;
    readonly ITimeZoneCatalog timeZoneCatalog;
    readonly IOccurrenceTimeResolver occurrenceTimeResolver;

    public LifeSchemaMigrator(
        string? assumedIanaTimeZoneId = null,
        Func<DateTimeOffset>? utcNow = null,
        ITimeZoneCatalog? timeZoneCatalog = null,
        IOccurrenceTimeResolver? occurrenceTimeResolver = null)
    {
        this.timeZoneCatalog = timeZoneCatalog ?? new SystemTimeZoneCatalog();
        this.occurrenceTimeResolver = occurrenceTimeResolver ?? new OccurrenceTimeResolver(this.timeZoneCatalog);
        this.assumedIanaTimeZoneId = assumedIanaTimeZoneId ?? DetectLocalIanaTimeZoneId();
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);

        if (!this.timeZoneCatalog.TryResolveIana(this.assumedIanaTimeZoneId, out _, out _))
            throw new InvalidOperationException($"The migration time zone '{this.assumedIanaTimeZoneId}' cannot be resolved.");
    }

    public LifeSchemaMigrationResult Migrate(SqliteConnection connection)
    {
        ValidateOpenConnection(connection);
        EnableForeignKeys(connection);
        using var transaction = connection.BeginTransaction();
        try
        {
            var result = Migrate(connection, transaction);
            transaction.Commit();
            return result;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public LifeSchemaMigrationResult Migrate(SqliteConnection connection, SqliteTransaction transaction)
    {
        ValidateOpenConnection(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!ReferenceEquals(transaction.Connection, connection))
            throw new ArgumentException("The migration transaction must belong to the supplied connection.", nameof(transaction));

        if (TableExists(connection, transaction, "schema_migrations") &&
            IsSchemaVersionApplied(connection, transaction))
            return new(CurrentVersion, 0, 0);
        CreateSchema(connection, transaction);

        var migratedItems = 0;
        var migratedRules = 0;
        if (TableExists(connection, transaction, "todos"))
            migratedItems += MigrateTodos(connection, transaction);
        if (TableExists(connection, transaction, "calendar_events"))
            migratedItems += MigrateEvents(connection, transaction);
        if (TableExists(connection, transaction, "single_reminders"))
            migratedItems += MigrateSingleReminders(connection, transaction);
        if (TableExists(connection, transaction, "recurring_reminders"))
        {
            var recurring = MigrateRecurringReminders(connection, transaction);
            migratedItems += recurring.Items;
            migratedRules += recurring.Rules;
        }

        ValidateImportedCounts(connection, transaction);
        ValidateIntegrity(connection, transaction);
        RecordSchemaVersion(connection, transaction);
        return new(CurrentVersion, migratedItems, migratedRules);
    }

    static bool IsSchemaVersionApplied(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM schema_migrations WHERE version=$version)";
        command.Parameters.AddWithValue("$version", CurrentVersion);
        return Convert.ToInt64(command.ExecuteScalar()) == 1;
    }

    static void ValidateOpenConnection(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.State != System.Data.ConnectionState.Open)
            throw new InvalidOperationException("The SQLite connection must be open before migration.");
    }

    static void EnableForeignKeys(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON";
        command.ExecuteNonQuery();
    }

    static void CreateSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS schema_migrations(
                version INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                applied_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS life_items(
                id TEXT PRIMARY KEY,
                kind TEXT NOT NULL CHECK(kind IN ('Todo','Reminder','Event')),
                title TEXT NOT NULL CHECK(length(trim(title)) > 0),
                notes TEXT,
                status TEXT NOT NULL DEFAULT 'Pending'
                    CHECK(status IN ('Pending','InProgress','Completed','Deferred','Cancelled','Ignored')),
                row_version INTEGER NOT NULL DEFAULT 1 CHECK(row_version >= 1),

                due_local_datetime TEXT,
                due_utc_instant TEXT,
                due_iana_time_zone_id TEXT,
                due_windows_time_zone_id_cache TEXT,
                due_time_semantics TEXT,

                remind_local_datetime TEXT,
                remind_utc_instant TEXT,
                remind_iana_time_zone_id TEXT,
                remind_windows_time_zone_id_cache TEXT,
                remind_time_semantics TEXT,

                start_local_datetime TEXT,
                start_utc_instant TEXT,
                start_iana_time_zone_id TEXT,
                start_windows_time_zone_id_cache TEXT,
                start_time_semantics TEXT,

                end_local_datetime TEXT,
                end_utc_instant TEXT,
                end_iana_time_zone_id TEXT,
                end_windows_time_zone_id_cache TEXT,
                end_time_semantics TEXT,

                origin_type TEXT NOT NULL DEFAULT 'Local'
                    CHECK(origin_type IN ('Local','IcsImport','MicrosoftToDo','OutlookCalendar')),
                origin_adapter TEXT,
                is_readonly INTEGER NOT NULL DEFAULT 0 CHECK(is_readonly IN (0,1)),
                readonly_reason TEXT,
                raw_external_payload_id TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                deleted_at TEXT,

                CHECK(
                    (is_readonly = 0 AND readonly_reason IS NULL)
                    OR
                    (is_readonly = 1 AND readonly_reason IS NOT NULL AND length(trim(readonly_reason)) > 0)
                ),

                CHECK(
                    (due_time_semantics IS NULL
                        AND due_local_datetime IS NULL AND due_utc_instant IS NULL
                        AND due_iana_time_zone_id IS NULL AND due_windows_time_zone_id_cache IS NULL)
                    OR
                    (due_time_semantics = 'AbsoluteInstant'
                        AND due_local_datetime IS NULL AND due_utc_instant IS NOT NULL
                        AND julianday(due_utc_instant) IS NOT NULL
                        AND due_iana_time_zone_id IS NULL AND due_windows_time_zone_id_cache IS NULL)
                    OR
                    (due_time_semantics = 'ZonedWallClock'
                        AND due_local_datetime IS NOT NULL AND julianday(due_local_datetime) IS NOT NULL
                        AND due_utc_instant IS NOT NULL AND julianday(due_utc_instant) IS NOT NULL
                        AND due_iana_time_zone_id IS NOT NULL AND length(trim(due_iana_time_zone_id)) > 0)
                    OR
                    (due_time_semantics = 'DeviceLocalFloatingWallClock'
                        AND due_local_datetime IS NOT NULL AND julianday(due_local_datetime) IS NOT NULL
                        AND due_utc_instant IS NOT NULL AND julianday(due_utc_instant) IS NOT NULL
                        AND due_iana_time_zone_id IS NULL AND due_windows_time_zone_id_cache IS NULL)
                ),
                CHECK(
                    (remind_time_semantics IS NULL
                        AND remind_local_datetime IS NULL AND remind_utc_instant IS NULL
                        AND remind_iana_time_zone_id IS NULL AND remind_windows_time_zone_id_cache IS NULL)
                    OR
                    (remind_time_semantics = 'AbsoluteInstant'
                        AND remind_local_datetime IS NULL AND remind_utc_instant IS NOT NULL
                        AND julianday(remind_utc_instant) IS NOT NULL
                        AND remind_iana_time_zone_id IS NULL AND remind_windows_time_zone_id_cache IS NULL)
                    OR
                    (remind_time_semantics = 'ZonedWallClock'
                        AND remind_local_datetime IS NOT NULL AND julianday(remind_local_datetime) IS NOT NULL
                        AND remind_utc_instant IS NOT NULL AND julianday(remind_utc_instant) IS NOT NULL
                        AND remind_iana_time_zone_id IS NOT NULL AND length(trim(remind_iana_time_zone_id)) > 0)
                    OR
                    (remind_time_semantics = 'DeviceLocalFloatingWallClock'
                        AND remind_local_datetime IS NOT NULL AND julianday(remind_local_datetime) IS NOT NULL
                        AND remind_utc_instant IS NOT NULL AND julianday(remind_utc_instant) IS NOT NULL
                        AND remind_iana_time_zone_id IS NULL AND remind_windows_time_zone_id_cache IS NULL)
                ),
                CHECK(
                    (start_time_semantics IS NULL
                        AND start_local_datetime IS NULL AND start_utc_instant IS NULL
                        AND start_iana_time_zone_id IS NULL AND start_windows_time_zone_id_cache IS NULL)
                    OR
                    (start_time_semantics = 'AbsoluteInstant'
                        AND start_local_datetime IS NULL AND start_utc_instant IS NOT NULL
                        AND julianday(start_utc_instant) IS NOT NULL
                        AND start_iana_time_zone_id IS NULL AND start_windows_time_zone_id_cache IS NULL)
                    OR
                    (start_time_semantics = 'ZonedWallClock'
                        AND start_local_datetime IS NOT NULL AND julianday(start_local_datetime) IS NOT NULL
                        AND start_utc_instant IS NOT NULL AND julianday(start_utc_instant) IS NOT NULL
                        AND start_iana_time_zone_id IS NOT NULL AND length(trim(start_iana_time_zone_id)) > 0)
                    OR
                    (start_time_semantics = 'DeviceLocalFloatingWallClock'
                        AND start_local_datetime IS NOT NULL AND julianday(start_local_datetime) IS NOT NULL
                        AND start_utc_instant IS NOT NULL AND julianday(start_utc_instant) IS NOT NULL
                        AND start_iana_time_zone_id IS NULL AND start_windows_time_zone_id_cache IS NULL)
                ),
                CHECK(
                    (end_time_semantics IS NULL
                        AND end_local_datetime IS NULL AND end_utc_instant IS NULL
                        AND end_iana_time_zone_id IS NULL AND end_windows_time_zone_id_cache IS NULL)
                    OR
                    (end_time_semantics = 'AbsoluteInstant'
                        AND end_local_datetime IS NULL AND end_utc_instant IS NOT NULL
                        AND julianday(end_utc_instant) IS NOT NULL
                        AND end_iana_time_zone_id IS NULL AND end_windows_time_zone_id_cache IS NULL)
                    OR
                    (end_time_semantics = 'ZonedWallClock'
                        AND end_local_datetime IS NOT NULL AND julianday(end_local_datetime) IS NOT NULL
                        AND end_utc_instant IS NOT NULL AND julianday(end_utc_instant) IS NOT NULL
                        AND end_iana_time_zone_id IS NOT NULL AND length(trim(end_iana_time_zone_id)) > 0)
                    OR
                    (end_time_semantics = 'DeviceLocalFloatingWallClock'
                        AND end_local_datetime IS NOT NULL AND julianday(end_local_datetime) IS NOT NULL
                        AND end_utc_instant IS NOT NULL AND julianday(end_utc_instant) IS NOT NULL
                        AND end_iana_time_zone_id IS NULL AND end_windows_time_zone_id_cache IS NULL)
                ),

                CHECK(
                    (kind = 'Todo'
                        AND start_local_datetime IS NULL AND start_utc_instant IS NULL
                        AND start_iana_time_zone_id IS NULL AND start_windows_time_zone_id_cache IS NULL
                        AND start_time_semantics IS NULL
                        AND end_local_datetime IS NULL AND end_utc_instant IS NULL
                        AND end_iana_time_zone_id IS NULL AND end_windows_time_zone_id_cache IS NULL
                        AND end_time_semantics IS NULL)
                    OR
                    (kind = 'Reminder'
                        AND remind_time_semantics IS NOT NULL
                        AND start_local_datetime IS NULL AND start_utc_instant IS NULL
                        AND start_iana_time_zone_id IS NULL AND start_windows_time_zone_id_cache IS NULL
                        AND start_time_semantics IS NULL
                        AND end_local_datetime IS NULL AND end_utc_instant IS NULL
                        AND end_iana_time_zone_id IS NULL AND end_windows_time_zone_id_cache IS NULL
                        AND end_time_semantics IS NULL)
                    OR
                    (kind = 'Event'
                        AND start_time_semantics IS NOT NULL AND end_time_semantics IS NOT NULL
                        AND start_time_semantics = end_time_semantics
                        AND (
                            start_time_semantics = 'AbsoluteInstant'
                            OR start_time_semantics = 'DeviceLocalFloatingWallClock'
                            OR (start_time_semantics = 'ZonedWallClock'
                                AND start_iana_time_zone_id IS NOT NULL AND end_iana_time_zone_id IS NOT NULL
                                AND start_iana_time_zone_id = end_iana_time_zone_id))
                        AND start_utc_instant IS NOT NULL AND julianday(start_utc_instant) IS NOT NULL
                        AND end_utc_instant IS NOT NULL AND julianday(end_utc_instant) IS NOT NULL
                        AND julianday(end_utc_instant) > julianday(start_utc_instant)
                        AND due_local_datetime IS NULL AND due_utc_instant IS NULL
                        AND due_iana_time_zone_id IS NULL AND due_windows_time_zone_id_cache IS NULL
                        AND due_time_semantics IS NULL)
                )
            );

            CREATE TABLE IF NOT EXISTS recurrence_rules(
                id TEXT PRIMARY KEY,
                series_item_id TEXT NOT NULL REFERENCES life_items(id),
                rule_version INTEGER NOT NULL CHECK(rule_version >= 1),
                start_local_datetime TEXT NOT NULL CHECK(julianday(start_local_datetime) IS NOT NULL),
                iana_time_zone_id TEXT NOT NULL CHECK(length(trim(iana_time_zone_id)) > 0),
                windows_time_zone_id_cache TEXT,
                frequency TEXT NOT NULL CHECK(frequency IN ('Daily','Weekly','Monthly','Yearly')),
                interval INTEGER NOT NULL DEFAULT 1 CHECK(interval >= 1),
                weekdays TEXT,
                month_day INTEGER CHECK(month_day IS NULL OR (month_day >= 1 AND month_day <= 31)),
                end_kind TEXT NOT NULL DEFAULT 'Never' CHECK(end_kind IN ('Never','Until','Count')),
                end_local_datetime TEXT,
                occurrence_count INTEGER,
                next_occurrence_utc TEXT NOT NULL CHECK(julianday(next_occurrence_utc) IS NOT NULL),
                row_version INTEGER NOT NULL DEFAULT 1 CHECK(row_version >= 1),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                deleted_at TEXT,
                CHECK(
                    (end_kind = 'Never' AND end_local_datetime IS NULL AND occurrence_count IS NULL)
                    OR
                    (end_kind = 'Until' AND end_local_datetime IS NOT NULL
                        AND julianday(end_local_datetime) IS NOT NULL AND occurrence_count IS NULL)
                    OR
                    (end_kind = 'Count' AND end_local_datetime IS NULL
                        AND occurrence_count IS NOT NULL AND occurrence_count > 0)
                )
            );

            CREATE TABLE IF NOT EXISTS migration_audit(
                id TEXT PRIMARY KEY,
                migration_version INTEGER NOT NULL,
                source_table TEXT NOT NULL,
                source_id TEXT NOT NULL,
                target_item_id TEXT NOT NULL REFERENCES life_items(id),
                original_values_json TEXT NOT NULL,
                assumed_iana_time_zone_id TEXT NOT NULL,
                migrated_local_values_json TEXT NOT NULL,
                migrated_utc_values_json TEXT NOT NULL,
                created_at TEXT NOT NULL,
                UNIQUE(migration_version, source_table, source_id)
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_life_items_active_external_origin
                ON life_items(origin_adapter, raw_external_payload_id)
                WHERE deleted_at IS NULL
                  AND origin_adapter IS NOT NULL
                  AND raw_external_payload_id IS NOT NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS ux_recurrence_rules_active_series
                ON recurrence_rules(series_item_id)
                WHERE deleted_at IS NULL;
            CREATE INDEX IF NOT EXISTS ix_life_items_active_due
                ON life_items(due_utc_instant)
                WHERE deleted_at IS NULL AND due_utc_instant IS NOT NULL;
            CREATE INDEX IF NOT EXISTS ix_life_items_active_remind
                ON life_items(remind_utc_instant)
                WHERE deleted_at IS NULL AND remind_utc_instant IS NOT NULL;
            """);
    }

    int MigrateTodos(SqliteConnection connection, SqliteTransaction transaction)
    {
        var rows = ReadRows(connection, transaction, """
            SELECT id,title,notes,completed,due_at,remind_at,created_at,updated_at FROM todos
            """, reader => new LegacyTodo(
                reader.GetString(0), reader.GetString(1), NullableText(reader, 2), reader.GetInt64(3) != 0,
                NullableText(reader, 4), NullableText(reader, 5), reader.GetString(6), reader.GetString(7)));

        var migrated = 0;
        foreach (var row in rows)
        {
            if (!ReserveTarget(connection, transaction, "todos", row.Id)) continue;
            var due = ConvertLegacyTime(row.DueAt);
            var remind = ConvertLegacyTime(row.RemindAt);
            InsertLifeItem(connection, transaction, row.Id, "Todo", row.Title, row.Notes,
                row.Completed ? "Completed" : "Pending", due, remind, null, null, row.CreatedAt, row.UpdatedAt);
            InsertAudit(connection, transaction, "todos", row.Id, row.Id, row,
                TimesJson(("due", due), ("remind", remind)), UtcTimesJson(("due", due), ("remind", remind)));
            migrated++;
        }
        return migrated;
    }

    int MigrateEvents(SqliteConnection connection, SqliteTransaction transaction)
    {
        var rows = ReadRows(connection, transaction, """
            SELECT id,title,notes,start_at,end_at,remind_at,created_at,updated_at FROM calendar_events
            """, reader => new LegacyEvent(
                reader.GetString(0), reader.GetString(1), NullableText(reader, 2), reader.GetString(3), reader.GetString(4),
                NullableText(reader, 5), reader.GetString(6), reader.GetString(7)));

        var migrated = 0;
        foreach (var row in rows)
        {
            if (!ReserveTarget(connection, transaction, "calendar_events", row.Id)) continue;
            var start = ConvertLegacyTime(row.StartAt)!;
            var end = ConvertLegacyTime(row.EndAt)!;
            var remind = ConvertLegacyTime(row.RemindAt);
            InsertLifeItem(connection, transaction, row.Id, "Event", row.Title, row.Notes, "Pending",
                null, remind, start, end, row.CreatedAt, row.UpdatedAt);
            InsertAudit(connection, transaction, "calendar_events", row.Id, row.Id, row,
                TimesJson(("start", start), ("end", end), ("remind", remind)),
                UtcTimesJson(("start", start), ("end", end), ("remind", remind)));
            migrated++;
        }
        return migrated;
    }

    int MigrateSingleReminders(SqliteConnection connection, SqliteTransaction transaction)
    {
        var rows = ReadRows(connection, transaction, """
            SELECT id,title,notes,remind_at,created_at,updated_at FROM single_reminders
            """, reader => new LegacySingleReminder(
                reader.GetString(0), reader.GetString(1), NullableText(reader, 2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5)));

        var migrated = 0;
        foreach (var row in rows)
        {
            if (!ReserveTarget(connection, transaction, "single_reminders", row.Id)) continue;
            var remind = ConvertLegacyTime(row.RemindAt)!;
            InsertLifeItem(connection, transaction, row.Id, "Reminder", row.Title, row.Notes, "Pending",
                null, remind, null, null, row.CreatedAt, row.UpdatedAt);
            InsertAudit(connection, transaction, "single_reminders", row.Id, row.Id, row,
                TimesJson(("remind", remind)), UtcTimesJson(("remind", remind)));
            migrated++;
        }
        return migrated;
    }

    (int Items, int Rules) MigrateRecurringReminders(SqliteConnection connection, SqliteTransaction transaction)
    {
        var rows = ReadRows(connection, transaction, """
            SELECT id,title,notes,reminder_time,recurrence,weekdays,created_at,updated_at FROM recurring_reminders
            """, reader => new LegacyRecurringReminder(
                reader.GetString(0), reader.GetString(1), NullableText(reader, 2), reader.GetString(3),
                reader.GetString(4), NullableText(reader, 5), reader.GetString(6), reader.GetString(7)));

        var itemCount = 0;
        var ruleCount = 0;
        foreach (var row in rows)
        {
            if (!ReserveTarget(connection, transaction, "recurring_reminders", row.Id)) continue;
            var occurrence = NextLegacyOccurrence(row);
            InsertLifeItem(connection, transaction, row.Id, "Reminder", row.Title, row.Notes, "Pending",
                null, occurrence, null, null, row.CreatedAt, row.UpdatedAt);
            InsertRecurrenceRule(connection, transaction, row, occurrence);
            InsertAudit(connection, transaction, "recurring_reminders", row.Id, row.Id, row,
                TimesJson(("nextOccurrence", occurrence)), UtcTimesJson(("nextOccurrence", occurrence)));
            itemCount++;
            ruleCount++;
        }
        return (itemCount, ruleCount);
    }

    bool ReserveTarget(SqliteConnection connection, SqliteTransaction transaction, string sourceTable, string sourceId)
    {
        using (var audit = connection.CreateCommand())
        {
            audit.Transaction = transaction;
            audit.CommandText = """
                SELECT target_item_id FROM migration_audit
                WHERE migration_version=$version AND source_table=$table AND source_id=$source
                """;
            audit.Parameters.AddWithValue("$version", CurrentVersion);
            audit.Parameters.AddWithValue("$table", sourceTable);
            audit.Parameters.AddWithValue("$source", sourceId);
            var target = audit.ExecuteScalar() as string;
            if (target is not null)
            {
                if (!string.Equals(target, sourceId, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Migration audit target mismatch for {sourceTable}/{sourceId}.");
                return false;
            }
        }

        using var item = connection.CreateCommand();
        item.Transaction = transaction;
        item.CommandText = "SELECT COUNT(*) FROM life_items WHERE id=$id";
        item.Parameters.AddWithValue("$id", sourceId);
        if (Convert.ToInt64(item.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
            throw new InvalidOperationException($"Legacy item id collision for {sourceTable}/{sourceId}.");
        return true;
    }

    void InsertLifeItem(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string id,
        string kind,
        string title,
        string? notes,
        string status,
        MigratedTime? due,
        MigratedTime? remind,
        MigratedTime? start,
        MigratedTime? end,
        string createdAt,
        string updatedAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO life_items(
                id,kind,title,notes,status,row_version,
                due_local_datetime,due_utc_instant,due_iana_time_zone_id,due_windows_time_zone_id_cache,due_time_semantics,
                remind_local_datetime,remind_utc_instant,remind_iana_time_zone_id,remind_windows_time_zone_id_cache,remind_time_semantics,
                start_local_datetime,start_utc_instant,start_iana_time_zone_id,start_windows_time_zone_id_cache,start_time_semantics,
                end_local_datetime,end_utc_instant,end_iana_time_zone_id,end_windows_time_zone_id_cache,end_time_semantics,
                origin_type,origin_adapter,is_readonly,readonly_reason,raw_external_payload_id,
                created_at,updated_at,deleted_at)
            VALUES(
                $id,$kind,$title,$notes,$status,1,
                $dueLocal,$dueUtc,$dueIana,$dueWindows,$dueSemantics,
                $remindLocal,$remindUtc,$remindIana,$remindWindows,$remindSemantics,
                $startLocal,$startUtc,$startIana,$startWindows,$startSemantics,
                $endLocal,$endUtc,$endIana,$endWindows,$endSemantics,
                'Local',NULL,0,NULL,NULL,$created,$updated,NULL)
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$notes", DbValue(notes));
        command.Parameters.AddWithValue("$status", status);
        AddTimeParameters(command, "due", due);
        AddTimeParameters(command, "remind", remind);
        AddTimeParameters(command, "start", start);
        AddTimeParameters(command, "end", end);
        command.Parameters.AddWithValue("$created", NormalizeAuditDate(createdAt));
        command.Parameters.AddWithValue("$updated", NormalizeAuditDate(updatedAt));
        command.ExecuteNonQuery();
    }

    void InsertRecurrenceRule(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LegacyRecurringReminder row,
        MigratedTime occurrence)
    {
        var recurrence = row.Recurrence.Trim();
        var (frequency, weekdays) = recurrence.ToLowerInvariant() switch
        {
            "daily" => ("Daily", (string?)null),
            "weekdays" => ("Weekly", "1,2,3,4,5"),
            "weekly" when !string.IsNullOrWhiteSpace(row.Weekdays) => ("Weekly", NormalizeWeekdays(row.Weekdays)),
            "weekly" => throw new InvalidOperationException($"Weekly reminder '{row.Id}' has no weekdays."),
            _ => throw new InvalidOperationException($"Unsupported legacy recurrence '{row.Recurrence}'.")
        };

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO recurrence_rules(
                id,series_item_id,rule_version,start_local_datetime,iana_time_zone_id,
                windows_time_zone_id_cache,frequency,interval,weekdays,month_day,end_kind,
                end_local_datetime,occurrence_count,next_occurrence_utc,row_version,created_at,updated_at,deleted_at)
            VALUES($id,$series,1,$startLocal,$iana,$windows,$frequency,1,$weekdays,NULL,'Never',
                NULL,NULL,$nextUtc,1,$created,$updated,NULL)
            """;
        command.Parameters.AddWithValue("$id", $"legacy-recurring:{row.Id}:v1");
        command.Parameters.AddWithValue("$series", row.Id);
        command.Parameters.AddWithValue("$startLocal", occurrence.LocalDateTime);
        command.Parameters.AddWithValue("$iana", assumedIanaTimeZoneId);
        command.Parameters.AddWithValue("$windows", DbValue(occurrence.WindowsTimeZoneId));
        command.Parameters.AddWithValue("$frequency", frequency);
        command.Parameters.AddWithValue("$weekdays", DbValue(weekdays));
        command.Parameters.AddWithValue("$nextUtc", occurrence.UtcInstant);
        command.Parameters.AddWithValue("$created", NormalizeAuditDate(row.CreatedAt));
        command.Parameters.AddWithValue("$updated", NormalizeAuditDate(row.UpdatedAt));
        command.ExecuteNonQuery();
    }

    void InsertAudit(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceTable,
        string sourceId,
        string targetItemId,
        object originalValues,
        string localValuesJson,
        string utcValuesJson)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO migration_audit(
                id,migration_version,source_table,source_id,target_item_id,original_values_json,
                assumed_iana_time_zone_id,migrated_local_values_json,migrated_utc_values_json,created_at)
            VALUES($id,$version,$table,$source,$target,$original,$iana,$local,$utc,$created)
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$version", CurrentVersion);
        command.Parameters.AddWithValue("$table", sourceTable);
        command.Parameters.AddWithValue("$source", sourceId);
        command.Parameters.AddWithValue("$target", targetItemId);
        command.Parameters.AddWithValue("$original", JsonSerializer.Serialize(originalValues));
        command.Parameters.AddWithValue("$iana", assumedIanaTimeZoneId);
        command.Parameters.AddWithValue("$local", localValuesJson);
        command.Parameters.AddWithValue("$utc", utcValuesJson);
        command.Parameters.AddWithValue("$created", utcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    MigratedTime? ConvertLegacyTime(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
            throw new InvalidOperationException($"Legacy time '{raw}' is invalid.");

        var local = DateTime.SpecifyKind(parsed.DateTime, DateTimeKind.Unspecified);
        var windows = TimeZoneInfo.TryConvertIanaIdToWindowsId(assumedIanaTimeZoneId, out var windowsId)
            ? windowsId
            : null;
        var temporal = new TemporalValue(local, null, assumedIanaTimeZoneId, windows, TimeSemantics.ZonedWallClock);
        var instant = occurrenceTimeResolver.ResolveSingleLocalTime(temporal);
        return new(
            local.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
            instant.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            windows);
    }

    MigratedTime NextLegacyOccurrence(LegacyRecurringReminder row)
    {
        if (!TimeOnly.TryParse(row.ReminderTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var reminderTime))
            throw new InvalidOperationException($"Legacy reminder time '{row.ReminderTime}' is invalid.");
        if (!timeZoneCatalog.TryResolveIana(assumedIanaTimeZoneId, out var zone, out _) || zone is null)
            throw new InvalidOperationException($"The migration time zone '{assumedIanaTimeZoneId}' cannot be resolved.");

        var now = utcNow();
        var localNow = TimeZoneInfo.ConvertTime(now, zone).DateTime;
        for (var day = localNow.Date; day <= localNow.Date.AddDays(8); day = day.AddDays(1))
        {
            if (!MatchesLegacyRecurrence(row, day.DayOfWeek)) continue;
            var local = DateTime.SpecifyKind(day.Add(reminderTime.ToTimeSpan()), DateTimeKind.Unspecified);
            var windows = TimeZoneInfo.TryConvertIanaIdToWindowsId(assumedIanaTimeZoneId, out var windowsId)
                ? windowsId
                : null;
            var temporal = new TemporalValue(local, null, assumedIanaTimeZoneId, windows, TimeSemantics.ZonedWallClock);
            var instant = occurrenceTimeResolver.ResolveRecurringOccurrence(local, assumedIanaTimeZoneId);
            if (instant <= now) continue;
            return new(
                local.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
                instant.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                windows);
        }
        throw new InvalidOperationException($"No occurrence could be generated for legacy reminder '{row.Id}'.");
    }

    static bool MatchesLegacyRecurrence(LegacyRecurringReminder row, DayOfWeek day) =>
        row.Recurrence.Trim().ToLowerInvariant() switch
        {
            "daily" => true,
            "weekdays" => day is not DayOfWeek.Saturday and not DayOfWeek.Sunday,
            "weekly" => ParseWeekdays(row.Weekdays).Contains((int)day),
            _ => throw new InvalidOperationException($"Unsupported legacy recurrence '{row.Recurrence}'.")
        };

    static string NormalizeWeekdays(string value) => string.Join(",", ParseWeekdays(value).Order());

    static HashSet<int> ParseWeekdays(string? value)
    {
        var result = new HashSet<int>();
        foreach (var part in (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var day) || day is < 0 or > 6)
                throw new InvalidOperationException($"Legacy weekday '{part}' is invalid.");
            result.Add(day);
        }
        return result;
    }

    static void ValidateImportedCounts(SqliteConnection connection, SqliteTransaction transaction)
    {
        foreach (var table in new[] { "todos", "calendar_events", "single_reminders", "recurring_reminders" })
        {
            if (!TableExists(connection, transaction, table)) continue;
            var sourceCount = ScalarCount(connection, transaction, $"SELECT COUNT(*) FROM {table}");
            var importedCount = ScalarCount(connection, transaction, $"""
                SELECT COUNT(*)
                FROM {table} source
                JOIN migration_audit audit
                  ON audit.source_id=source.id
                 AND audit.source_table=$table
                 AND audit.migration_version=$version
                JOIN life_items item ON item.id=audit.target_item_id
                """, ("$table", table), ("$version", CurrentVersion));
            if (sourceCount != importedCount)
                throw new InvalidOperationException($"Migration count validation failed for {table}: source={sourceCount}, imported={importedCount}.");
        }
    }

    static void ValidateIntegrity(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA integrity_check";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var result = reader.GetString(0);
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"SQLite integrity_check failed: {result}");
        }
    }

    void RecordSchemaVersion(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO schema_migrations(version,name,applied_at)
            VALUES($version,$name,$applied)
            ON CONFLICT(version) DO NOTHING
            """;
        command.Parameters.AddWithValue("$version", CurrentVersion);
        command.Parameters.AddWithValue("$name", "canonical-life-items-v1");
        command.Parameters.AddWithValue("$applied", utcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    static bool TableExists(SqliteConnection connection, SqliteTransaction transaction, string table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    static long ScalarCount(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    static List<T> ReadRows<T>(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        Func<SqliteDataReader, T> map)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read()) rows.Add(map(reader));
        return rows;
    }

    static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    static string? NullableText(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    static object DbValue(string? value) => value is null ? DBNull.Value : value;

    static string NormalizeAuditDate(string raw)
    {
        if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            throw new InvalidOperationException($"Legacy audit date '{raw}' is invalid.");
        return parsed.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    }

    void AddTimeParameters(SqliteCommand command, string prefix, MigratedTime? value)
    {
        command.Parameters.AddWithValue($"${prefix}Local", DbValue(value?.LocalDateTime));
        command.Parameters.AddWithValue($"${prefix}Utc", DbValue(value?.UtcInstant));
        command.Parameters.AddWithValue($"${prefix}Iana", DbValue(value is null ? null : assumedIanaTimeZoneId));
        command.Parameters.AddWithValue($"${prefix}Windows", DbValue(value?.WindowsTimeZoneId));
        command.Parameters.AddWithValue($"${prefix}Semantics", DbValue(value is null ? null : "ZonedWallClock"));
    }

    string TimesJson(params (string Name, MigratedTime? Value)[] values) => JsonSerializer.Serialize(
        values.Where(value => value.Value is not null)
            .ToDictionary(value => value.Name, value => value.Value!.LocalDateTime));

    static string UtcTimesJson(params (string Name, MigratedTime? Value)[] values) => JsonSerializer.Serialize(
        values.Where(value => value.Value is not null)
            .ToDictionary(value => value.Name, value => value.Value!.UtcInstant));

    string DetectLocalIanaTimeZoneId()
    {
        var localId = TimeZoneInfo.Local.Id;
        if (timeZoneCatalog.TryResolveIana(localId, out _, out _)) return localId;
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(localId, out var ianaId) &&
            timeZoneCatalog.TryResolveIana(ianaId, out _, out _))
            return ianaId;
        throw new InvalidOperationException($"The local time zone '{localId}' cannot be represented as an IANA id.");
    }

    sealed record MigratedTime(string LocalDateTime, string UtcInstant, string? WindowsTimeZoneId);

    sealed record LegacyTodo(
        string Id, string Title, string? Notes, bool Completed, string? DueAt, string? RemindAt,
        string CreatedAt, string UpdatedAt);

    sealed record LegacyEvent(
        string Id, string Title, string? Notes, string StartAt, string EndAt, string? RemindAt,
        string CreatedAt, string UpdatedAt);

    sealed record LegacySingleReminder(
        string Id, string Title, string? Notes, string RemindAt, string CreatedAt, string UpdatedAt);

    sealed record LegacyRecurringReminder(
        string Id, string Title, string? Notes, string ReminderTime, string Recurrence, string? Weekdays,
        string CreatedAt, string UpdatedAt);
}

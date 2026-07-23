using System.Globalization;
using Microsoft.Data.Sqlite;
using OpenIsland.App.Services.Domain;

namespace OpenIsland.App.Services.Persistence;

internal sealed class CanonicalLifeItemWriter
{
    readonly string ianaTimeZoneId;
    readonly string? windowsTimeZoneId;
    readonly IOccurrenceTimeResolver occurrenceTimeResolver;

    public CanonicalLifeItemWriter(
        ITimeZoneCatalog? timeZoneCatalog = null,
        IOccurrenceTimeResolver? occurrenceTimeResolver = null)
    {
        var catalog = timeZoneCatalog ?? new SystemTimeZoneCatalog();
        this.occurrenceTimeResolver = occurrenceTimeResolver ?? new OccurrenceTimeResolver(catalog);
        ianaTimeZoneId = DetectLocalIanaTimeZoneId(catalog);
        windowsTimeZoneId = TimeZoneInfo.TryConvertIanaIdToWindowsId(ianaTimeZoneId, out var windowsId)
            ? windowsId
            : null;
    }

    public void UpsertTodo(SqliteConnection connection, SqliteTransaction transaction, TodoItem item) =>
        UpsertLifeItem(
            connection,
            transaction,
            item.Id,
            "Todo",
            item.Title,
            item.Notes,
            item.IsCompleted ? "Completed" : "Pending",
            Resolve(item.DueAt),
            Resolve(item.RemindAt),
            null,
            null,
            item.CreatedAt,
            item.UpdatedAt);

    public void UpsertEvent(SqliteConnection connection, SqliteTransaction transaction, CalendarEventItem item) =>
        UpsertLifeItem(
            connection,
            transaction,
            item.Id,
            "Event",
            item.Title,
            item.Notes,
            "Pending",
            null,
            Resolve(item.RemindAt),
            Resolve(item.StartsAt),
            Resolve(item.EndsAt),
            item.CreatedAt,
            item.UpdatedAt);

    public void UpsertReminder(SqliteConnection connection, SqliteTransaction transaction, SingleReminder item) =>
        UpsertLifeItem(
            connection,
            transaction,
            item.Id,
            "Reminder",
            item.Title,
            item.Notes,
            "Pending",
            null,
            Resolve(item.RemindAt),
            null,
            null,
            item.CreatedAt,
            item.UpdatedAt);

    public void UpsertRecurringReminder(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecurringReminder item,
        DateTime nextOccurrence)
    {
        var occurrence = Resolve(nextOccurrence)!;
        UpsertLifeItem(
            connection,
            transaction,
            item.Id,
            "Reminder",
            item.Title,
            item.Notes,
            "Pending",
            null,
            occurrence,
            null,
            null,
            item.CreatedAt,
            item.UpdatedAt);

        var (frequency, weekdays) = item.Recurrence switch
        {
            RecurrenceKind.Daily => ("Daily", (string?)null),
            RecurrenceKind.Weekdays => ("Weekly", "1,2,3,4,5"),
            RecurrenceKind.OfficialWorkdays => ("Weekly", "1,2,3,4,5"),
            RecurrenceKind.StatutoryHolidays => ("Daily", (string?)null),
            RecurrenceKind.Weekly => ("Weekly", string.Join(",", item.Weekdays.Select(day => (int)day))),
            _ => throw new InvalidOperationException($"Unsupported recurrence kind '{item.Recurrence}'.")
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
            ON CONFLICT(id) DO UPDATE SET
                series_item_id=excluded.series_item_id,
                rule_version=recurrence_rules.rule_version+1,
                start_local_datetime=excluded.start_local_datetime,
                iana_time_zone_id=excluded.iana_time_zone_id,
                windows_time_zone_id_cache=excluded.windows_time_zone_id_cache,
                frequency=excluded.frequency,
                interval=excluded.interval,
                weekdays=excluded.weekdays,
                month_day=NULL,
                end_kind='Never',
                end_local_datetime=NULL,
                occurrence_count=NULL,
                next_occurrence_utc=excluded.next_occurrence_utc,
                row_version=recurrence_rules.row_version+1,
                updated_at=excluded.updated_at,
                deleted_at=NULL
            """;
        command.Parameters.AddWithValue("$id", $"legacy-recurring:{item.Id}:v1");
        command.Parameters.AddWithValue("$series", item.Id);
        command.Parameters.AddWithValue("$startLocal", occurrence.LocalDateTime);
        command.Parameters.AddWithValue("$iana", ianaTimeZoneId);
        command.Parameters.AddWithValue("$windows", DbValue(windowsTimeZoneId));
        command.Parameters.AddWithValue("$frequency", frequency);
        command.Parameters.AddWithValue("$weekdays", DbValue(weekdays));
        command.Parameters.AddWithValue("$nextUtc", occurrence.UtcInstant);
        command.Parameters.AddWithValue("$created", AuditDate(item.CreatedAt));
        command.Parameters.AddWithValue("$updated", AuditDate(item.UpdatedAt));
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException($"Canonical recurrence rule '{item.Id}' was not written.");
    }

    public void SetTodoCompleted(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string id,
        bool completed,
        DateTime updatedAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE life_items
            SET status=$status,completed_at_utc=$completedAt,row_version=row_version+1,updated_at=$updated
            WHERE id=$id AND kind='Todo' AND deleted_at IS NULL
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$status", completed ? "Completed" : "Pending");
        command.Parameters.AddWithValue("$completedAt", completed ? AuditDate(updatedAt) : DBNull.Value);
        command.Parameters.AddWithValue("$updated", AuditDate(updatedAt));
        command.ExecuteNonQuery();
    }

    public void SoftDelete(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string id,
        DateTime deletedAt)
    {
        var timestamp = AuditDate(deletedAt);
        using (var rules = connection.CreateCommand())
        {
            rules.Transaction = transaction;
            rules.CommandText = """
                UPDATE recurrence_rules
                SET deleted_at=$deleted,row_version=row_version+1,updated_at=$deleted
                WHERE series_item_id=$id AND deleted_at IS NULL
                """;
            rules.Parameters.AddWithValue("$id", id);
            rules.Parameters.AddWithValue("$deleted", timestamp);
            rules.ExecuteNonQuery();
        }

        using var item = connection.CreateCommand();
        item.Transaction = transaction;
        item.CommandText = """
            UPDATE life_items
            SET deleted_at=$deleted,row_version=row_version+1,updated_at=$deleted
            WHERE id=$id AND deleted_at IS NULL
            """;
        item.Parameters.AddWithValue("$id", id);
        item.Parameters.AddWithValue("$deleted", timestamp);
        item.ExecuteNonQuery();
    }

    void UpsertLifeItem(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string id,
        string kind,
        string title,
        string? notes,
        string status,
        CanonicalTime? due,
        CanonicalTime? remind,
        CanonicalTime? start,
        CanonicalTime? end,
        DateTime createdAt,
        DateTime updatedAt)
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
            ON CONFLICT(id) DO UPDATE SET
                kind=excluded.kind,title=excluded.title,notes=excluded.notes,status=excluded.status,
                row_version=life_items.row_version+1,
                due_local_datetime=excluded.due_local_datetime,due_utc_instant=excluded.due_utc_instant,
                due_iana_time_zone_id=excluded.due_iana_time_zone_id,
                due_windows_time_zone_id_cache=excluded.due_windows_time_zone_id_cache,
                due_time_semantics=excluded.due_time_semantics,
                remind_local_datetime=excluded.remind_local_datetime,remind_utc_instant=excluded.remind_utc_instant,
                remind_iana_time_zone_id=excluded.remind_iana_time_zone_id,
                remind_windows_time_zone_id_cache=excluded.remind_windows_time_zone_id_cache,
                remind_time_semantics=excluded.remind_time_semantics,
                start_local_datetime=excluded.start_local_datetime,start_utc_instant=excluded.start_utc_instant,
                start_iana_time_zone_id=excluded.start_iana_time_zone_id,
                start_windows_time_zone_id_cache=excluded.start_windows_time_zone_id_cache,
                start_time_semantics=excluded.start_time_semantics,
                end_local_datetime=excluded.end_local_datetime,end_utc_instant=excluded.end_utc_instant,
                end_iana_time_zone_id=excluded.end_iana_time_zone_id,
                end_windows_time_zone_id_cache=excluded.end_windows_time_zone_id_cache,
                end_time_semantics=excluded.end_time_semantics,
                origin_type='Local',origin_adapter=NULL,is_readonly=0,readonly_reason=NULL,
                raw_external_payload_id=NULL,updated_at=excluded.updated_at,deleted_at=NULL
            WHERE life_items.origin_type='Local' AND life_items.is_readonly=0
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
        command.Parameters.AddWithValue("$created", AuditDate(createdAt));
        command.Parameters.AddWithValue("$updated", AuditDate(updatedAt));
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException($"Canonical life item '{id}' was not written.");
    }

    CanonicalTime? Resolve(DateTime? value)
    {
        if (value is null) return null;
        var local = DateTime.SpecifyKind(value.Value, DateTimeKind.Unspecified);
        var temporal = new TemporalValue(
            local,
            null,
            ianaTimeZoneId,
            windowsTimeZoneId,
            TimeSemantics.ZonedWallClock);
        var instant = occurrenceTimeResolver.ResolveSingleLocalTime(temporal);
        return new(
            local.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
            instant.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
    }

    void AddTimeParameters(SqliteCommand command, string prefix, CanonicalTime? value)
    {
        command.Parameters.AddWithValue($"${prefix}Local", DbValue(value?.LocalDateTime));
        command.Parameters.AddWithValue($"${prefix}Utc", DbValue(value?.UtcInstant));
        command.Parameters.AddWithValue($"${prefix}Iana", DbValue(value is null ? null : ianaTimeZoneId));
        command.Parameters.AddWithValue($"${prefix}Windows", DbValue(value is null ? null : windowsTimeZoneId));
        command.Parameters.AddWithValue($"${prefix}Semantics", DbValue(value is null ? null : "ZonedWallClock"));
    }

    static string DetectLocalIanaTimeZoneId(ITimeZoneCatalog catalog)
    {
        var localId = TimeZoneInfo.Local.Id;
        if (catalog.TryResolveIana(localId, out _, out _)) return localId;
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(localId, out var ianaId) &&
            catalog.TryResolveIana(ianaId, out _, out _))
            return ianaId;
        throw new InvalidOperationException($"The local time zone '{localId}' cannot be represented as an IANA id.");
    }

    static string AuditDate(DateTime value) => new DateTimeOffset(value).ToUniversalTime()
        .ToString("O", CultureInfo.InvariantCulture);

    static object DbValue(string? value) => value is null ? DBNull.Value : value;

    sealed record CanonicalTime(string LocalDateTime, string UtcInstant);
}

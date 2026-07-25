using System.Globalization;
using Microsoft.Data.Sqlite;
using ChronoIsle.App.Services.Domain;
using ChronoIsle.App.Services.Persistence;

namespace ChronoIsle.App.Services.Sync;

public sealed record IcsImportResult(int ImportedCount, int ReadOnlyMirrorCount);
public enum ExternalMirrorOperation { View, Hide, DeleteLocalCopy, Complete, Reschedule, Decompose, WriteBack }

public static class ExternalMirrorPolicy
{
    public static bool IsAllowed(bool isReadOnly, ExternalMirrorOperation operation) => !isReadOnly ||
        operation is ExternalMirrorOperation.View or ExternalMirrorOperation.Hide or ExternalMirrorOperation.DeleteLocalCopy;
}

public sealed class IcsImportService
{
    readonly IDbWriteQueue writeQueue;
    readonly IOccurrenceTimeResolver resolver;
    readonly Func<DateTimeOffset> utcNow;

    public IcsImportService(IDbWriteQueue writeQueue, IOccurrenceTimeResolver resolver, Func<DateTimeOffset>? utcNow = null)
    {
        this.writeQueue = writeQueue ?? throw new ArgumentNullException(nameof(writeQueue));
        this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public IcsImportResult Import(string ics)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ics);
        var components = IcsCodec.Parse(ics);
        return writeQueue.Execute(uow =>
        {
            EnsurePayloadTable(uow.Connection, uow.Transaction);
            var imported = 0;
            var mirrors = 0;
            foreach (var component in components)
            {
                ImportComponent(uow.Connection, uow.Transaction, component, out var readOnly);
                imported++;
                if (readOnly) mirrors++;
            }
            return new IcsImportResult(imported, mirrors);
        });
    }

    void ImportComponent(SqliteConnection connection, SqliteTransaction transaction, IcsComponent component, out bool readOnly)
    {
        var properties = component.Properties.GroupBy(property => property.Name)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        if (!properties.TryGetValue("SUMMARY", out var summary) || string.IsNullOrWhiteSpace(summary.Value))
            throw new FormatException("VEVENT/VTODO requires SUMMARY.");

        var hasKindProperty = properties.TryGetValue("X-CHRONO-ISLE-KIND", out var kindProperty) ||
            properties.TryGetValue("X-OPEN-ISLAND-KIND", out kindProperty);
        var kind = component.Name == "VEVENT" ? "Event" :
            hasKindProperty && kindProperty!.Value.Equals("REMINDER", StringComparison.OrdinalIgnoreCase)
                ? "Reminder" : "Todo";
        var due = properties.TryGetValue("DUE", out var dueProperty) ? IcsCodec.ParseTime(dueProperty, resolver) : null;
        var hasRemindProperty = properties.TryGetValue("X-CHRONO-ISLE-REMIND", out var remindProperty) ||
            properties.TryGetValue("X-OPEN-ISLAND-REMIND", out remindProperty);
        var remind = hasRemindProperty ? IcsCodec.ParseTime(remindProperty!, resolver) : null;
        var start = properties.TryGetValue("DTSTART", out var startProperty) ? IcsCodec.ParseTime(startProperty, resolver) : null;
        var end = properties.TryGetValue("DTEND", out var endProperty) ? IcsCodec.ParseTime(endProperty, resolver) : null;
        if (kind == "Reminder") remind = due ?? throw new FormatException("Reminder VTODO requires DUE.");
        if (kind == "Event" && (start is null || end is null)) throw new FormatException("VEVENT requires DTSTART and DTEND.");

        var hasDateOnlyValue = properties.Values.Any(property =>
            property.Name is "DUE" or "DTSTART" or "DTEND" &&
            property.Parameters.TryGetValue("VALUE", out var valueType) &&
            valueType.Equals("DATE", StringComparison.OrdinalIgnoreCase));
        var recurrence = properties.TryGetValue("RRULE", out var rule) ? ParseRule(rule.Value) : null;
        var hasOccurrenceExceptions = component.Properties.Any(property => property.Name is "EXDATE" or "RDATE" or "RECURRENCE-ID");
        readOnly = hasDateOnlyValue || recurrence is { Supported: false } ||
            hasOccurrenceExceptions || recurrence is not null && PrimaryTime(kind, due, remind, start)?.Semantics != TimeSemantics.ZonedWallClock;
        var reason = hasDateOnlyValue ? "All-day VALUE=DATE items are preserved as read-only because the local model has no all-day semantic." :
            hasOccurrenceExceptions ? "ICS EXDATE/RDATE/RECURRENCE-ID exceptions are preserved as a read-only mirror." :
            readOnly ? recurrence?.Reason ?? "Recurring floating/absolute time cannot be mapped losslessly." : null;
        var payloadId = "ics-payload-" + IcsCodec.StableId(component.Raw);
        using (var payload = connection.CreateCommand())
        {
            payload.Transaction = transaction;
            payload.CommandText = "INSERT OR IGNORE INTO raw_external_payloads(id,adapter,content,created_at) VALUES($id,'ics',$content,$created)";
            payload.Parameters.AddWithValue("$id", payloadId);
            payload.Parameters.AddWithValue("$content", component.Raw);
            payload.Parameters.AddWithValue("$created", utcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            payload.ExecuteNonQuery();
        }

        var sourceUid = properties.TryGetValue("UID", out var uid) ? uid.Value : component.Raw;
        var itemId = "ics-" + IcsCodec.StableId(sourceUid + "\n" + component.Raw);
        InsertItem(connection, transaction, itemId, kind, IcsCodec.Unescape(summary.Value),
            properties.TryGetValue("DESCRIPTION", out var description) ? IcsCodec.Unescape(description.Value) : null,
            due, remind, start, end, readOnly, reason, payloadId);
        if (recurrence is { Supported: true })
            InsertRule(connection, transaction, itemId, recurrence, PrimaryTime(kind, due, remind, start)!);
    }

    void InsertItem(SqliteConnection connection, SqliteTransaction transaction, string id, string kind, string title, string? notes,
        TemporalValue? due, TemporalValue? remind, TemporalValue? start, TemporalValue? end, bool readOnly, string? reason, string payloadId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO life_items(id,kind,title,notes,status,row_version,
              due_local_datetime,due_utc_instant,due_iana_time_zone_id,due_windows_time_zone_id_cache,due_time_semantics,
              remind_local_datetime,remind_utc_instant,remind_iana_time_zone_id,remind_windows_time_zone_id_cache,remind_time_semantics,
              start_local_datetime,start_utc_instant,start_iana_time_zone_id,start_windows_time_zone_id_cache,start_time_semantics,
              end_local_datetime,end_utc_instant,end_iana_time_zone_id,end_windows_time_zone_id_cache,end_time_semantics,
              origin_type,origin_adapter,is_readonly,readonly_reason,raw_external_payload_id,created_at,updated_at,deleted_at)
            VALUES($id,$kind,$title,$notes,'Pending',1,
              $dueLocal,$dueUtc,$dueIana,$dueWindows,$dueSemantics,
              $remindLocal,$remindUtc,$remindIana,$remindWindows,$remindSemantics,
              $startLocal,$startUtc,$startIana,$startWindows,$startSemantics,
              $endLocal,$endUtc,$endIana,$endWindows,$endSemantics,
              'IcsImport','ics',$readonly,$reason,$payload,$now,$now,NULL)
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$notes", Db(notes));
        AddTime(command, "due", due); AddTime(command, "remind", remind);
        AddTime(command, "start", start); AddTime(command, "end", end);
        command.Parameters.AddWithValue("$readonly", readOnly ? 1 : 0);
        command.Parameters.AddWithValue("$reason", Db(reason));
        command.Parameters.AddWithValue("$payload", payloadId);
        command.Parameters.AddWithValue("$now", utcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    static TemporalValue? PrimaryTime(string kind, TemporalValue? due, TemporalValue? remind, TemporalValue? start) =>
        kind == "Event" ? start : kind == "Reminder" ? remind : due;

    static void EnsurePayloadTable(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "CREATE TABLE IF NOT EXISTS raw_external_payloads(id TEXT PRIMARY KEY,adapter TEXT NOT NULL,content TEXT NOT NULL,created_at TEXT NOT NULL)";
        command.ExecuteNonQuery();
    }

    static void AddTime(SqliteCommand command, string prefix, TemporalValue? value)
    {
        command.Parameters.AddWithValue("$" + prefix + "Local", Db(value?.LocalDateTime?.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture)));
        command.Parameters.AddWithValue("$" + prefix + "Utc", Db(value?.UtcInstant?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)));
        command.Parameters.AddWithValue("$" + prefix + "Iana", Db(value?.IanaTimeZoneId));
        command.Parameters.AddWithValue("$" + prefix + "Windows", Db(value?.WindowsTimeZoneIdCache));
        command.Parameters.AddWithValue("$" + prefix + "Semantics", Db(value?.Semantics.ToString()));
    }

    static object Db(object? value) => value ?? DBNull.Value;

    sealed record ParsedRule(bool Supported, string? Reason, string Frequency, int Interval, string? Weekdays, int? MonthDay, string EndKind, string? Until, int? Count);

    static ParsedRule ParseRule(string value)
    {
        var parts = value.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2)).Where(part => part.Length == 2)
            .ToDictionary(part => part[0].ToUpperInvariant(), part => part[1], StringComparer.OrdinalIgnoreCase);
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "FREQ", "INTERVAL", "BYDAY", "BYMONTHDAY", "COUNT", "UNTIL" };
        if (parts.Keys.Any(key => !known.Contains(key)) || !parts.TryGetValue("FREQ", out var frequency) ||
            frequency.ToUpperInvariant() is not ("DAILY" or "WEEKLY" or "MONTHLY"))
            return Unsupported("ICS recurrence rule is not losslessly supported.");
        var interval = parts.TryGetValue("INTERVAL", out var intervalText) && int.TryParse(intervalText, out var parsedInterval) && parsedInterval > 0 ? parsedInterval : 1;
        string? weekdays = null;
        if (parts.TryGetValue("BYDAY", out var dayText))
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                { ["SU"] = 0, ["MO"] = 1, ["TU"] = 2, ["WE"] = 3, ["TH"] = 4, ["FR"] = 5, ["SA"] = 6 };
            var days = dayText.Split(',');
            if (days.Any(day => !map.ContainsKey(day))) return Unsupported("ICS BYDAY contains unsupported values.");
            weekdays = string.Join(',', days.Select(day => map[day]));
        }
        int? monthDay = null;
        if (parts.TryGetValue("BYMONTHDAY", out var monthText))
        {
            if (!int.TryParse(monthText, out var day) || day is < 1 or > 31) return Unsupported("ICS BYMONTHDAY is invalid.");
            monthDay = day;
        }
        var endKind = parts.ContainsKey("COUNT") ? "Count" : parts.ContainsKey("UNTIL") ? "Until" : "Never";
        int? count = parts.TryGetValue("COUNT", out var countText) && int.TryParse(countText, out var parsedCount) && parsedCount > 0 ? parsedCount : null;
        string? until = null;
        if (parts.TryGetValue("UNTIL", out var untilText) && DateTime.TryParseExact(untilText,
                new[] { "yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmmss'Z'", "yyyyMMdd" }, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsedUntil))
            until = parsedUntil.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
        if (endKind == "Count" && count is null || endKind == "Until" && until is null)
            return Unsupported("ICS recurrence end condition is invalid.");
        return new(true, null, CultureInfo.InvariantCulture.TextInfo.ToTitleCase(frequency.ToLowerInvariant()), interval, weekdays, monthDay, endKind, until, count);
    }

    static ParsedRule Unsupported(string reason) => new(false, reason, "Daily", 1, null, null, "Never", null, null);

    void InsertRule(SqliteConnection connection, SqliteTransaction transaction, string itemId, ParsedRule rule, TemporalValue primary)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO recurrence_rules(id,series_item_id,rule_version,start_local_datetime,iana_time_zone_id,
              windows_time_zone_id_cache,frequency,interval,weekdays,month_day,end_kind,end_local_datetime,
              occurrence_count,next_occurrence_utc,row_version,created_at,updated_at,deleted_at)
            VALUES($id,$series,1,$start,$iana,$windows,$frequency,$interval,$weekdays,$monthDay,$endKind,$until,
              $count,$next,1,$now,$now,NULL)
            """;
        command.Parameters.AddWithValue("$id", itemId + ":rrule:v1");
        command.Parameters.AddWithValue("$series", itemId);
        command.Parameters.AddWithValue("$start", primary.LocalDateTime!.Value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$iana", primary.IanaTimeZoneId!);
        command.Parameters.AddWithValue("$windows", Db(primary.WindowsTimeZoneIdCache));
        command.Parameters.AddWithValue("$frequency", rule.Frequency);
        command.Parameters.AddWithValue("$interval", rule.Interval);
        command.Parameters.AddWithValue("$weekdays", Db(rule.Weekdays));
        command.Parameters.AddWithValue("$monthDay", Db(rule.MonthDay));
        command.Parameters.AddWithValue("$endKind", rule.EndKind);
        command.Parameters.AddWithValue("$until", Db(rule.Until));
        command.Parameters.AddWithValue("$count", Db(rule.Count));
        command.Parameters.AddWithValue("$next", primary.UtcInstant!.Value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$now", utcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }
}

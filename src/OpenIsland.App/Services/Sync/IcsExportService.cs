using System.Globalization;
using Microsoft.Data.Sqlite;
using OpenIsland.App.Services.Domain;
using OpenIsland.App.Services.Persistence;

namespace OpenIsland.App.Services.Sync;

public sealed class IcsExportService
{
    readonly SqliteConnectionFactory connectionFactory;

    public IcsExportService(SqliteConnectionFactory connectionFactory) =>
        this.connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    public string Export()
    {
        using var connection = connectionFactory.OpenConnection();
        var lines = new List<string> { "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//Open Island//Life Assistant//EN", "CALSCALE:GREGORIAN" };
        foreach (var zone in IcsExportTimeZoneReader.Read(connection)) lines.AddRange(IcsTimeZoneWriter.Build(zone, DateTimeOffset.Now.Year));
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,kind,title,notes,
              due_local_datetime,due_utc_instant,due_iana_time_zone_id,due_windows_time_zone_id_cache,due_time_semantics,
              remind_local_datetime,remind_utc_instant,remind_iana_time_zone_id,remind_windows_time_zone_id_cache,remind_time_semantics,
              start_local_datetime,start_utc_instant,start_iana_time_zone_id,start_windows_time_zone_id_cache,start_time_semantics,
              end_local_datetime,end_utc_instant,end_iana_time_zone_id,end_windows_time_zone_id_cache,end_time_semantics,
              updated_at
            FROM life_items WHERE deleted_at IS NULL AND is_readonly=0 ORDER BY created_at,id
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read()) AppendItem(lines, connection, reader);
        lines.Add("END:VCALENDAR");
        return string.Join("\r\n", lines.SelectMany(IcsCodec.Fold)) + "\r\n";
    }

    static void AppendItem(List<string> lines, SqliteConnection connection, SqliteDataReader reader)
    {
        var id = reader.GetString(0);
        var kind = reader.GetString(1);
        var component = kind == "Event" ? "VEVENT" : "VTODO";
        lines.Add("BEGIN:" + component);
        lines.Add("UID:" + IcsCodec.Escape(id) + "@open-island.local");
        lines.Add("DTSTAMP:" + ParseInstant(reader.GetString(24)).UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
        lines.Add("SUMMARY:" + IcsCodec.Escape(reader.GetString(2)));
        if (!reader.IsDBNull(3)) lines.Add("DESCRIPTION:" + IcsCodec.Escape(reader.GetString(3)));
        lines.Add("X-OPEN-ISLAND-KIND:" + kind.ToUpperInvariant());

        var due = ReadTime(reader, 4);
        var remind = ReadTime(reader, 9);
        var start = ReadTime(reader, 14);
        var end = ReadTime(reader, 19);
        if (kind == "Event")
        {
            lines.Add(IcsCodec.FormatTime("DTSTART", start!));
            lines.Add(IcsCodec.FormatTime("DTEND", end!));
        }
        else if (kind == "Reminder")
            lines.Add(IcsCodec.FormatTime("DUE", remind!));
        else if (due is not null)
            lines.Add(IcsCodec.FormatTime("DUE", due));
        if (remind is not null && kind != "Reminder")
            lines.Add(IcsCodec.FormatTime("X-OPEN-ISLAND-REMIND", remind));

        var recurrence = ReadRecurrence(connection, id);
        if (recurrence is not null) lines.Add("RRULE:" + recurrence);
        lines.Add("END:" + component);
    }

    static string? ReadRecurrence(SqliteConnection connection, string itemId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT frequency,interval,weekdays,month_day,end_kind,end_local_datetime,occurrence_count
            FROM recurrence_rules WHERE series_item_id=$id AND deleted_at IS NULL ORDER BY rule_version DESC LIMIT 1
            """;
        command.Parameters.AddWithValue("$id", itemId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var frequency = reader.GetString(0).ToUpperInvariant();
        if (frequency is not ("DAILY" or "WEEKLY" or "MONTHLY")) return null;
        var parts = new List<string> { "FREQ=" + frequency, "INTERVAL=" + reader.GetInt32(1) };
        if (!reader.IsDBNull(2))
        {
            var names = new[] { "SU", "MO", "TU", "WE", "TH", "FR", "SA" };
            parts.Add("BYDAY=" + string.Join(',', reader.GetString(2).Split(',').Select(value => names[int.Parse(value, CultureInfo.InvariantCulture)])));
        }
        if (!reader.IsDBNull(3)) parts.Add("BYMONTHDAY=" + reader.GetInt32(3));
        var endKind = reader.GetString(4);
        if (endKind == "Until" && !reader.IsDBNull(5))
            parts.Add("UNTIL=" + DateTime.Parse(reader.GetString(5), CultureInfo.InvariantCulture).ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture));
        else if (endKind == "Count" && !reader.IsDBNull(6)) parts.Add("COUNT=" + reader.GetInt32(6));
        return string.Join(';', parts);
    }

    static TemporalValue? ReadTime(SqliteDataReader reader, int index)
    {
        if (reader.IsDBNull(index + 4)) return null;
        var semantics = Enum.Parse<TimeSemantics>(reader.GetString(index + 4));
        DateTime? local = reader.IsDBNull(index) ? null : DateTime.SpecifyKind(DateTime.Parse(reader.GetString(index), CultureInfo.InvariantCulture), DateTimeKind.Unspecified);
        DateTimeOffset? utc = reader.IsDBNull(index + 1) ? null : ParseInstant(reader.GetString(index + 1));
        return new(local, utc, reader.IsDBNull(index + 2) ? null : reader.GetString(index + 2),
            reader.IsDBNull(index + 3) ? null : reader.GetString(index + 3), semantics);
    }

    static DateTimeOffset ParseInstant(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

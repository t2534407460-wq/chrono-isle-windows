using Microsoft.Data.Sqlite;

namespace ChronoIsle.App.Services;

public sealed partial class LifeDataService
{
    /// <summary>
    /// Long-term items are canonical-only items. They may be unscheduled, are never treated as
    /// recurring rules, and remain active until the user completes, deletes, or archives them.
    /// </summary>
    public IReadOnlyList<ManagedLifeItem> LongTermItems()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT id,title,notes,status,
                   COALESCE(due_local_datetime,remind_local_datetime,due_utc_instant,remind_utc_instant)
            FROM life_items
            WHERE item_type='LongTerm' AND deleted_at IS NULL
              AND status NOT IN ('Cancelled','Ignored')
            ORDER BY status='Completed',COALESCE(due_utc_instant,remind_utc_instant) IS NULL,
                     COALESCE(due_utc_instant,remind_utc_instant),title
            """;
        using var reader = command.ExecuteReader();
        var values = new List<ManagedLifeItem>();
        while (reader.Read())
            values.Add(new(
                reader.GetString(0),
                "long_term",
                reader.GetString(1),
                Text(reader, 2),
                LongTermDate(reader, 4),
                string.Equals(reader.GetString(3), "Completed", StringComparison.Ordinal),
                "长期事项"));
        return values;
    }

    IReadOnlyList<AgendaItem> LongTermAgendaFor(DateTime day)
    {
        var start = day.Date;
        var end = start.AddDays(1);
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT id,title,notes,status,
                   COALESCE(due_local_datetime,remind_local_datetime),
                   remind_local_datetime
            FROM life_items
            WHERE item_type='LongTerm' AND deleted_at IS NULL
              AND status NOT IN ('Completed','Cancelled','Ignored')
              AND COALESCE(due_local_datetime,remind_local_datetime) >= $start
              AND COALESCE(due_local_datetime,remind_local_datetime) < $end
            ORDER BY COALESCE(due_local_datetime,remind_local_datetime),title
            """;
        command.Parameters.AddWithValue("$start", start.ToString("O"));
        command.Parameters.AddWithValue("$end", end.ToString("O"));
        using var reader = command.ExecuteReader();
        var values = new List<AgendaItem>();
        while (reader.Read())
        {
            var scheduled = LongTermDate(reader, 4);
            if (scheduled is null) continue;
            values.Add(new(
                reader.GetString(0),
                "long_term",
                reader.GetString(1),
                Text(reader, 2),
                scheduled.Value,
                null,
                LongTermDate(reader, 5),
                false));
        }
        return values;
    }

    IReadOnlyList<AgendaItem> LongTermReminderItems()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT id,title,notes,remind_local_datetime
            FROM life_items
            WHERE item_type='LongTerm' AND deleted_at IS NULL
              AND status NOT IN ('Completed','Cancelled','Ignored')
              AND remind_local_datetime IS NOT NULL
            ORDER BY remind_utc_instant,title
            """;
        using var reader = command.ExecuteReader();
        var values = new List<AgendaItem>();
        while (reader.Read())
        {
            var remindAt = LongTermDate(reader, 3);
            if (remindAt is null) continue;
            values.Add(new(
                reader.GetString(0),
                "long_term",
                reader.GetString(1),
                Text(reader, 2),
                remindAt.Value,
                null,
                remindAt,
                false));
        }
        return values;
    }


    AgendaItem? NextLongTermAgendaItem(DateTime now, bool remindersOnly = false)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = $"""
            SELECT id,title,notes,
                   {(remindersOnly ? "remind_local_datetime" : "COALESCE(due_local_datetime,remind_local_datetime)")},
                   remind_local_datetime
            FROM life_items
            WHERE item_type='LongTerm' AND deleted_at IS NULL
              AND status NOT IN ('Completed','Cancelled','Ignored')
              AND {(remindersOnly ? "remind_local_datetime" : "COALESCE(due_local_datetime,remind_local_datetime)")} >= $now
            ORDER BY {(remindersOnly ? "remind_utc_instant" : "COALESCE(due_utc_instant,remind_utc_instant)")},title
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var scheduled = LongTermDate(reader, 3);
        if (scheduled is null) return null;
        return new(
            reader.GetString(0),
            "long_term",
            reader.GetString(1),
            Text(reader, 2),
            scheduled.Value,
            null,
            LongTermDate(reader, 4),
            false);
    }

    AgendaItem? FindLongTermItem(string id)
    {
        var managed = LongTermItems().FirstOrDefault(item => item.Id == id);
        return managed is null
            ? null
            : new(
                managed.Id,
                managed.Kind,
                managed.Title,
                managed.Notes,
                managed.ScheduledAt ?? localNow(),
                null,
                managed.ScheduledAt,
                managed.IsCompleted);
    }

    static DateTime? LongTermDate(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) || !DateTime.TryParse(reader.GetString(index), out var value)
            ? null
            : value;
}

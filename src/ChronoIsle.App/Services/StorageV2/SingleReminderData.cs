using Microsoft.Data.Sqlite;

namespace ChronoIsle.App.Services;

public sealed partial class LifeDataService
{
    static void InitializeSingleReminders(SqliteConnection db, SqliteTransaction transaction)
    {
        Execute(db, transaction, """
            CREATE TABLE IF NOT EXISTS single_reminders(
                id TEXT PRIMARY KEY,title TEXT NOT NULL,notes TEXT,remind_at TEXT NOT NULL,
                notified_at TEXT,created_at TEXT NOT NULL,updated_at TEXT NOT NULL);
            """);
    }

    public SingleReminder SaveReminder(string title, string? notes, DateTime remindAt, string? id = null)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("A reminder title is required.", nameof(title));
        var now = localNow();
        var item = new SingleReminder(id ?? Guid.NewGuid().ToString("N"), title.Trim(), notes, remindAt, null, now, now);
        writeQueue.Execute(unitOfWork =>
        {
            using var command = unitOfWork.Connection.CreateCommand();
            command.Transaction = unitOfWork.Transaction;
            command.CommandText = """
                INSERT INTO single_reminders(id,title,notes,remind_at,notified_at,created_at,updated_at)
                VALUES($id,$title,$notes,$remind,NULL,$created,$updated)
                ON CONFLICT(id) DO UPDATE SET title=$title,notes=$notes,remind_at=$remind,
                    notified_at=NULL,updated_at=$updated
                """;
            command.Parameters.AddWithValue("$id", item.Id);
            command.Parameters.AddWithValue("$title", item.Title);
            command.Parameters.AddWithValue("$notes", (object?)item.Notes ?? DBNull.Value);
            command.Parameters.AddWithValue("$remind", item.RemindAt.ToString("O"));
            command.Parameters.AddWithValue("$created", item.CreatedAt.ToString("O"));
            command.Parameters.AddWithValue("$updated", item.UpdatedAt.ToString("O"));
            command.ExecuteNonQuery();
            canonicalWriter.UpsertReminder(unitOfWork.Connection, unitOfWork.Transaction, item);
        });
        RaiseAgendaChanged();
        return item;
    }

    IReadOnlyList<AgendaItem> SingleReminderAgendaFor(DateTime day)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT id,title,notes,remind_at,notified_at FROM single_reminders
            WHERE remind_at >= $start AND remind_at < $end ORDER BY remind_at,title
            """;
        command.Parameters.AddWithValue("$start", day.Date.ToString("O"));
        command.Parameters.AddWithValue("$end", day.Date.AddDays(1).ToString("O"));
        using var reader = command.ExecuteReader();
        var values = new List<AgendaItem>();
        while (reader.Read())
        {
            var at = ReadDate(reader, 3);
            values.Add(new AgendaItem(reader.GetString(0), "reminder", reader.GetString(1), Text(reader, 2), at, null, at, false));
        }
        return values;
    }

    IReadOnlyList<AgendaItem> NextSingleReminderItems(DateTime now)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT id,title,notes,remind_at FROM single_reminders
            WHERE remind_at >= $now ORDER BY remind_at,title
            """;
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        using var reader = command.ExecuteReader();
        var values = new List<AgendaItem>();
        while (reader.Read())
        {
            var at = ReadDate(reader, 3);
            values.Add(new AgendaItem(reader.GetString(0), "reminder", reader.GetString(1), Text(reader, 2), at, null, at, false));
        }
        return values;
    }

    IReadOnlyList<AgendaItem> SingleReminderItems()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT id,title,notes,remind_at FROM single_reminders WHERE notified_at IS NULL ORDER BY remind_at,title";
        using var reader = command.ExecuteReader();
        var values = new List<AgendaItem>();
        while (reader.Read())
        {
            var at = ReadDate(reader, 3);
            values.Add(new AgendaItem(reader.GetString(0), "reminder", reader.GetString(1), Text(reader, 2), at, null, at, false));
        }
        return values;
    }

    IReadOnlyList<ManagedLifeItem> ManagedSingleReminders()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT id,title,notes,remind_at FROM single_reminders ORDER BY remind_at";
        using var reader = command.ExecuteReader();
        var values = new List<ManagedLifeItem>();
        while (reader.Read())
            values.Add(new ManagedLifeItem(reader.GetString(0), "reminder", reader.GetString(1), Text(reader, 2), ReadDate(reader, 3), false, null));
        return values;
    }
}


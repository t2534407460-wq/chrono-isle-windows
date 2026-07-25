using Microsoft.Data.Sqlite;

namespace ChronoIsle.App.Services;

public sealed partial class LifeDataService
{
    static void InitializeRecurring(SqliteConnection db)
    {
        Execute(db, """
            CREATE TABLE IF NOT EXISTS recurring_reminders(
                id TEXT PRIMARY KEY,title TEXT NOT NULL,notes TEXT,reminder_time TEXT NOT NULL,
                recurrence TEXT NOT NULL,weekdays TEXT,last_notified_at TEXT,
                created_at TEXT NOT NULL,updated_at TEXT NOT NULL);
            """);
    }

    public IReadOnlyList<RecurringReminder> RecurringReminders()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT id,title,notes,reminder_time,recurrence,weekdays,last_notified_at,created_at,updated_at
            FROM recurring_reminders ORDER BY reminder_time,title
            """;
        using var reader = command.ExecuteReader();
        var values = new List<RecurringReminder>();
        while (reader.Read()) values.Add(ReadRecurring(reader));
        return values;
    }

    public RecurringReminder SaveRecurringReminder(
        string title,
        string? notes,
        TimeOnly reminderTime,
        RecurrenceKind recurrence,
        IEnumerable<DayOfWeek>? weekdays,
        string? id = null)
    {
        var normalizedDays = recurrence == RecurrenceKind.Weekly
            ? (weekdays ?? []).Distinct().OrderBy(x => x).ToList()
            : [];
        if (recurrence == RecurrenceKind.Weekly && normalizedDays.Count == 0)
            throw new InvalidOperationException("每周提醒必须指定星期。");

        var now = DateTime.Now;
        var item = new RecurringReminder(
            id ?? Guid.NewGuid().ToString("N"),
            title.Trim(),
            notes,
            reminderTime,
            recurrence,
            normalizedDays,
            null,
            now,
            now);
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO recurring_reminders(id,title,notes,reminder_time,recurrence,weekdays,last_notified_at,created_at,updated_at)
            VALUES($id,$title,$notes,$time,$recurrence,$weekdays,NULL,$created,$updated)
            ON CONFLICT(id) DO UPDATE SET title=$title,notes=$notes,reminder_time=$time,recurrence=$recurrence,
                weekdays=$weekdays,last_notified_at=NULL,updated_at=$updated
            """;
        BindRecurring(command, item);
        command.ExecuteNonQuery();
        RaiseAgendaChanged();
        return item;
    }

    public IReadOnlyList<AgendaItem> RecurringAgendaFor(DateTime day) =>
        RecurringReminders().Select(reminder => OccurrenceOn(reminder, day)).Where(item => item is not null).Cast<AgendaItem>().ToList();

    public AgendaItem? OccurrenceOn(RecurringReminder reminder, DateTime day)
    {
        if (!Matches(reminder, day.Date)) return null;
        var startsAt = day.Date.Add(reminder.ReminderTime.ToTimeSpan());
        return new(reminder.Id, "recurring", reminder.Title, reminder.Notes, startsAt, null, startsAt, false, true);
    }

    public AgendaItem? NextOccurrence(RecurringReminder reminder, DateTime after)
    {
        for (var day = after.Date; day <= after.Date.AddDays(8); day = day.AddDays(1))
        {
            var occurrence = OccurrenceOn(reminder, day);
            if (occurrence is not null && occurrence.StartsAt > after) return occurrence;
        }
        return null;
    }

    IReadOnlyList<AgendaItem> NextRecurringAgendaItems(DateTime now) =>
        RecurringReminders().Select(reminder => NextOccurrence(reminder, now)).Where(item => item is not null).Cast<AgendaItem>().ToList();

    IReadOnlyList<AgendaItem> NextRecurringReminderItems(DateTime now) => NextRecurringAgendaItems(now);

    IReadOnlyList<AgendaItem> ClaimDueRecurringReminders(SqliteConnection db, DateTime now)
    {
        var due = new List<AgendaItem>();
        foreach (var reminder in RecurringReminders())
        {
            var occurrence = OccurrenceOn(reminder, now.Date);
            if (occurrence?.RemindAt is null || occurrence.RemindAt > now ||
                reminder.LastNotifiedAt is not null && reminder.LastNotifiedAt >= occurrence.RemindAt)
                continue;

            using var command = db.CreateCommand();
            command.CommandText = """
                UPDATE recurring_reminders SET last_notified_at=$occurrence,updated_at=$updated
                WHERE id=$id AND (last_notified_at IS NULL OR last_notified_at < $occurrence)
                """;
            command.Parameters.AddWithValue("$id", reminder.Id);
            command.Parameters.AddWithValue("$occurrence", occurrence.RemindAt.Value.ToString("O"));
            command.Parameters.AddWithValue("$updated", now.ToString("O"));
            if (command.ExecuteNonQuery() == 1) due.Add(occurrence);
        }
        return due;
    }

    public AgendaItem? FindAgendaItem(string kind, string id, DateTime? day = null)
    {
        var date = day?.Date ?? DateTime.Today;
        if (string.Equals(kind, "recurring", StringComparison.OrdinalIgnoreCase))
        {
            var reminder = RecurringReminders().FirstOrDefault(x => x.Id == id);
            return reminder is null ? null : OccurrenceOn(reminder, date) ?? NextOccurrence(reminder, DateTime.Now);
        }
        return AgendaFor(date).FirstOrDefault(item =>
            item.Id == id && string.Equals(item.Kind, kind, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<ManagedLifeItem> ManagedItems()
    {
        var values = Todos()
            .Select(todo => new ManagedLifeItem(todo.Id, "todo", todo.Title, todo.Notes, todo.DueAt, todo.IsCompleted, null))
            .ToList();
        using (var db = Open())
        using (var command = db.CreateCommand())
        {
            command.CommandText = "SELECT id,title,notes,start_at FROM calendar_events ORDER BY start_at";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                values.Add(new ManagedLifeItem(reader.GetString(0), "event", reader.GetString(1), Text(reader, 2), ReadDate(reader, 3), false, null));
        }

        foreach (var reminder in RecurringReminders())
        {
            var next = NextOccurrence(reminder, DateTime.Now);
            values.Add(new ManagedLifeItem(
                reminder.Id,
                "recurring",
                reminder.Title,
                reminder.Notes,
                next?.StartsAt,
                false,
                AssistantActionService.RecurrenceText(reminder.Recurrence, reminder.Weekdays)));
        }
        values.AddRange(ManagedSingleReminders());
        return values.OrderBy(x => x.ScheduledAt is null).ThenBy(x => x.ScheduledAt).ThenBy(x => x.Title).ToList();
    }

    public int DeleteAgendaItems(IEnumerable<AgendaItem> items)
    {
        var targets = items.Select(item => (item.Kind, item.Id))
            .Where(item => item.Kind is "todo" or "event" or "recurring" or "reminder")
            .Distinct()
            .ToList();
        if (targets.Count == 0) return 0;

        using var db = Open();
        using var transaction = db.BeginTransaction();
        var deleted = 0;
        foreach (var target in targets)
        {
            using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = target.Kind switch
            {
                "todo" => "DELETE FROM todos WHERE id=$id",
                "event" => "DELETE FROM calendar_events WHERE id=$id",
                "recurring" => "DELETE FROM recurring_reminders WHERE id=$id",
                "reminder" => "DELETE FROM single_reminders WHERE id=$id",
                _ => throw new InvalidOperationException("不支持删除此事项。")
            };
            command.Parameters.AddWithValue("$id", target.Id);
            deleted += command.ExecuteNonQuery();
        }
        transaction.Commit();
        RaiseAgendaChanged();
        return deleted;
    }

    static RecurringReminder ReadRecurring(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        Text(reader, 2),
        TimeOnly.Parse(reader.GetString(3)),
        Enum.Parse<RecurrenceKind>(reader.GetString(4), true),
        ReadWeekdays(Text(reader, 5)),
        Date(reader, 6),
        ReadDate(reader, 7),
        ReadDate(reader, 8));

    static IReadOnlyList<DayOfWeek> ReadWeekdays(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => int.TryParse(x, out var day) && Enum.IsDefined(typeof(DayOfWeek), day) ? (DayOfWeek)day : (DayOfWeek?)null)
            .Where(x => x is not null)
            .Select(x => x!.Value)
            .Distinct()
            .OrderBy(x => x)
            .ToList();
    }

    static bool Matches(RecurringReminder reminder, DateTime day) => reminder.Recurrence switch
    {
        RecurrenceKind.Daily => true,
        RecurrenceKind.Weekdays => day.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday,
        RecurrenceKind.Weekly => reminder.Weekdays.Contains(day.DayOfWeek),
        _ => false
    };

    static void BindRecurring(SqliteCommand command, RecurringReminder item)
    {
        command.Parameters.AddWithValue("$id", item.Id);
        command.Parameters.AddWithValue("$title", item.Title);
        command.Parameters.AddWithValue("$notes", (object?)item.Notes ?? DBNull.Value);
        command.Parameters.AddWithValue("$time", item.ReminderTime.ToString("HH:mm"));
        command.Parameters.AddWithValue("$recurrence", item.Recurrence.ToString());
        command.Parameters.AddWithValue("$weekdays", string.Join(",", item.Weekdays.Select(x => (int)x)));
        command.Parameters.AddWithValue("$created", item.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated", item.UpdatedAt.ToString("O"));
    }
}
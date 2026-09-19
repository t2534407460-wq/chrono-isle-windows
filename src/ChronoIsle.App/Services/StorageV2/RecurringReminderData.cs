using Microsoft.Data.Sqlite;

using ChronoIsle.App.Services.Domain;
using ChronoIsle.App.Services.Scheduling;
namespace ChronoIsle.App.Services;

public sealed partial class LifeDataService
{
    static readonly ChinaStatutoryHolidayCalendar statutoryHolidayCalendar = new();

    static void InitializeRecurring(SqliteConnection db, SqliteTransaction transaction)
    {
        Execute(db, transaction, """
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
            SELECT r.id,r.title,r.notes,r.reminder_time,r.recurrence,r.weekdays,r.last_notified_at,r.created_at,r.updated_at,
                   (SELECT rr.start_local_datetime FROM recurrence_rules rr JOIN life_items li ON li.id=rr.series_item_id
                    WHERE rr.series_item_id=r.id AND rr.deleted_at IS NULL AND li.origin_adapter='assistant_command'
                    ORDER BY rr.rule_version DESC LIMIT 1)
            FROM recurring_reminders r ORDER BY r.reminder_time,r.title
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
            throw new InvalidOperationException("Weekly reminders require at least one weekday.");

        var now = localNow();
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
        var nextOccurrence = NextOccurrence(item, now)
            ?? throw new InvalidOperationException("A next recurring occurrence could not be generated.");
        writeQueue.Execute(unitOfWork =>
        {
            using var command = unitOfWork.Connection.CreateCommand();
            command.Transaction = unitOfWork.Transaction;
            command.CommandText = """
                INSERT INTO recurring_reminders(id,title,notes,reminder_time,recurrence,weekdays,last_notified_at,created_at,updated_at)
                VALUES($id,$title,$notes,$time,$recurrence,$weekdays,NULL,$created,$updated)
                ON CONFLICT(id) DO UPDATE SET title=$title,notes=$notes,reminder_time=$time,recurrence=$recurrence,
                    weekdays=$weekdays,last_notified_at=NULL,updated_at=$updated
                """;
            BindRecurring(command, item);
            command.ExecuteNonQuery();
            canonicalWriter.UpsertRecurringReminder(
                unitOfWork.Connection,
                unitOfWork.Transaction,
                item,
                nextOccurrence.StartsAt);
        });
        RaiseAgendaChanged();
        return item;
    }

    public IReadOnlyList<AgendaItem> RecurringAgendaFor(DateTime day) =>
        RecurringReminders().Select(reminder => OccurrenceOn(reminder, day)).Where(item => item is not null).Cast<AgendaItem>().ToList();

    /// <summary>Skips only the supplied recurring occurrence; the recurrence rule is unchanged.</summary>
    public void SkipRecurringOccurrence(AgendaItem item) => SaveRecurringOccurrenceOverride(item, OccurrenceOverrideType.Skip, null);

    /// <summary>Moves only the supplied recurring occurrence into a one-off reminder.</summary>
    public AgendaItem RescheduleRecurringOccurrence(AgendaItem item, DateTime remindAt)
    {
        if (remindAt <= localNow()) throw new ArgumentOutOfRangeException(nameof(remindAt));
        SaveRecurringOccurrenceOverride(item, OccurrenceOverrideType.Reschedule, remindAt);
        var saved = SaveReminder($"改单次提醒：{item.Title}", item.Notes, remindAt);
        return new AgendaItem(saved.Id, "reminder", saved.Title, saved.Notes, saved.RemindAt, null, saved.RemindAt, false);
    }

    void SaveRecurringOccurrenceOverride(AgendaItem item, OccurrenceOverrideType type, DateTime? rescheduledAt)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Kind != "recurring") throw new InvalidOperationException("Only recurring occurrences can be overridden.");

        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT rule_version,iana_time_zone_id
            FROM recurrence_rules
            WHERE series_item_id=$series AND deleted_at IS NULL
            ORDER BY rule_version DESC LIMIT 1
            """;
        command.Parameters.AddWithValue("$series", item.Id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("The recurrence rule no longer exists.");

        var version = reader.GetInt32(0);
        var zone = reader.GetString(1);
        var resolver = new OccurrenceTimeResolver(new SystemTimeZoneCatalog());
        var originalUtc = resolver.ResolveRecurringOccurrence(item.StartsAt, zone);
        var key = new OccurrenceOverrideKey(item.Id, version, item.StartsAt, originalUtc, zone);
        var rescheduledUtc = rescheduledAt is null ? (DateTimeOffset?)null : resolver.ResolveRecurringOccurrence(rescheduledAt.Value, zone);
        new OccurrenceOverrideStore(writeQueue).Save(new OccurrenceOverride(
            key, type, null, rescheduledUtc, new DateTimeOffset(localNow())));
    }
    public AgendaItem? OccurrenceOn(RecurringReminder reminder, DateTime day)
    {
        if (reminder.StartsAt is { } starts && day.Date < starts.Date || !Matches(reminder, day.Date)) return null;
        var startsAt = day.Date.Add(reminder.ReminderTime.ToTimeSpan());
        return new(reminder.Id, "recurring", reminder.Title, reminder.Notes, startsAt, null, startsAt, false, true);
    }

    public AgendaItem? NextOccurrence(RecurringReminder reminder, DateTime after)
    {
        var horizon = reminder.Recurrence == RecurrenceKind.StatutoryHolidays ? 550 : 8;
        var firstDay = reminder.StartsAt is { } starts && starts.Date > after.Date ? starts.Date : after.Date;
        for (var day = firstDay; day <= firstDay.AddDays(horizon); day = day.AddDays(1))
        {
            var occurrence = OccurrenceOn(reminder, day);
            if (occurrence is not null && occurrence.StartsAt > after) return occurrence;
        }
        return null;
    }

    IReadOnlyList<AgendaItem> NextRecurringAgendaItems(DateTime now) =>
        RecurringReminders().Select(reminder => NextOccurrence(reminder, now)).Where(item => item is not null).Cast<AgendaItem>().ToList();

    IReadOnlyList<AgendaItem> NextRecurringReminderItems(DateTime now) => NextRecurringAgendaItems(now);

    IReadOnlyList<AgendaItem> ClaimDueRecurringReminders(
        SqliteConnection db,
        SqliteTransaction transaction,
        DateTime now,
        IReadOnlyList<RecurringReminder> reminders)
    {
        var due = new List<AgendaItem>();
        foreach (var reminder in reminders)
        {
            var occurrence = OccurrenceOn(reminder, now.Date);
            if (occurrence?.RemindAt is null || occurrence.RemindAt > now ||
                reminder.LastNotifiedAt is not null && reminder.LastNotifiedAt >= occurrence.RemindAt)
                continue;

            using var command = db.CreateCommand();
            command.Transaction = transaction;
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
            return reminder is null ? null : OccurrenceOn(reminder, date) ?? NextOccurrence(reminder, localNow());
        }
        if (string.Equals(kind, "long_term", StringComparison.OrdinalIgnoreCase))
            return FindLongTermItem(id);
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
            var next = NextOccurrence(reminder, localNow());
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
        values.AddRange(LongTermItems());
        return values.OrderBy(x => x.ScheduledAt is null).ThenBy(x => x.ScheduledAt).ThenBy(x => x.Title).ToList();
    }

    public int DeleteAgendaItems(IEnumerable<AgendaItem> items)
    {
        var targets = items.Select(item => (item.Kind, item.Id))
            .Where(item => item.Kind is "todo" or "event" or "recurring" or "reminder" or "long_term")
            .Distinct()
            .ToList();
        if (targets.Count == 0) return 0;

        var deletedAt = localNow();
        var deleted = writeQueue.Execute(unitOfWork =>
        {
            var count = 0;
            foreach (var target in targets)
            {
                using var command = unitOfWork.Connection.CreateCommand();
                command.Transaction = unitOfWork.Transaction;
                command.CommandText = target.Kind switch
                {
                    "todo" => "DELETE FROM todos WHERE id=$id",
                    "event" => "DELETE FROM calendar_events WHERE id=$id",
                    "recurring" => "DELETE FROM recurring_reminders WHERE id=$id",
                    "reminder" => "DELETE FROM single_reminders WHERE id=$id",
                    "long_term" => "UPDATE life_items SET status='Cancelled',deleted_at=$deletedAt,updated_at=$deletedAt,row_version=row_version+1 WHERE id=$id AND item_type='LongTerm' AND deleted_at IS NULL",
                    _ => throw new InvalidOperationException("This item kind cannot be deleted.")
                };
                command.Parameters.AddWithValue("$id", target.Id);
                command.Parameters.AddWithValue("$deletedAt", deletedAt.ToString("O"));
                var affected = command.ExecuteNonQuery();
                count += affected;
                if (affected == 1 && target.Kind != "long_term")
                    canonicalWriter.SoftDelete(unitOfWork.Connection, unitOfWork.Transaction, target.Id, deletedAt);
            }
            return count;
        });
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
        ReadDate(reader, 8),
        reader.FieldCount > 9 ? Date(reader, 9) : null);

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
        RecurrenceKind.OfficialWorkdays => !statutoryHolidayCalendar.IsRestDay(day),
        RecurrenceKind.StatutoryHolidays => statutoryHolidayCalendar.IsRestDay(day),
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


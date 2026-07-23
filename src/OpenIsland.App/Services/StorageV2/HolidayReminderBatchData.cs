using Microsoft.Data.Sqlite;

namespace OpenIsland.App.Services;

public sealed partial class LifeDataService
{
    public HolidayReminderTargetSet StatutoryHolidayReminderTargets(ChinaStatutoryHolidayCalendar holidays)
    {
        var candidates = ReminderItems()
            .Where(item => item.RemindAt is not null && holidays.Get(item.RemindAt.Value).IsHoliday)
            .ToList();

        var targets = candidates
            .Where(item => item.Kind is "todo" or "event" or "reminder")
            .GroupBy(item => (item.Kind, item.Id))
            .Select(group => group.First())
            .Select(item => new HolidayReminderTarget(item.Id, item.Kind, item.Title, item.RemindAt!.Value))
            .ToList();

        return new HolidayReminderTargetSet(
            targets,
            candidates.Count(item => item.Kind == "recurring"));
    }

    public HolidayReminderBatchApplyResult RescheduleHolidayReminderTargets(
        IReadOnlyList<HolidayReminderTarget> targets,
        TimeOnly reminderTime)
    {
        var uniqueTargets = targets
            .GroupBy(target => (target.Kind, target.Id))
            .Select(group => group.Single())
            .ToList();
        if (uniqueTargets.Count == 0)
            throw new InvalidOperationException("没有可修改的单次提醒。");

        var now = localNow();
        writeQueue.Execute(unitOfWork =>
        {
            foreach (var target in uniqueTargets)
            {
                var rescheduledAt = target.OriginalRemindAt.Date.Add(reminderTime.ToTimeSpan());
                switch (target.Kind)
                {
                    case "todo":
                        RescheduleTodoReminder(unitOfWork.Connection, unitOfWork.Transaction, target, rescheduledAt, now);
                        break;
                    case "event":
                        RescheduleEventReminder(unitOfWork.Connection, unitOfWork.Transaction, target, rescheduledAt, now);
                        break;
                    case "reminder":
                        RescheduleSingleReminder(unitOfWork.Connection, unitOfWork.Transaction, target, rescheduledAt, now);
                        break;
                    default:
                        throw new InvalidOperationException("确认内容包含不支持的提醒类型。");
                }
            }
        });
        RaiseAgendaChanged();
        return new HolidayReminderBatchApplyResult(uniqueTargets.Count);
    }

    void RescheduleTodoReminder(
        SqliteConnection connection,
        SqliteTransaction transaction,
        HolidayReminderTarget target,
        DateTime reminderAt,
        DateTime updatedAt)
    {
        TodoItem item;
        using (var command = CreateTargetCommand(connection, transaction, """
            SELECT id,title,notes,completed,due_at,remind_at,notified_at,created_at,updated_at
            FROM todos WHERE id=$id AND completed=0 AND remind_at=$expected
            """, target))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw ChangedSinceConfirmation();
            item = new TodoItem(reader.GetString(0), reader.GetString(1), Text(reader, 2), reader.GetInt64(3) > 0,
                Date(reader, 4), ReadDate(reader, 5), Date(reader, 6), ReadDate(reader, 7), ReadDate(reader, 8));
        }

        UpdateReminderTimestamp(connection, transaction, "todos", target, reminderAt, updatedAt);
        canonicalWriter.UpsertTodo(connection, transaction, item with { RemindAt = reminderAt, NotifiedAt = null, UpdatedAt = updatedAt });
    }

    void RescheduleEventReminder(
        SqliteConnection connection,
        SqliteTransaction transaction,
        HolidayReminderTarget target,
        DateTime reminderAt,
        DateTime updatedAt)
    {
        CalendarEventItem item;
        using (var command = CreateTargetCommand(connection, transaction, """
            SELECT id,title,notes,start_at,end_at,remind_at,notified_at,created_at,updated_at
            FROM calendar_events WHERE id=$id AND remind_at=$expected
            """, target))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw ChangedSinceConfirmation();
            item = new CalendarEventItem(reader.GetString(0), reader.GetString(1), Text(reader, 2), ReadDate(reader, 3),
                ReadDate(reader, 4), ReadDate(reader, 5), Date(reader, 6), ReadDate(reader, 7), ReadDate(reader, 8));
        }

        UpdateReminderTimestamp(connection, transaction, "calendar_events", target, reminderAt, updatedAt);
        canonicalWriter.UpsertEvent(connection, transaction, item with { RemindAt = reminderAt, NotifiedAt = null, UpdatedAt = updatedAt });
    }

    void RescheduleSingleReminder(
        SqliteConnection connection,
        SqliteTransaction transaction,
        HolidayReminderTarget target,
        DateTime reminderAt,
        DateTime updatedAt)
    {
        SingleReminder item;
        using (var command = CreateTargetCommand(connection, transaction, """
            SELECT id,title,notes,remind_at,notified_at,created_at,updated_at
            FROM single_reminders WHERE id=$id AND remind_at=$expected
            """, target))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw ChangedSinceConfirmation();
            item = new SingleReminder(reader.GetString(0), reader.GetString(1), Text(reader, 2), ReadDate(reader, 3),
                Date(reader, 4), ReadDate(reader, 5), ReadDate(reader, 6));
        }

        UpdateReminderTimestamp(connection, transaction, "single_reminders", target, reminderAt, updatedAt);
        canonicalWriter.UpsertReminder(connection, transaction, item with { RemindAt = reminderAt, NotifiedAt = null, UpdatedAt = updatedAt });
    }

    static SqliteCommand CreateTargetCommand(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        HolidayReminderTarget target)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", target.Id);
        command.Parameters.AddWithValue("$expected", target.OriginalRemindAt.ToString("O"));
        return command;
    }

    static void UpdateReminderTimestamp(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        HolidayReminderTarget target,
        DateTime reminderAt,
        DateTime updatedAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"UPDATE {table} SET remind_at=$remind, notified_at=NULL, updated_at=$updated WHERE id=$id AND remind_at=$expected";
        command.Parameters.AddWithValue("$id", target.Id);
        command.Parameters.AddWithValue("$expected", target.OriginalRemindAt.ToString("O"));
        command.Parameters.AddWithValue("$remind", reminderAt.ToString("O"));
        command.Parameters.AddWithValue("$updated", updatedAt.ToString("O"));
        if (command.ExecuteNonQuery() != 1) throw ChangedSinceConfirmation();
    }

    static InvalidOperationException ChangedSinceConfirmation() =>
        new("至少一条提醒已在确认后被修改或删除；为避免覆盖新数据，本次没有执行任何修改。");
}

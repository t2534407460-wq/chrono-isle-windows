using ChronoIsle.App.Services.ImportExport;
using ChronoIsle.App.Services.Persistence;
using Microsoft.Data.Sqlite;

namespace ChronoIsle.App.Services;

public sealed partial class LifeDataService
{
    internal IReadOnlyList<MarkdownTransferItem> MarkdownTransferItems()
    {
        var values = Todos().Select(todo => new MarkdownTransferItem(
            MarkdownItemKind.Todo, todo.Title, todo.Notes, todo.DueAt, null, null,
            todo.RemindAt, todo.IsCompleted, null, null, [])).ToList();

        using (var db = Open())
        {
            using (var events = db.CreateCommand())
            {
                events.CommandText = """
                    SELECT title,notes,start_at,end_at,remind_at
                    FROM calendar_events ORDER BY start_at,title
                    """;
                using var reader = events.ExecuteReader();
                while (reader.Read())
                    values.Add(new(
                        MarkdownItemKind.Event,
                        reader.GetString(0),
                        Text(reader, 1),
                        null,
                        ReadDate(reader, 2),
                        ReadDate(reader, 3),
                        Date(reader, 4),
                        false,
                        null,
                        null,
                        []));
            }

            using (var reminders = db.CreateCommand())
            {
                reminders.CommandText = """
                    SELECT title,notes,remind_at
                    FROM single_reminders ORDER BY remind_at,title
                    """;
                using var reader = reminders.ExecuteReader();
                while (reader.Read())
                    values.Add(new(
                        MarkdownItemKind.Reminder,
                        reader.GetString(0),
                        Text(reader, 1),
                        null,
                        null,
                        null,
                        ReadDate(reader, 2),
                        false,
                        null,
                        null,
                        []));
            }

            using (var longTerm = db.CreateCommand())
            {
                longTerm.CommandText = """
                    SELECT title,notes,due_local_datetime,remind_local_datetime,status
                    FROM life_items
                    WHERE item_type='LongTerm' AND deleted_at IS NULL
                      AND status NOT IN ('Cancelled','Ignored')
                    ORDER BY title
                    """;
                using var reader = longTerm.ExecuteReader();
                while (reader.Read())
                    values.Add(new(
                        MarkdownItemKind.LongTerm,
                        reader.GetString(0),
                        Text(reader, 1),
                        LongTermDate(reader, 2),
                        null,
                        null,
                        LongTermDate(reader, 3),
                        string.Equals(reader.GetString(4), "Completed", StringComparison.Ordinal),
                        null,
                        null,
                        []));
            }
        }

        values.AddRange(RecurringReminders().Select(reminder => new MarkdownTransferItem(
            MarkdownItemKind.Recurring,
            reminder.Title,
            reminder.Notes,
            null,
            null,
            null,
            null,
            false,
            reminder.Recurrence,
            reminder.ReminderTime,
            reminder.Weekdays)));

        return values
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.DueAt ?? item.StartsAt ?? item.RemindAt)
            .ThenBy(item => item.Title, StringComparer.Ordinal)
            .ToArray();
    }

    internal int ImportMarkdownItems(IReadOnlyList<MarkdownTransferItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var now = localNow();
        var imported = writeQueue.Execute(unitOfWork =>
        {
            foreach (var item in items)
            {
                var id = Guid.NewGuid().ToString("N");
                switch (item.Kind)
                {
                    case MarkdownItemKind.Todo:
                        InsertImportedTodo(unitOfWork, new(
                            id, item.Title, item.Notes, item.IsCompleted, item.DueAt,
                            item.RemindAt, null, now, now));
                        break;
                    case MarkdownItemKind.Reminder:
                        InsertImportedReminder(unitOfWork, new(
                            id, item.Title, item.Notes, item.RemindAt!.Value, null, now, now));
                        break;
                    case MarkdownItemKind.Event:
                        InsertImportedEvent(unitOfWork, new(
                            id, item.Title, item.Notes, item.StartsAt!.Value, item.EndsAt!.Value,
                            item.RemindAt, null, now, now));
                        break;
                    case MarkdownItemKind.LongTerm:
                        InsertImportedLongTerm(unitOfWork, id, item, now);
                        break;
                    case MarkdownItemKind.Recurring:
                        InsertImportedRecurring(unitOfWork, id, item, now);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(item.Kind));
                }
            }
            return items.Count;
        });
        RaiseAgendaChanged();
        return imported;
    }

    void InsertImportedTodo(IUnitOfWork unitOfWork, TodoItem item)
    {
        using var command = unitOfWork.Connection.CreateCommand();
        command.Transaction = unitOfWork.Transaction;
        command.CommandText = """
            INSERT INTO todos(id,title,notes,completed,due_at,remind_at,notified_at,created_at,updated_at)
            VALUES($id,$title,$notes,$completed,$due,$remind,NULL,$created,$updated)
            """;
        command.Parameters.AddWithValue("$id", item.Id);
        command.Parameters.AddWithValue("$title", item.Title);
        command.Parameters.AddWithValue("$notes", (object?)item.Notes ?? DBNull.Value);
        command.Parameters.AddWithValue("$completed", item.IsCompleted ? 1 : 0);
        command.Parameters.AddWithValue("$due", (object?)StoreDate(item.DueAt) ?? DBNull.Value);
        command.Parameters.AddWithValue("$remind", (object?)StoreDate(item.RemindAt) ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", item.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated", item.UpdatedAt.ToString("O"));
        command.ExecuteNonQuery();
        canonicalWriter.UpsertTodo(unitOfWork.Connection, unitOfWork.Transaction, item);
    }

    void InsertImportedReminder(IUnitOfWork unitOfWork, SingleReminder item)
    {
        using var command = unitOfWork.Connection.CreateCommand();
        command.Transaction = unitOfWork.Transaction;
        command.CommandText = """
            INSERT INTO single_reminders(id,title,notes,remind_at,notified_at,created_at,updated_at)
            VALUES($id,$title,$notes,$remind,NULL,$created,$updated)
            """;
        command.Parameters.AddWithValue("$id", item.Id);
        command.Parameters.AddWithValue("$title", item.Title);
        command.Parameters.AddWithValue("$notes", (object?)item.Notes ?? DBNull.Value);
        command.Parameters.AddWithValue("$remind", item.RemindAt.ToString("O"));
        command.Parameters.AddWithValue("$created", item.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated", item.UpdatedAt.ToString("O"));
        command.ExecuteNonQuery();
        canonicalWriter.UpsertReminder(unitOfWork.Connection, unitOfWork.Transaction, item);
    }

    void InsertImportedEvent(IUnitOfWork unitOfWork, CalendarEventItem item)
    {
        using var command = unitOfWork.Connection.CreateCommand();
        command.Transaction = unitOfWork.Transaction;
        command.CommandText = """
            INSERT INTO calendar_events(id,title,notes,start_at,end_at,remind_at,notified_at,created_at,updated_at)
            VALUES($id,$title,$notes,$start,$end,$remind,NULL,$created,$updated)
            """;
        command.Parameters.AddWithValue("$id", item.Id);
        command.Parameters.AddWithValue("$title", item.Title);
        command.Parameters.AddWithValue("$notes", (object?)item.Notes ?? DBNull.Value);
        command.Parameters.AddWithValue("$start", item.StartsAt.ToString("O"));
        command.Parameters.AddWithValue("$end", item.EndsAt.ToString("O"));
        command.Parameters.AddWithValue("$remind", (object?)StoreDate(item.RemindAt) ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", item.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated", item.UpdatedAt.ToString("O"));
        command.ExecuteNonQuery();
        canonicalWriter.UpsertEvent(unitOfWork.Connection, unitOfWork.Transaction, item);
    }

    void InsertImportedLongTerm(
        IUnitOfWork unitOfWork,
        string id,
        MarkdownTransferItem source,
        DateTime now)
    {
        var item = new TodoItem(id, source.Title, source.Notes, source.IsCompleted,
            source.DueAt, source.RemindAt, null, now, now);
        canonicalWriter.UpsertTodo(unitOfWork.Connection, unitOfWork.Transaction, item);
        using var command = unitOfWork.Connection.CreateCommand();
        command.Transaction = unitOfWork.Transaction;
        command.CommandText = "UPDATE life_items SET item_type='LongTerm' WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("长期事项导入失败。");
    }

    void InsertImportedRecurring(
        IUnitOfWork unitOfWork,
        string id,
        MarkdownTransferItem source,
        DateTime now)
    {
        var weekdays = source.Recurrence == RecurrenceKind.Weekly
            ? source.Weekdays.Distinct().OrderBy(day => day).ToArray()
            : [];
        var item = new RecurringReminder(
            id,
            source.Title,
            source.Notes,
            source.RecurrenceTime!.Value,
            source.Recurrence!.Value,
            weekdays,
            null,
            now,
            now);
        var next = NextOccurrence(item, now)
            ?? throw new InvalidOperationException("周期提醒无法计算下一次时间。");
        using var command = unitOfWork.Connection.CreateCommand();
        command.Transaction = unitOfWork.Transaction;
        command.CommandText = """
            INSERT INTO recurring_reminders(id,title,notes,reminder_time,recurrence,weekdays,last_notified_at,created_at,updated_at)
            VALUES($id,$title,$notes,$time,$recurrence,$weekdays,NULL,$created,$updated)
            """;
        BindRecurring(command, item);
        command.ExecuteNonQuery();
        canonicalWriter.UpsertRecurringReminder(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            item,
            next.StartsAt);
    }
}

using System.IO;
using System.Text.Json;
using OpenIsland.App.Services.Scheduling;
using Microsoft.Data.Sqlite;
using OpenIsland.App.Services.Persistence;
using OpenIsland.App.Services.Domain;
using OpenIsland.App.Services.Productivity;

namespace OpenIsland.App.Services;

public sealed partial class LifeDataService
{
    readonly SqliteConnectionFactory connectionFactory;
    readonly IDbWriteQueue writeQueue;
    readonly CanonicalLifeItemWriter canonicalWriter;
    readonly Func<DateTime> localNow;
    internal string DatabasePath { get; }
    public event EventHandler? AgendaChanged;
    public event EventHandler? TodosChanged;

    public LifeDataService(string? databasePath = null, Func<DateTime>? localNow = null)
    {
        this.localNow = localNow ?? (() => DateTime.Now);
        var path = databasePath ?? Environment.GetEnvironmentVariable("OPENISLAND_DATABASE_PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenIsland");
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, "life-assistant.db");
        }

        DatabasePath = Path.GetFullPath(path);
        var runtime = LifeDataStoreRuntimeRegistry.GetOrCreate(DatabasePath);
        connectionFactory = runtime.ConnectionFactory;
        writeQueue = runtime.WriteQueue;
        var timeZones = new SystemTimeZoneCatalog();
        var timeZoneCapability = timeZones.CheckCapabilities();
        if (!timeZoneCapability.IsHealthy)
            throw new InvalidOperationException("时区能力自检失败：" +
                string.Join("；", timeZoneCapability.Errors));
        canonicalWriter = new CanonicalLifeItemWriter(timeZones);
        var migrator = new LifeSchemaMigrator(timeZoneCatalog: timeZones);
        writeQueue.Execute(unitOfWork =>
        {
            Initialize(unitOfWork.Connection, unitOfWork.Transaction);
            migrator.Migrate(unitOfWork.Connection, unitOfWork.Transaction);
        });
        new ProductivitySchemaInitializer(writeQueue).Initialize();
    }

    SqliteConnection Open() => connectionFactory.OpenConnection();

    static void Initialize(SqliteConnection db, SqliteTransaction transaction)
    {
        Execute(db, transaction, """
            CREATE TABLE IF NOT EXISTS todos(
                id TEXT PRIMARY KEY,title TEXT NOT NULL,notes TEXT,completed INTEGER NOT NULL,
                due_at TEXT,remind_at TEXT,notified_at TEXT,created_at TEXT NOT NULL,updated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS calendar_events(
                id TEXT PRIMARY KEY,title TEXT NOT NULL,notes TEXT,start_at TEXT NOT NULL,end_at TEXT NOT NULL,
                remind_at TEXT,notified_at TEXT,created_at TEXT NOT NULL,updated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS chat_sessions(
                id TEXT PRIMARY KEY,title TEXT NOT NULL,created_at TEXT NOT NULL,updated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS chat_messages(
                id TEXT PRIMARY KEY,session_id TEXT NOT NULL,role TEXT NOT NULL,content TEXT NOT NULL,created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS assistant_actions(
                id TEXT PRIMARY KEY,session_id TEXT NOT NULL,source_text TEXT NOT NULL,intent_json TEXT NOT NULL,
                status TEXT NOT NULL,error_message TEXT,created_at TEXT NOT NULL,updated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS archived_todos(
                id TEXT PRIMARY KEY,title TEXT NOT NULL,notes TEXT,due_at TEXT,remind_at TEXT,
                archived_at TEXT NOT NULL,reason TEXT NOT NULL);
            """);
        EnsureColumn(db, transaction, "todos", "notified_at", "TEXT");
        EnsureColumn(db, transaction, "archived_todos", "kind", "TEXT NOT NULL DEFAULT 'todo'");
        EnsureColumn(db, transaction, "archived_todos", "payload_json", "TEXT");
        InitializeRecurring(db, transaction);
        InitializeSingleReminders(db, transaction);
    }

    static void Execute(SqliteConnection db, SqliteTransaction transaction, string sql)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    static void EnsureColumn(SqliteConnection db, SqliteTransaction transaction, string table, string column, string type)
    {
        var exists = false;
        using (var command = db.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"PRAGMA table_info({table})";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) continue;
                exists = true;
                break;
            }
        }
        if (!exists) Execute(db, transaction, $"ALTER TABLE {table} ADD COLUMN {column} {type}");
    }

    static DateTime? ParseDate(string? value) => DateTime.TryParse(value, out var parsed) ? parsed : null;
    static string? StoreDate(DateTime? value) => value?.ToString("O");
    static DateTime ReadDate(SqliteDataReader reader, int index) => DateTime.Parse(reader.GetString(index));

    public IReadOnlyList<TodoItem> Todos()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT id,title,notes,completed,due_at,remind_at,notified_at,created_at,updated_at FROM todos ORDER BY completed,due_at IS NULL,due_at,title";
        using var reader = command.ExecuteReader();
        var values = new List<TodoItem>();
        while (reader.Read())
            values.Add(new(reader.GetString(0), reader.GetString(1), Text(reader, 2), reader.GetInt64(3) > 0,
                Date(reader, 4), Date(reader, 5), Date(reader, 6), ReadDate(reader, 7), ReadDate(reader, 8)));
        return values;
    }

    public TodoItem Save(string title, string? notes, DateTime? due, DateTime? reminder, string? id = null, bool completed = false)
    {
        var now = localNow();
        var item = new TodoItem(id ?? Guid.NewGuid().ToString("N"), title.Trim(), notes, completed, due, reminder, null, now, now);
        writeQueue.Execute(unitOfWork =>
        {
            using var command = unitOfWork.Connection.CreateCommand();
            command.Transaction = unitOfWork.Transaction;
            command.CommandText = """
                INSERT INTO todos(id,title,notes,completed,due_at,remind_at,notified_at,created_at,updated_at)
                VALUES($id,$title,$notes,$completed,$due,$reminder,NULL,$created,$updated)
                ON CONFLICT(id) DO UPDATE SET title=$title,notes=$notes,completed=$completed,due_at=$due,
                  remind_at=$reminder,notified_at=NULL,updated_at=$updated
                """;
            BindTodo(command, item);
            command.ExecuteNonQuery();
            canonicalWriter.UpsertTodo(unitOfWork.Connection, unitOfWork.Transaction, item);
        });
        RaiseAgendaChanged();
        return item;
    }

    public void Complete(string id, bool value = true)
    {
        var updatedAt = localNow();
        writeQueue.Execute(unitOfWork =>
        {
            using var command = unitOfWork.Connection.CreateCommand();
            command.Transaction = unitOfWork.Transaction;
            command.CommandText = "UPDATE todos SET completed=$completed,updated_at=$updated WHERE id=$id";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$completed", value ? 1 : 0);
            command.Parameters.AddWithValue("$updated", updatedAt.ToString("O"));
            if (command.ExecuteNonQuery() == 1)
            {
                canonicalWriter.SetTodoCompleted(unitOfWork.Connection, unitOfWork.Transaction, id, value, updatedAt);
                if (value) ArchiveTodo(unitOfWork, id, "已完成", updatedAt);
            }
        });
        RaiseAgendaChanged();
    }

    public IReadOnlyList<ArchivedTodoItem> ArchivedTodos()
    {
        ArchiveCompletedAndOverdue();
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT id,title,notes,due_at,archived_at,reason,kind FROM archived_todos ORDER BY archived_at DESC";
        using var reader = command.ExecuteReader();
        var values = new List<ArchivedTodoItem>();
        while (reader.Read())
            values.Add(new(reader.GetString(0), reader.GetString(1), Text(reader, 2), Date(reader, 3),
                ReadDate(reader, 4), reader.GetString(5), reader.GetString(6)));
        return values;
    }

    public void ArchiveCompletedAndOverdue()
    {
        var now = localNow();
        var changed = writeQueue.Execute(unitOfWork =>
        {
            var targets = new List<(string Id, string Kind, string Reason)>();
            using (var command = unitOfWork.Connection.CreateCommand())
            {
                command.Transaction = unitOfWork.Transaction;
                command.CommandText = """
                    SELECT t.id,'todo',CASE WHEN t.completed=1 THEN '已完成' ELSE '已逾期' END
                    FROM todos t
                    WHERE t.completed=1 OR (
                        t.completed=0 AND t.due_at IS NOT NULL
                        AND julianday(t.due_at) < julianday($today))
                    UNION ALL
                    SELECT id,'event','已逾期' FROM calendar_events
                    WHERE julianday(end_at) < julianday($today)
                    UNION ALL
                    SELECT id,'reminder','已逾期' FROM single_reminders
                    WHERE julianday(remind_at) < julianday($today)
                    """;
                command.Parameters.AddWithValue("$today", now.Date.ToString("O"));
                using var reader = command.ExecuteReader();
                while (reader.Read()) targets.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
            var count = targets.Count(target => ArchiveItem(unitOfWork, target.Id, target.Kind, target.Reason, now));
            PurgeArchivedTodos(unitOfWork, now);
            return count;
        });
        if (changed > 0) RaiseAgendaChanged();
    }

    public int ArchiveAgendaItems(IEnumerable<AgendaItem> items, string reason = "手动归档")
    {
        ArgumentNullException.ThrowIfNull(items);
        var targets = items.Select(item => (item.Id, item.Kind))
            .Where(item => item.Kind is "todo" or "event" or "recurring" or "reminder")
            .Distinct()
            .ToList();
        if (targets.Count == 0) return 0;

        var now = localNow();
        var archived = writeQueue.Execute(unitOfWork =>
        {
            var count = targets.Count(target => ArchiveItem(unitOfWork, target.Id, target.Kind, reason, now));
            PurgeArchivedTodos(unitOfWork, now);
            return count;
        });
        if (archived > 0) RaiseAgendaChanged();
        return archived;
    }

    public bool RestoreArchivedTodo(string id, DateTime scheduledAt) => RestoreArchivedItem(id, scheduledAt);

    public bool RestoreArchivedItem(string id, DateTime? scheduledAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var restoredAt = localNow();
        var restored = writeQueue.Execute(unitOfWork =>
        {
            using var read = unitOfWork.Connection.CreateCommand();
            read.Transaction = unitOfWork.Transaction;
            read.CommandText = "SELECT title,notes,due_at,payload_json,kind FROM archived_todos WHERE id=$id";
            read.Parameters.AddWithValue("$id", id);
            using var reader = read.ExecuteReader();
            if (!reader.Read()) return false;
            var archived = new ArchivedItemSnapshot(
                id, reader.GetString(0), Text(reader, 1), Date(reader, 2), Text(reader, 3), reader.GetString(4));
            reader.Close();

            if (archived.Kind != "recurring" && (scheduledAt is null || scheduledAt <= restoredAt)) return false;
            var saved = archived.Kind switch
            {
                "todo" => RestoreTodo(unitOfWork, archived, scheduledAt!.Value, restoredAt),
                "event" => RestoreEvent(unitOfWork, archived, scheduledAt!.Value, restoredAt),
                "reminder" => RestoreSingleReminder(unitOfWork, archived, scheduledAt!.Value, restoredAt),
                "recurring" => RestoreRecurringReminder(unitOfWork, archived, restoredAt),
                _ => false
            };
            if (!saved) return false;

            using var remove = unitOfWork.Connection.CreateCommand();
            remove.Transaction = unitOfWork.Transaction;
            remove.CommandText = "DELETE FROM archived_todos WHERE id=$id";
            remove.Parameters.AddWithValue("$id", id);
            return remove.ExecuteNonQuery() == 1;
        });
        if (restored) RaiseAgendaChanged();
        return restored;
    }

    bool RestoreTodo(IUnitOfWork unitOfWork, ArchivedItemSnapshot archived, DateTime scheduledAt, DateTime restoredAt)
    {
        var todo = new TodoItem(archived.Id, archived.Title, archived.Notes, false,
            scheduledAt, scheduledAt, null, restoredAt, restoredAt);
        using var save = unitOfWork.Connection.CreateCommand();
        save.Transaction = unitOfWork.Transaction;
        save.CommandText = """
            INSERT INTO todos(id,title,notes,completed,due_at,remind_at,notified_at,created_at,updated_at)
            VALUES($id,$title,$notes,0,$due,$reminder,NULL,$created,$updated)
            ON CONFLICT(id) DO UPDATE SET title=$title,notes=$notes,completed=0,due_at=$due,
              remind_at=$reminder,notified_at=NULL,updated_at=$updated
            """;
        BindTodo(save, todo);
        if (save.ExecuteNonQuery() != 1) return false;
        canonicalWriter.UpsertTodo(unitOfWork.Connection, unitOfWork.Transaction, todo);
        return true;
    }

    bool RestoreEvent(IUnitOfWork unitOfWork, ArchivedItemSnapshot archived, DateTime startsAt, DateTime restoredAt)
    {
        var payload = string.IsNullOrWhiteSpace(archived.PayloadJson)
            ? null
            : JsonSerializer.Deserialize<ArchivedEventPayload>(archived.PayloadJson);
        if (payload is null || payload.EndsAt <= payload.StartsAt) return false;
        DateTime? remindAt = payload.RemindAt is null ? null : startsAt + (payload.RemindAt.Value - payload.StartsAt);
        var item = new CalendarEventItem(archived.Id, archived.Title, archived.Notes,
            startsAt, startsAt + (payload.EndsAt - payload.StartsAt), remindAt, null, restoredAt, restoredAt);
        using var save = unitOfWork.Connection.CreateCommand();
        save.Transaction = unitOfWork.Transaction;
        save.CommandText = """
            INSERT INTO calendar_events(id,title,notes,start_at,end_at,remind_at,notified_at,created_at,updated_at)
            VALUES($id,$title,$notes,$start,$end,$reminder,NULL,$created,$updated)
            """;
        save.Parameters.AddWithValue("$id", item.Id);
        save.Parameters.AddWithValue("$title", item.Title);
        save.Parameters.AddWithValue("$notes", (object?)item.Notes ?? DBNull.Value);
        save.Parameters.AddWithValue("$start", item.StartsAt.ToString("O"));
        save.Parameters.AddWithValue("$end", item.EndsAt.ToString("O"));
        save.Parameters.AddWithValue("$reminder", (object?)StoreDate(item.RemindAt) ?? DBNull.Value);
        save.Parameters.AddWithValue("$created", item.CreatedAt.ToString("O"));
        save.Parameters.AddWithValue("$updated", item.UpdatedAt.ToString("O"));
        if (save.ExecuteNonQuery() != 1) return false;
        canonicalWriter.UpsertEvent(unitOfWork.Connection, unitOfWork.Transaction, item);
        return true;
    }

    bool RestoreSingleReminder(IUnitOfWork unitOfWork, ArchivedItemSnapshot archived, DateTime remindAt, DateTime restoredAt)
    {
        var item = new SingleReminder(archived.Id, archived.Title, archived.Notes, remindAt, null, restoredAt, restoredAt);
        using var save = unitOfWork.Connection.CreateCommand();
        save.Transaction = unitOfWork.Transaction;
        save.CommandText = """
            INSERT INTO single_reminders(id,title,notes,remind_at,notified_at,created_at,updated_at)
            VALUES($id,$title,$notes,$remind,NULL,$created,$updated)
            """;
        save.Parameters.AddWithValue("$id", item.Id);
        save.Parameters.AddWithValue("$title", item.Title);
        save.Parameters.AddWithValue("$notes", (object?)item.Notes ?? DBNull.Value);
        save.Parameters.AddWithValue("$remind", item.RemindAt.ToString("O"));
        save.Parameters.AddWithValue("$created", item.CreatedAt.ToString("O"));
        save.Parameters.AddWithValue("$updated", item.UpdatedAt.ToString("O"));
        if (save.ExecuteNonQuery() != 1) return false;
        canonicalWriter.UpsertReminder(unitOfWork.Connection, unitOfWork.Transaction, item);
        return true;
    }

    bool RestoreRecurringReminder(IUnitOfWork unitOfWork, ArchivedItemSnapshot archived, DateTime restoredAt)
    {
        var payload = string.IsNullOrWhiteSpace(archived.PayloadJson)
            ? null
            : JsonSerializer.Deserialize<ArchivedRecurringPayload>(archived.PayloadJson);
        if (payload is null) return false;
        var item = new RecurringReminder(archived.Id, archived.Title, archived.Notes,
            payload.ReminderTime, payload.Recurrence, payload.Weekdays, null, restoredAt, restoredAt);
        var next = NextOccurrence(item, restoredAt);
        if (next is null) return false;
        using var save = unitOfWork.Connection.CreateCommand();
        save.Transaction = unitOfWork.Transaction;
        save.CommandText = """
            INSERT INTO recurring_reminders(id,title,notes,reminder_time,recurrence,weekdays,last_notified_at,created_at,updated_at)
            VALUES($id,$title,$notes,$time,$recurrence,$weekdays,NULL,$created,$updated)
            """;
        BindRecurring(save, item);
        if (save.ExecuteNonQuery() != 1) return false;
        canonicalWriter.UpsertRecurringReminder(
            unitOfWork.Connection, unitOfWork.Transaction, item, next.StartsAt);
        return true;
    }

    bool ArchiveTodo(IUnitOfWork unitOfWork, string id, string reason, DateTime archivedAt)
    {
        using var read = unitOfWork.Connection.CreateCommand();
        read.Transaction = unitOfWork.Transaction;
        read.CommandText = "SELECT title,notes,due_at,remind_at,completed,created_at,updated_at FROM todos WHERE id=$id";
        read.Parameters.AddWithValue("$id", id);
        using var reader = read.ExecuteReader();
        if (!reader.Read()) return false;
        var todo = new TodoItem(id, reader.GetString(0), Text(reader, 1), reader.GetInt64(4) > 0,
            Date(reader, 2), Date(reader, 3), null, ReadDate(reader, 5), ReadDate(reader, 6));
        reader.Close();

        SaveArchivedItem(unitOfWork, todo.Id, "todo", todo.Title, todo.Notes, todo.DueAt, todo.RemindAt,
            archivedAt, reason, null);
        return RemoveActiveItem(unitOfWork, "todos", id, archivedAt);
    }

    bool ArchiveItem(IUnitOfWork unitOfWork, string id, string kind, string reason, DateTime archivedAt) => kind switch
    {
        "todo" => ArchiveTodo(unitOfWork, id, reason, archivedAt),
        "event" => ArchiveEvent(unitOfWork, id, reason, archivedAt),
        "reminder" => ArchiveSingleReminder(unitOfWork, id, reason, archivedAt),
        "recurring" => ArchiveRecurringReminder(unitOfWork, id, reason, archivedAt),
        _ => false
    };

    bool ArchiveEvent(IUnitOfWork unitOfWork, string id, string reason, DateTime archivedAt)
    {
        using var read = unitOfWork.Connection.CreateCommand();
        read.Transaction = unitOfWork.Transaction;
        read.CommandText = "SELECT title,notes,start_at,end_at,remind_at FROM calendar_events WHERE id=$id";
        read.Parameters.AddWithValue("$id", id);
        using var reader = read.ExecuteReader();
        if (!reader.Read()) return false;
        var title = reader.GetString(0);
        var notes = Text(reader, 1);
        var startsAt = ReadDate(reader, 2);
        var endsAt = ReadDate(reader, 3);
        var remindAt = Date(reader, 4);
        reader.Close();

        var payload = JsonSerializer.Serialize(new ArchivedEventPayload(startsAt, endsAt, remindAt));
        SaveArchivedItem(unitOfWork, id, "event", title, notes, startsAt, remindAt, archivedAt, reason, payload);
        return RemoveActiveItem(unitOfWork, "calendar_events", id, archivedAt);
    }

    bool ArchiveSingleReminder(IUnitOfWork unitOfWork, string id, string reason, DateTime archivedAt)
    {
        using var read = unitOfWork.Connection.CreateCommand();
        read.Transaction = unitOfWork.Transaction;
        read.CommandText = "SELECT title,notes,remind_at FROM single_reminders WHERE id=$id";
        read.Parameters.AddWithValue("$id", id);
        using var reader = read.ExecuteReader();
        if (!reader.Read()) return false;
        var title = reader.GetString(0);
        var notes = Text(reader, 1);
        var remindAt = ReadDate(reader, 2);
        reader.Close();

        SaveArchivedItem(unitOfWork, id, "reminder", title, notes, remindAt, remindAt, archivedAt, reason, null);
        return RemoveActiveItem(unitOfWork, "single_reminders", id, archivedAt);
    }

    bool ArchiveRecurringReminder(IUnitOfWork unitOfWork, string id, string reason, DateTime archivedAt)
    {
        using var read = unitOfWork.Connection.CreateCommand();
        read.Transaction = unitOfWork.Transaction;
        read.CommandText = """
            SELECT id,title,notes,reminder_time,recurrence,weekdays,last_notified_at,created_at,updated_at
            FROM recurring_reminders WHERE id=$id
            """;
        read.Parameters.AddWithValue("$id", id);
        using var reader = read.ExecuteReader();
        if (!reader.Read()) return false;
        var reminder = ReadRecurring(reader);
        reader.Close();

        var payload = JsonSerializer.Serialize(new ArchivedRecurringPayload(
            reminder.ReminderTime, reminder.Recurrence, reminder.Weekdays.ToArray()));
        SaveArchivedItem(unitOfWork, id, "recurring", reminder.Title, reminder.Notes,
            NextOccurrence(reminder, archivedAt)?.StartsAt, null, archivedAt, reason, payload);
        return RemoveActiveItem(unitOfWork, "recurring_reminders", id, archivedAt);
    }

    static void SaveArchivedItem(
        IUnitOfWork unitOfWork,
        string id,
        string kind,
        string title,
        string? notes,
        DateTime? scheduledAt,
        DateTime? remindAt,
        DateTime archivedAt,
        string reason,
        string? payloadJson)
    {
        using var archive = unitOfWork.Connection.CreateCommand();
        archive.Transaction = unitOfWork.Transaction;
        archive.CommandText = """
            INSERT INTO archived_todos(id,title,notes,due_at,remind_at,archived_at,reason,kind,payload_json)
            VALUES($id,$title,$notes,$due,$reminder,$archived,$reason,$kind,$payload)
            ON CONFLICT(id) DO UPDATE SET title=$title,notes=$notes,due_at=$due,remind_at=$reminder,
                archived_at=$archived,reason=$reason,kind=$kind,payload_json=$payload
            """;
        archive.Parameters.AddWithValue("$id", id);
        archive.Parameters.AddWithValue("$title", title);
        archive.Parameters.AddWithValue("$notes", (object?)notes ?? DBNull.Value);
        archive.Parameters.AddWithValue("$due", (object?)StoreDate(scheduledAt) ?? DBNull.Value);
        archive.Parameters.AddWithValue("$reminder", (object?)StoreDate(remindAt) ?? DBNull.Value);
        archive.Parameters.AddWithValue("$archived", archivedAt.ToString("O"));
        archive.Parameters.AddWithValue("$reason", reason);
        archive.Parameters.AddWithValue("$kind", kind);
        archive.Parameters.AddWithValue("$payload", (object?)payloadJson ?? DBNull.Value);
        archive.ExecuteNonQuery();
    }

    bool RemoveActiveItem(IUnitOfWork unitOfWork, string table, string id, DateTime archivedAt)
    {
        using var remove = unitOfWork.Connection.CreateCommand();
        remove.Transaction = unitOfWork.Transaction;
        remove.CommandText = $"DELETE FROM {table} WHERE id=$id";
        remove.Parameters.AddWithValue("$id", id);
        if (remove.ExecuteNonQuery() != 1) return false;
        canonicalWriter.SoftDelete(unitOfWork.Connection, unitOfWork.Transaction, id, archivedAt);
        return true;
    }

    public bool DeleteArchivedItem(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var deleted = writeQueue.Execute(unitOfWork =>
        {
            using var command = unitOfWork.Connection.CreateCommand();
            command.Transaction = unitOfWork.Transaction;
            command.CommandText = "DELETE FROM archived_todos WHERE id=$id";
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() == 1;
        });
        if (deleted) RaiseAgendaChanged();
        return deleted;
    }

    sealed record ArchivedItemSnapshot(string Id, string Title, string? Notes, DateTime? ScheduledAt, string? PayloadJson, string Kind);
    sealed record ArchivedEventPayload(DateTime StartsAt, DateTime EndsAt, DateTime? RemindAt);
    sealed record ArchivedRecurringPayload(TimeOnly ReminderTime, RecurrenceKind Recurrence, DayOfWeek[] Weekdays);

    static void PurgeArchivedTodos(IUnitOfWork unitOfWork, DateTime now)
    {
        using var command = unitOfWork.Connection.CreateCommand();
        command.Transaction = unitOfWork.Transaction;
        command.CommandText = "DELETE FROM archived_todos WHERE julianday(archived_at) <= julianday($cutoff)";
        command.Parameters.AddWithValue("$cutoff", now.AddDays(-7).ToString("O"));
        command.ExecuteNonQuery();
    }

    public CalendarEventItem SaveEvent(string title, string? notes, DateTime startsAt, DateTime endsAt, DateTime? reminderAt, string? id = null)
    {
        var now = localNow();
        var item = new CalendarEventItem(id ?? Guid.NewGuid().ToString("N"), title.Trim(), notes, startsAt, endsAt, reminderAt, null, now, now);
        writeQueue.Execute(unitOfWork =>
        {
            using var command = unitOfWork.Connection.CreateCommand();
            command.Transaction = unitOfWork.Transaction;
            command.CommandText = """
                INSERT INTO calendar_events(id,title,notes,start_at,end_at,remind_at,notified_at,created_at,updated_at)
                VALUES($id,$title,$notes,$start,$end,$reminder,NULL,$created,$updated)
                ON CONFLICT(id) DO UPDATE SET title=$title,notes=$notes,start_at=$start,end_at=$end,
                  remind_at=$reminder,notified_at=NULL,updated_at=$updated
                """;
            command.Parameters.AddWithValue("$id", item.Id);
            command.Parameters.AddWithValue("$title", item.Title);
            command.Parameters.AddWithValue("$notes", (object?)item.Notes ?? DBNull.Value);
            command.Parameters.AddWithValue("$start", item.StartsAt.ToString("O"));
            command.Parameters.AddWithValue("$end", item.EndsAt.ToString("O"));
            command.Parameters.AddWithValue("$reminder", (object?)StoreDate(item.RemindAt) ?? DBNull.Value);
            command.Parameters.AddWithValue("$created", item.CreatedAt.ToString("O"));
            command.Parameters.AddWithValue("$updated", item.UpdatedAt.ToString("O"));
            command.ExecuteNonQuery();
            canonicalWriter.UpsertEvent(unitOfWork.Connection, unitOfWork.Transaction, item);
        });
        RaiseAgendaChanged();
        return item;
    }

    /// <summary>
    /// Snooze only changes the reminder delivery. A todo due date and an event's scheduled time
    /// are deliberately retained. A recurring occurrence becomes a separate one-off reminder so
    /// the recurrence rule itself is never silently changed.
    /// </summary>
    public AgendaItem SnoozeReminder(AgendaItem item, DateTime remindAt)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (remindAt <= localNow()) throw new ArgumentOutOfRangeException(nameof(remindAt));
        return item.Kind switch
        {
            "todo" => SnoozeTodo(item, remindAt),
            "event" => SnoozeEvent(item, remindAt),
            "reminder" => SnoozeSingleReminder(item, remindAt),
            "recurring" => SnoozeRecurringOccurrence(item, remindAt),
            _ => throw new InvalidOperationException("This item does not support snooze.")
        };
    }

    AgendaItem SnoozeTodo(AgendaItem item, DateTime remindAt)
    {
        var todo = Todos().FirstOrDefault(value => value.Id == item.Id)
            ?? throw new KeyNotFoundException("The todo no longer exists.");
        var saved = Save(todo.Title, todo.Notes, todo.DueAt, remindAt, todo.Id, todo.IsCompleted);
        IncrementTodoDeferredCount(saved.Id);
        return new(saved.Id, "todo", saved.Title, saved.Notes, saved.DueAt ?? remindAt, null, remindAt, saved.IsCompleted);
    }

    void IncrementTodoDeferredCount(string id)
    {
        writeQueue.Execute(unitOfWork =>
        {
            using var command = unitOfWork.Connection.CreateCommand();
            command.Transaction = unitOfWork.Transaction;
            command.CommandText = "UPDATE life_items SET deferred_count=deferred_count+1,row_version=row_version+1,updated_at=$now WHERE id=$id AND kind='Todo' AND deleted_at IS NULL";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        });
    }

    AgendaItem SnoozeEvent(AgendaItem item, DateTime remindAt)
    {
        if (item.EndsAt is null) throw new InvalidOperationException("The event has no end time.");
        var saved = SaveEvent(item.Title, item.Notes, item.StartsAt, item.EndsAt.Value, remindAt, item.Id);
        return new(saved.Id, "event", saved.Title, saved.Notes, saved.StartsAt, saved.EndsAt, remindAt, false);
    }

    AgendaItem SnoozeSingleReminder(AgendaItem item, DateTime remindAt)
    {
        var saved = SaveReminder(item.Title, item.Notes, remindAt, item.Id);
        return new(saved.Id, "reminder", saved.Title, saved.Notes, remindAt, null, remindAt, false);
    }

    AgendaItem SnoozeRecurringOccurrence(AgendaItem item, DateTime remindAt)
    {
        SaveRecurringOccurrenceOverride(item, OccurrenceOverrideType.Snooze, remindAt);
        var saved = SaveReminder($"稍后提醒：{item.Title}", item.Notes, remindAt);
        return new(saved.Id, "reminder", saved.Title, saved.Notes, remindAt, null, remindAt, false);
    }


    /// <summary>Changes the business schedule itself; this is intentionally different from SnoozeReminder.</summary>
    public AgendaItem RescheduleAgenda(AgendaItem item, DateTime scheduledAt)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (scheduledAt <= localNow()) throw new ArgumentOutOfRangeException(nameof(scheduledAt));
        return item.Kind switch
        {
            "todo" => RescheduleTodo(item, scheduledAt),
            "event" => RescheduleEvent(item, scheduledAt),
            "reminder" => RescheduleSingleReminder(item, scheduledAt),
            "recurring" => throw new InvalidOperationException("周期事项只能跳过本次或创建单次稍后提醒。"),
            _ => throw new InvalidOperationException("该事项不支持修改时间。")
        };
    }

    AgendaItem RescheduleTodo(AgendaItem item, DateTime scheduledAt)
    {
        var todo = Todos().FirstOrDefault(value => value.Id == item.Id)
            ?? throw new KeyNotFoundException("该待办已不存在。 ");
        var saved = Save(todo.Title, todo.Notes, scheduledAt, todo.RemindAt is null ? null : scheduledAt, todo.Id, todo.IsCompleted);
        return new(saved.Id, "todo", saved.Title, saved.Notes, scheduledAt, null, saved.RemindAt, saved.IsCompleted);
    }

    AgendaItem RescheduleEvent(AgendaItem item, DateTime scheduledAt)
    {
        if (item.EndsAt is null) throw new InvalidOperationException("该日程没有结束时间。 ");
        var duration = item.EndsAt.Value - item.StartsAt;
        var saved = SaveEvent(item.Title, item.Notes, scheduledAt, scheduledAt.Add(duration), item.RemindAt is null ? null : scheduledAt, item.Id);
        return new(saved.Id, "event", saved.Title, saved.Notes, saved.StartsAt, saved.EndsAt, saved.RemindAt, false);
    }

    AgendaItem RescheduleSingleReminder(AgendaItem item, DateTime scheduledAt)
    {
        var saved = SaveReminder(item.Title, item.Notes, scheduledAt, item.Id);
        return new(saved.Id, "reminder", saved.Title, saved.Notes, scheduledAt, null, scheduledAt, false);
    }
    public IReadOnlyList<AgendaItem> AgendaFor(DateTime day)
    {
        var start = day.Date;
        var end = start.AddDays(1);
        var values = new List<AgendaItem>();
        using var db = Open();
        using (var todos = db.CreateCommand())
        {
            todos.CommandText = """
                SELECT id,title,notes,completed,due_at,remind_at FROM todos
                WHERE due_at >= $start AND due_at < $end
                ORDER BY due_at,title
                """;
            todos.Parameters.AddWithValue("$start", start.ToString("O"));
            todos.Parameters.AddWithValue("$end", end.ToString("O"));
            using var reader = todos.ExecuteReader();
            while (reader.Read())
            {
                var due = ParseDate(reader.GetString(4)) ?? start;
                values.Add(new(reader.GetString(0), "todo", reader.GetString(1), Text(reader, 2), due, null, Date(reader, 5), reader.GetInt64(3) > 0));
            }
        }
        using (var events = db.CreateCommand())
        {
            events.CommandText = """
                SELECT id,title,notes,start_at,end_at,remind_at FROM calendar_events
                WHERE start_at < $end AND end_at > $start
                ORDER BY start_at,title
                """;
            events.Parameters.AddWithValue("$start", start.ToString("O"));
            events.Parameters.AddWithValue("$end", end.ToString("O"));
            using var reader = events.ExecuteReader();
            while (reader.Read())
                values.Add(new(reader.GetString(0), "event", reader.GetString(1), Text(reader, 2),
                    ReadDate(reader, 3), ReadDate(reader, 4), Date(reader, 5), false));
        }
        values.AddRange(RecurringAgendaFor(day));
        values.AddRange(SingleReminderAgendaFor(day));
        return values.OrderBy(x => x.StartsAt).ThenBy(x => x.Kind).ToList();
    }

    public IReadOnlyList<AgendaItem> TodayAgenda() => AgendaFor(DateTime.Today);
    public IReadOnlyList<AgendaItem> AgendaForRange(DateTime startsAt, DateTime endsAt)
    {
        if (endsAt <= startsAt) return [];
        var values = new List<AgendaItem>();
        for (var day = startsAt.Date; day < endsAt; day = day.AddDays(1))
            values.AddRange(AgendaFor(day));

        return values
            .GroupBy(item => item.Kind == "recurring" ? $"{item.Kind}:{item.Id}:{item.StartsAt:O}" : $"{item.Kind}:{item.Id}")
            .Select(group => group.First())
            .OrderBy(item => item.StartsAt)
            .ThenBy(item => item.Kind)
            .ToList();
    }

    public IslandIndicatorState GetIslandIndicatorState(DateTime now) =>
        GetCalendarIndicatorState(now.Date, now);

    public IslandIndicatorState GetCalendarIndicatorState(DateTime day, DateTime now)
    {
        var items = AgendaFor(day).Where(item => item.Kind != "recurring").ToList();
        var pendingTodos = items.Where(item => item.Kind == "todo" && !item.IsCompleted).ToList();
        var graceMinutes = TodoOverdueGraceMinutes();
        if (pendingTodos.Any(item => IsOverdue(item.StartsAt, now, graceMinutes.GetValueOrDefault(item.Id, 5)))) return IslandIndicatorState.OverdueTodo;
        if (pendingTodos.Any(item => item.StartsAt > now && item.StartsAt <= now.AddHours(1))) return IslandIndicatorState.DueSoonTodo;
        if (pendingTodos.Count > 0) return IslandIndicatorState.PendingTodo;
        return items.Any(item => item.Kind is "event" or "reminder")
            ? IslandIndicatorState.ReminderOnly
            : IslandIndicatorState.Idle;
    }

    public IslandIndicatorState GetAgendaItemIndicatorState(AgendaItem item, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(item);
        return GetItemIndicatorState(item.Id, item.Kind, item.IsCompleted, item.StartsAt, now, TodoOverdueGraceMinutes());
    }

    public IReadOnlyDictionary<string, IslandIndicatorState> GetManagedItemIndicatorStates(IEnumerable<ManagedLifeItem> items, DateTime now)
    {
        var graceMinutes = TodoOverdueGraceMinutes();
        return items.ToDictionary(item => item.Id, item =>
            GetItemIndicatorState(item.Id, item.Kind, item.IsCompleted, item.ScheduledAt, now, graceMinutes), StringComparer.Ordinal);
    }

    static IslandIndicatorState GetItemIndicatorState(string id, string kind, bool isCompleted, DateTime? scheduledAt, DateTime now, IReadOnlyDictionary<string, int> graceMinutes)
    {
        if (kind == "todo")
        {
            if (isCompleted) return IslandIndicatorState.Idle;
            if (scheduledAt is null) return IslandIndicatorState.PendingTodo;
            if (IsOverdue(scheduledAt.Value, now, graceMinutes.GetValueOrDefault(id, 5))) return IslandIndicatorState.OverdueTodo;
            return scheduledAt > now && scheduledAt <= now.AddHours(1)
                ? IslandIndicatorState.DueSoonTodo
                : IslandIndicatorState.PendingTodo;
        }

        return kind is "event" or "reminder" or "recurring"
            ? IslandIndicatorState.ReminderOnly
            : IslandIndicatorState.Idle;
    }

    Dictionary<string, int> TodoOverdueGraceMinutes()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT id,COALESCE(overdue_grace_minutes,5) FROM life_items WHERE kind='Todo' AND deleted_at IS NULL";
        using var reader = command.ExecuteReader();
        var values = new Dictionary<string, int>(StringComparer.Ordinal);
        while (reader.Read()) values[reader.GetString(0)] = Math.Max(0, reader.GetInt32(1));
        return values;
    }

    static bool IsOverdue(DateTime dueAt, DateTime now, int graceMinutes) => dueAt < now.AddMinutes(-graceMinutes);

    public AgendaItem? NextAgenda()
    {
        var now = localNow();
        using var db = Open();
        var values = new List<AgendaItem>();
        using (var todos = db.CreateCommand())
        {
            todos.CommandText = "SELECT id,title,notes,completed,due_at,remind_at FROM todos WHERE completed=0 AND due_at IS NOT NULL AND due_at >= $now";
            todos.Parameters.AddWithValue("$now", now.ToString("O"));
            using var reader = todos.ExecuteReader();
            while (reader.Read())
                values.Add(new(reader.GetString(0), "todo", reader.GetString(1), Text(reader, 2), ReadDate(reader, 4), null, Date(reader, 5), false));
        }
        using (var events = db.CreateCommand())
        {
            events.CommandText = "SELECT id,title,notes,start_at,end_at,remind_at FROM calendar_events WHERE end_at >= $now";
            events.Parameters.AddWithValue("$now", now.ToString("O"));
            using var reader = events.ExecuteReader();
            while (reader.Read())
                values.Add(new(reader.GetString(0), "event", reader.GetString(1), Text(reader, 2), ReadDate(reader, 3), ReadDate(reader, 4), Date(reader, 5), false));
        }
        values.AddRange(RecurringReminders()
            .Where(reminder => reminder.Recurrence is not RecurrenceKind.OfficialWorkdays and not RecurrenceKind.StatutoryHolidays)
            .Select(reminder => NextOccurrence(reminder, now))
            .Where(item => item is not null)
            .Cast<AgendaItem>());
        values.AddRange(NextSingleReminderItems(localNow()));
        return values.OrderBy(x => x.StartsAt).FirstOrDefault();
    }

    /// <summary>Returns the nearest long-running reminder occurrence for the Today dashboard only.</summary>
    public AgendaItem? NextLongTermReminder(DateTime now) =>
        NextRecurringAgendaItems(now).OrderBy(item => item.StartsAt).FirstOrDefault();
    public IReadOnlyList<AgendaItem> ReminderItems()
    {
        var values = new List<AgendaItem>();
        using var db = Open();
        using (var todos = db.CreateCommand())
        {
            todos.CommandText = "SELECT id,title,notes,completed,due_at,remind_at FROM todos WHERE completed=0 AND remind_at IS NOT NULL";
            using var reader = todos.ExecuteReader();
            while (reader.Read())
            {
                var remindAt = ReadDate(reader, 5);
                values.Add(new(reader.GetString(0), "todo", reader.GetString(1), Text(reader, 2),
                    Date(reader, 4) ?? remindAt, null, remindAt, false));
            }
        }
        using (var events = db.CreateCommand())
        {
            events.CommandText = "SELECT id,title,notes,start_at,end_at,remind_at FROM calendar_events WHERE remind_at IS NOT NULL";
            using var reader = events.ExecuteReader();
            while (reader.Read())
                values.Add(new(reader.GetString(0), "event", reader.GetString(1), Text(reader, 2), ReadDate(reader, 3), ReadDate(reader, 4), ReadDate(reader, 5), false));
        }
        values.AddRange(NextRecurringReminderItems(localNow()));
        values.AddRange(SingleReminderItems());
        return values;
    }

    public IReadOnlyList<AgendaItem> ClaimDueReminders(DateTime now)
    {
        var reminderCandidates = ReminderItems();
        var recurringCandidates = RecurringReminders();
        return writeQueue.Execute(unitOfWork =>
        {
            var due = new List<AgendaItem>();
            foreach (var item in reminderCandidates)
            {
                if (item.RemindAt is null || item.RemindAt > now) continue;
                using var command = unitOfWork.Connection.CreateCommand();
                command.Transaction = unitOfWork.Transaction;
                command.CommandText = item.Kind switch
                {
                    "todo" => "UPDATE todos SET notified_at=$now WHERE id=$id AND notified_at IS NULL AND completed=0",
                    "event" => "UPDATE calendar_events SET notified_at=$now WHERE id=$id AND notified_at IS NULL",
                    "reminder" => "UPDATE single_reminders SET notified_at=$now WHERE id=$id AND notified_at IS NULL",
                    _ => "SELECT 0"
                };
                command.Parameters.AddWithValue("$id", item.Id);
                command.Parameters.AddWithValue("$now", now.ToString("O"));
                if (command.ExecuteNonQuery() == 1) due.Add(item);
            }
            due.AddRange(ClaimDueRecurringReminders(
                unitOfWork.Connection,
                unitOfWork.Transaction,
                now,
                recurringCandidates));
            return (IReadOnlyList<AgendaItem>)due;
        });
    }

    public void Delete(string id)
    {
        var deletedAt = localNow();
        writeQueue.Execute(unitOfWork =>
        {
            using var command = unitOfWork.Connection.CreateCommand();
            command.Transaction = unitOfWork.Transaction;
            command.CommandText = "DELETE FROM todos WHERE id=$id";
            command.Parameters.AddWithValue("$id", id);
            if (command.ExecuteNonQuery() == 1)
                canonicalWriter.SoftDelete(unitOfWork.Connection, unitOfWork.Transaction, id, deletedAt);
        });
        RaiseAgendaChanged();
    }

    public IReadOnlyList<ChatSession> Sessions()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT id,title,created_at,updated_at FROM chat_sessions ORDER BY updated_at DESC";
        using var reader = command.ExecuteReader();
        var values = new List<ChatSession>();
        while (reader.Read()) values.Add(new(reader.GetString(0), reader.GetString(1), ReadDate(reader, 2), ReadDate(reader, 3)));
        return values;
    }

    public ChatSession NewSession()
    {
        var now = localNow();
        var session = new ChatSession(Guid.NewGuid().ToString("N"), "\u65b0\u5bf9\u8bdd", now, now);
        writeQueue.Execute(unitOfWork =>
        {
            using var command = unitOfWork.Connection.CreateCommand();
            command.Transaction = unitOfWork.Transaction;
            command.CommandText = "INSERT INTO chat_sessions VALUES($id,$title,$now,$now)";
            command.Parameters.AddWithValue("$id", session.Id);
            command.Parameters.AddWithValue("$title", session.Title);
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            command.ExecuteNonQuery();
        });
        return session;
    }

    public void DeleteSession(string sessionId)
    {
        writeQueue.Execute(unitOfWork =>
        {
            using var command = unitOfWork.Connection.CreateCommand();
            command.Transaction = unitOfWork.Transaction;
            command.CommandText = "DELETE FROM chat_messages WHERE session_id=$id; DELETE FROM assistant_actions WHERE session_id=$id; DELETE FROM chat_sessions WHERE id=$id";
            command.Parameters.AddWithValue("$id", sessionId);
            command.ExecuteNonQuery();
        });
    }

    public IReadOnlyList<ChatMessage> Messages(string sessionId)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT id,session_id,role,content,created_at FROM chat_messages WHERE session_id=$id ORDER BY created_at";
        command.Parameters.AddWithValue("$id", sessionId);
        using var reader = command.ExecuteReader();
        var values = new List<ChatMessage>();
        while (reader.Read()) values.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), ReadDate(reader, 4)));
        return values;
    }

    public void Message(string sessionId, string role, string text)
    {
        writeQueue.Execute(unitOfWork =>
        {
            using var command = unitOfWork.Connection.CreateCommand();
            command.Transaction = unitOfWork.Transaction;
            var now = localNow().ToString("O");
            command.CommandText = """
                INSERT INTO chat_messages VALUES($id,$session,$role,$text,$now);
                UPDATE chat_sessions SET title=CASE WHEN title=$newTitle THEN substr($text,1,24) ELSE title END,updated_at=$now WHERE id=$session
                """;
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            command.Parameters.AddWithValue("$session", sessionId);
            command.Parameters.AddWithValue("$role", role);
            command.Parameters.AddWithValue("$text", text);
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$newTitle", "\u65b0\u5bf9\u8bdd");
            command.ExecuteNonQuery();
        });
    }

    public AssistantAction SaveAction(string sessionId, string sourceText, string intentJson, string status, string? errorMessage = null, string? id = null)
    {
        var now = localNow();
        var action = new AssistantAction(id ?? Guid.NewGuid().ToString("N"), sessionId, sourceText, intentJson, status, errorMessage, now, now);
        writeQueue.Execute(unitOfWork =>
        {
            using var command = unitOfWork.Connection.CreateCommand();
            command.Transaction = unitOfWork.Transaction;
            command.CommandText = """
                INSERT INTO assistant_actions(id,session_id,source_text,intent_json,status,error_message,created_at,updated_at)
                VALUES($id,$session,$source,$intent,$status,$error,$created,$updated)
                ON CONFLICT(id) DO UPDATE SET source_text=$source,intent_json=$intent,status=$status,error_message=$error,updated_at=$updated
                """;
            command.Parameters.AddWithValue("$id", action.Id);
            command.Parameters.AddWithValue("$session", action.SessionId);
            command.Parameters.AddWithValue("$source", action.SourceText);
            command.Parameters.AddWithValue("$intent", action.IntentJson);
            command.Parameters.AddWithValue("$status", action.Status);
            command.Parameters.AddWithValue("$error", (object?)action.ErrorMessage ?? DBNull.Value);
            command.Parameters.AddWithValue("$created", action.CreatedAt.ToString("O"));
            command.Parameters.AddWithValue("$updated", action.UpdatedAt.ToString("O"));
            command.ExecuteNonQuery();
        });
        return action;
    }

    public AssistantAction? ActiveClarification(string sessionId)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT id,session_id,source_text,intent_json,status,error_message,created_at,updated_at FROM assistant_actions WHERE session_id=$session AND status='clarifying' ORDER BY updated_at DESC LIMIT 1";
        command.Parameters.AddWithValue("$session", sessionId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadAction(reader) : null;
    }

    public AssistantAction? ActiveHolidayReminderBatch(string sessionId)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT id,session_id,source_text,intent_json,status,error_message,created_at,updated_at FROM assistant_actions WHERE session_id=$session AND status='holiday_batch_time_pending' ORDER BY updated_at DESC LIMIT 1";
        command.Parameters.AddWithValue("$session", sessionId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadAction(reader) : null;
    }

    public AssistantAction? Action(string id)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT id,session_id,source_text,intent_json,status,error_message,created_at,updated_at FROM assistant_actions WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadAction(reader) : null;
    }

    public void SetActionStatus(string id, string status, string? errorMessage = null)
    {
        writeQueue.Execute(unitOfWork =>
        {
            using var command = unitOfWork.Connection.CreateCommand();
            command.Transaction = unitOfWork.Transaction;
            command.CommandText = "UPDATE assistant_actions SET status=$status,error_message=$error,updated_at=$updated WHERE id=$id";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$error", (object?)errorMessage ?? DBNull.Value);
            command.Parameters.AddWithValue("$updated", localNow().ToString("O"));
            command.ExecuteNonQuery();
        });
    }

    static AssistantAction ReadAction(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetString(4), Text(reader, 5), ReadDate(reader, 6), ReadDate(reader, 7));
    static string? Text(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    static DateTime? Date(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : ParseDate(reader.GetString(index));
    static void BindTodo(SqliteCommand command, TodoItem item)
    {
        command.Parameters.AddWithValue("$id", item.Id);
        command.Parameters.AddWithValue("$title", item.Title);
        command.Parameters.AddWithValue("$notes", (object?)item.Notes ?? DBNull.Value);
        command.Parameters.AddWithValue("$completed", item.IsCompleted ? 1 : 0);
        command.Parameters.AddWithValue("$due", (object?)StoreDate(item.DueAt) ?? DBNull.Value);
        command.Parameters.AddWithValue("$reminder", (object?)StoreDate(item.RemindAt) ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", item.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated", item.UpdatedAt.ToString("O"));
    }

    void RaiseAgendaChanged()
    {
        TodosChanged?.Invoke(this, EventArgs.Empty);
        AgendaChanged?.Invoke(this, EventArgs.Empty);
    }
}


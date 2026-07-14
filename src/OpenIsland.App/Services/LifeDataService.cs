using System.IO;
using Microsoft.Data.Sqlite;

namespace OpenIsland.App.Services;

public sealed partial class LifeDataService
{
    readonly string connectionString;
    public event EventHandler? AgendaChanged;
    public event EventHandler? TodosChanged;

    public LifeDataService(string? databasePath = null)
    {
        var path = databasePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenIsland");
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, "life-assistant.db");
        }
        connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        using var db = Open();
        Initialize(db);
    }

    SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        return db;
    }

    static void Initialize(SqliteConnection db)
    {
        Execute(db, """
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
            """);
        EnsureColumn(db, "todos", "notified_at", "TEXT");
        InitializeRecurring(db);
        InitializeSingleReminders(db);
    }

    static void Execute(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    static void EnsureColumn(SqliteConnection db, string table, string column, string type)
    {
        using var command = db.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
        Execute(db, $"ALTER TABLE {table} ADD COLUMN {column} {type}");
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
        var now = DateTime.Now;
        var item = new TodoItem(id ?? Guid.NewGuid().ToString("N"), title.Trim(), notes, completed, due, reminder, null, now, now);
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO todos(id,title,notes,completed,due_at,remind_at,notified_at,created_at,updated_at)
            VALUES($id,$title,$notes,$completed,$due,$reminder,NULL,$created,$updated)
            ON CONFLICT(id) DO UPDATE SET title=$title,notes=$notes,completed=$completed,due_at=$due,
              remind_at=$reminder,notified_at=NULL,updated_at=$updated
            """;
        BindTodo(command, item);
        command.ExecuteNonQuery();
        RaiseAgendaChanged();
        return item;
    }

    public void Complete(string id, bool value = true)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "UPDATE todos SET completed=$completed,updated_at=$updated WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$completed", value ? 1 : 0);
        command.Parameters.AddWithValue("$updated", DateTime.Now.ToString("O"));
        command.ExecuteNonQuery();
        RaiseAgendaChanged();
    }

    public CalendarEventItem SaveEvent(string title, string? notes, DateTime startsAt, DateTime endsAt, DateTime? reminderAt, string? id = null)
    {
        var now = DateTime.Now;
        var item = new CalendarEventItem(id ?? Guid.NewGuid().ToString("N"), title.Trim(), notes, startsAt, endsAt, reminderAt, null, now, now);
        using var db = Open();
        using var command = db.CreateCommand();
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
        RaiseAgendaChanged();
        return item;
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

    public IslandIndicatorState GetIslandIndicatorState(DateTime now)
    {
        var pendingTodos = Todos().Where(todo => !todo.IsCompleted).ToList();
        if (pendingTodos.Any(todo => todo.DueAt is not null && todo.DueAt <= now))
            return IslandIndicatorState.OverdueTodo;
        if (pendingTodos.Any(todo => todo.DueAt is not null && todo.DueAt > now && todo.DueAt <= now.AddHours(1)))
            return IslandIndicatorState.DueSoonTodo;
        if (pendingTodos.Count > 0)
            return IslandIndicatorState.PendingTodo;
        return HasActiveReminderOrEvent(now) ? IslandIndicatorState.ReminderOnly : IslandIndicatorState.Idle;
    }

    public IslandIndicatorState GetCalendarIndicatorState(DateTime day, DateTime now)
    {
        var items = AgendaFor(day).Where(item => item.Kind != "recurring").ToList();
        var pendingTodos = items.Where(item => item.Kind == "todo" && !item.IsCompleted).ToList();
        if (pendingTodos.Any(item => item.StartsAt <= now)) return IslandIndicatorState.OverdueTodo;
        if (pendingTodos.Any(item => item.StartsAt > now && item.StartsAt <= now.AddHours(1))) return IslandIndicatorState.DueSoonTodo;
        if (pendingTodos.Count > 0) return IslandIndicatorState.PendingTodo;
        return items.Any(item => item.Kind is "event" or "reminder")
            ? IslandIndicatorState.ReminderOnly
            : IslandIndicatorState.Idle;
    }
    bool HasActiveReminderOrEvent(DateTime now)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM calendar_events WHERE end_at >= $now";
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        if (Convert.ToInt64(command.ExecuteScalar()) > 0) return true;

        command.Parameters.Clear();
        command.CommandText = "SELECT COUNT(*) FROM single_reminders WHERE remind_at >= $now AND notified_at IS NULL";
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        if (Convert.ToInt64(command.ExecuteScalar()) > 0) return true;

        return false;
    }

    public AgendaItem? NextAgenda()
    {
        var today = DateTime.Today;
        using var db = Open();
        var values = new List<AgendaItem>();
        using (var todos = db.CreateCommand())
        {
            todos.CommandText = "SELECT id,title,notes,completed,due_at,remind_at FROM todos WHERE completed=0 AND due_at IS NOT NULL AND due_at >= $today";
            todos.Parameters.AddWithValue("$today", today.ToString("O"));
            using var reader = todos.ExecuteReader();
            while (reader.Read())
                values.Add(new(reader.GetString(0), "todo", reader.GetString(1), Text(reader, 2), ReadDate(reader, 4), null, Date(reader, 5), false));
        }
        using (var events = db.CreateCommand())
        {
            events.CommandText = "SELECT id,title,notes,start_at,end_at,remind_at FROM calendar_events WHERE end_at >= $today";
            events.Parameters.AddWithValue("$today", today.ToString("O"));
            using var reader = events.ExecuteReader();
            while (reader.Read())
                values.Add(new(reader.GetString(0), "event", reader.GetString(1), Text(reader, 2), ReadDate(reader, 3), ReadDate(reader, 4), Date(reader, 5), false));
        }
        values.AddRange(NextRecurringAgendaItems(DateTime.Now));
        values.AddRange(NextSingleReminderItems(DateTime.Now));
        return values.OrderBy(x => x.StartsAt).FirstOrDefault();
    }

    public IReadOnlyList<AgendaItem> ReminderItems()
    {
        var values = new List<AgendaItem>();
        using var db = Open();
        using (var todos = db.CreateCommand())
        {
            todos.CommandText = "SELECT id,title,notes,completed,due_at,remind_at FROM todos WHERE completed=0 AND remind_at IS NOT NULL";
            using var reader = todos.ExecuteReader();
            while (reader.Read())
                values.Add(new(reader.GetString(0), "todo", reader.GetString(1), Text(reader, 2), ReadDate(reader, 4), null, ReadDate(reader, 5), false));
        }
        using (var events = db.CreateCommand())
        {
            events.CommandText = "SELECT id,title,notes,start_at,end_at,remind_at FROM calendar_events WHERE remind_at IS NOT NULL";
            using var reader = events.ExecuteReader();
            while (reader.Read())
                values.Add(new(reader.GetString(0), "event", reader.GetString(1), Text(reader, 2), ReadDate(reader, 3), ReadDate(reader, 4), ReadDate(reader, 5), false));
        }
        values.AddRange(NextRecurringReminderItems(DateTime.Now));
        values.AddRange(SingleReminderItems());
        return values;
    }

    public IReadOnlyList<AgendaItem> ClaimDueReminders(DateTime now)
    {
        var due = new List<AgendaItem>();
        using var db = Open();
        foreach (var item in ReminderItems())
        {
            if (item.RemindAt is null || item.RemindAt > now) continue;
            using var command = db.CreateCommand();
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
        due.AddRange(ClaimDueRecurringReminders(db, now));
        return due;
    }

    public void Delete(string id)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "DELETE FROM todos WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
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
        var now = DateTime.Now;
        var session = new ChatSession(Guid.NewGuid().ToString("N"), "新对话", now, now);
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO chat_sessions VALUES($id,$title,$now,$now)";
        command.Parameters.AddWithValue("$id", session.Id);
        command.Parameters.AddWithValue("$title", session.Title);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.ExecuteNonQuery();
        return session;
    }

    public void DeleteSession(string sessionId)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "DELETE FROM chat_messages WHERE session_id=$id; DELETE FROM assistant_actions WHERE session_id=$id; DELETE FROM chat_sessions WHERE id=$id";
        command.Parameters.AddWithValue("$id", sessionId);
        command.ExecuteNonQuery();
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
        using var db = Open();
        using var command = db.CreateCommand();
        var now = DateTime.Now.ToString("O");
        command.CommandText = """
            INSERT INTO chat_messages VALUES($id,$session,$role,$text,$now);
            UPDATE chat_sessions SET title=CASE WHEN title='新对话' THEN substr($text,1,24) ELSE title END,updated_at=$now WHERE id=$session
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$role", role);
        command.Parameters.AddWithValue("$text", text);
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();
    }

    public AssistantAction SaveAction(string sessionId, string sourceText, string intentJson, string status, string? errorMessage = null, string? id = null)
    {
        var now = DateTime.Now;
        var action = new AssistantAction(id ?? Guid.NewGuid().ToString("N"), sessionId, sourceText, intentJson, status, errorMessage, now, now);
        using var db = Open();
        using var command = db.CreateCommand();
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
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "UPDATE assistant_actions SET status=$status,error_message=$error,updated_at=$updated WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$error", (object?)errorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", DateTime.Now.ToString("O"));
        command.ExecuteNonQuery();
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

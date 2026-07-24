using Microsoft.Data.Sqlite;
using OpenIsland.App.Services.Persistence;

namespace OpenIsland.App.Services.Productivity;

public sealed class ProductivitySchemaInitializer
{
    readonly IDbWriteQueue writeQueue;

    public ProductivitySchemaInitializer(IDbWriteQueue writeQueue) =>
        this.writeQueue = writeQueue ?? throw new ArgumentNullException(nameof(writeQueue));

    public void Initialize() => writeQueue.Execute(unitOfWork =>
    {
        var db = unitOfWork.Connection;
        var tx = unitOfWork.Transaction;
        AddColumn(db, tx, "priority", "TEXT NOT NULL DEFAULT 'Normal' CHECK(priority IN ('Low','Normal','High','Urgent'))");
        AddColumn(db, tx, "category", "TEXT CHECK(category IS NULL OR length(trim(category)) > 0)");
        AddColumn(db, tx, "estimated_minutes", "INTEGER CHECK(estimated_minutes IS NULL OR estimated_minutes > 0)");
        AddColumn(db, tx, "energy", "TEXT CHECK(energy IS NULL OR energy IN ('Low','Medium','High'))");
        AddColumn(db, tx, "parent_item_id", "TEXT CHECK(parent_item_id IS NULL OR parent_item_id <> id)");
        AddColumn(db, tx, "completed_at_utc", "TEXT CHECK(completed_at_utc IS NULL OR julianday(completed_at_utc) IS NOT NULL)");
        AddColumn(db, tx, "deferred_count", "INTEGER NOT NULL DEFAULT 0 CHECK(deferred_count >= 0)");
        AddColumn(db, tx, "overdue_grace_minutes", "INTEGER NOT NULL DEFAULT 5 CHECK(overdue_grace_minutes >= 0)");

        Execute(db, tx, """
            CREATE TABLE IF NOT EXISTS command_drafts(
                id TEXT PRIMARY KEY,
                status TEXT NOT NULL CHECK(status IN ('Pending','Confirmed','Cancelled','Expired')),
                created_at_utc TEXT NOT NULL CHECK(julianday(created_at_utc) IS NOT NULL),
                expires_at_utc TEXT NOT NULL CHECK(julianday(expires_at_utc) IS NOT NULL),
                confirmed_at_utc TEXT,
                CHECK(julianday(expires_at_utc) > julianday(created_at_utc)),
                CHECK(confirmed_at_utc IS NULL OR julianday(confirmed_at_utc) IS NOT NULL)
            );
            CREATE TABLE IF NOT EXISTS draft_items(
                id TEXT PRIMARY KEY,
                draft_id TEXT NOT NULL REFERENCES command_drafts(id) ON DELETE CASCADE,
                proposed_life_item_id TEXT NOT NULL,
                ordinal INTEGER NOT NULL CHECK(ordinal >= 0 AND ordinal < 10),
                title TEXT NOT NULL CHECK(length(trim(title)) > 0),
                due_at_utc TEXT CHECK(due_at_utc IS NULL OR julianday(due_at_utc) IS NOT NULL),
                priority TEXT NOT NULL DEFAULT 'Normal' CHECK(priority IN ('Low','Normal','High','Urgent')),
                category TEXT CHECK(category IS NULL OR length(trim(category)) > 0),
                estimated_minutes INTEGER CHECK(estimated_minutes IS NULL OR estimated_minutes > 0),
                energy TEXT CHECK(energy IS NULL OR energy IN ('Low','Medium','High')),
                proposed_status TEXT NOT NULL DEFAULT 'Pending' CHECK(proposed_status = 'Pending'),
                UNIQUE(draft_id, ordinal), UNIQUE(proposed_life_item_id)
            );
            CREATE TABLE IF NOT EXISTS focus_sessions(
                id TEXT PRIMARY KEY,
                item_id TEXT NOT NULL REFERENCES life_items(id),
                started_at_utc TEXT NOT NULL CHECK(julianday(started_at_utc) IS NOT NULL),
                intended_minutes INTEGER NOT NULL CHECK(intended_minutes > 0),
                ended_at_utc TEXT CHECK(ended_at_utc IS NULL OR julianday(ended_at_utc) IS NOT NULL),
                actual_minutes INTEGER CHECK(actual_minutes IS NULL OR actual_minutes >= 0),
                paused_at_utc TEXT CHECK(paused_at_utc IS NULL OR julianday(paused_at_utc) IS NOT NULL),
                accumulated_paused_seconds INTEGER NOT NULL DEFAULT 0 CHECK(accumulated_paused_seconds >= 0),
                CHECK((ended_at_utc IS NULL AND actual_minutes IS NULL) OR
                      (ended_at_utc IS NOT NULL AND actual_minutes IS NOT NULL AND
                       julianday(ended_at_utc) >= julianday(started_at_utc)))
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_focus_sessions_one_active
                ON focus_sessions((1)) WHERE ended_at_utc IS NULL;
            """);
        EnsureFocusColumn(db, tx, "paused_at_utc", "TEXT");
        EnsureFocusColumn(db, tx, "accumulated_paused_seconds", "INTEGER NOT NULL DEFAULT 0");
    });

    static void AddColumn(SqliteConnection db, SqliteTransaction tx, string name, string definition)
    {
        using var info = db.CreateCommand();
        info.Transaction = tx;
        info.CommandText = "PRAGMA table_info(life_items)";
        using var reader = info.ExecuteReader();
        while (reader.Read()) if (string.Equals(reader.GetString(1), name, StringComparison.OrdinalIgnoreCase)) return;
        reader.Close();
        Execute(db, tx, $"ALTER TABLE life_items ADD COLUMN {name} {definition}");
    }

    static void EnsureFocusColumn(SqliteConnection db, SqliteTransaction tx, string name, string definition)
    {
        using var info = db.CreateCommand();
        info.Transaction = tx;
        info.CommandText = "PRAGMA table_info(focus_sessions)";
        using var reader = info.ExecuteReader();
        while (reader.Read()) if (string.Equals(reader.GetString(1), name, StringComparison.OrdinalIgnoreCase)) return;
        reader.Close();
        Execute(db, tx, $"ALTER TABLE focus_sessions ADD COLUMN {name} {definition}");
    }

    static void Execute(SqliteConnection db, SqliteTransaction tx, string sql)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

using Microsoft.Data.Sqlite;
using OpenIsland.App.Services.Persistence;

namespace OpenIsland.Tests;

public sealed class LifeSchemaMigratorTests
{
    static readonly DateTimeOffset FixedNow = new(2026, 7, 16, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void LifeItemChecks_RejectNullEventEndAndUnknownKind()
    {
        using var db = OpenMemoryDatabase();
        Migrator().Migrate(db);

        var nullEnd = Assert.Throws<SqliteException>(() => Execute(db, """
            INSERT INTO life_items(
                id,kind,title,status,row_version,
                start_local_datetime,start_utc_instant,start_iana_time_zone_id,start_time_semantics,
                end_local_datetime,end_utc_instant,end_iana_time_zone_id,end_time_semantics,
                origin_type,is_readonly,created_at,updated_at)
            VALUES(
                'event-null-end','Event','Invalid event','Pending',1,
                '2026-07-16T09:00:00','2026-07-16T09:00:00Z','Etc/UTC','ZonedWallClock',
                '2026-07-16T10:00:00',NULL,'Etc/UTC','ZonedWallClock',
                'Local',0,'2026-07-16T00:00:00Z','2026-07-16T00:00:00Z')
            """));
        Assert.Equal(19, nullEnd.SqliteErrorCode);

        var unknownKind = Assert.Throws<SqliteException>(() => Execute(db, """
            INSERT INTO life_items(id,kind,title,status,row_version,origin_type,is_readonly,created_at,updated_at)
            VALUES('unknown','Unknown','Invalid kind','Pending',1,'Local',0,
                   '2026-07-16T00:00:00Z','2026-07-16T00:00:00Z')
            """));
        Assert.Equal(19, unknownKind.SqliteErrorCode);
    }

    [Fact]
    public void Migration_CopiesEveryLegacyKindAndIsIdempotent()
    {
        using var db = OpenMemoryDatabase();
        Execute(db, """
            CREATE TABLE todos(
                id TEXT PRIMARY KEY,title TEXT NOT NULL,notes TEXT,completed INTEGER NOT NULL,
                due_at TEXT,remind_at TEXT,created_at TEXT NOT NULL,updated_at TEXT NOT NULL);
            CREATE TABLE calendar_events(
                id TEXT PRIMARY KEY,title TEXT NOT NULL,notes TEXT,start_at TEXT NOT NULL,end_at TEXT NOT NULL,
                remind_at TEXT,created_at TEXT NOT NULL,updated_at TEXT NOT NULL);
            CREATE TABLE single_reminders(
                id TEXT PRIMARY KEY,title TEXT NOT NULL,notes TEXT,remind_at TEXT NOT NULL,
                created_at TEXT NOT NULL,updated_at TEXT NOT NULL);
            CREATE TABLE recurring_reminders(
                id TEXT PRIMARY KEY,title TEXT NOT NULL,notes TEXT,reminder_time TEXT NOT NULL,
                recurrence TEXT NOT NULL,weekdays TEXT,created_at TEXT NOT NULL,updated_at TEXT NOT NULL);

            INSERT INTO todos VALUES(
                'todo-1','Todo',NULL,0,'2026-07-17T09:00:00','2026-07-17T08:30:00',
                '2026-07-15T00:00:00Z','2026-07-15T00:00:00Z');
            INSERT INTO calendar_events VALUES(
                'event-1','Event',NULL,'2026-07-17T10:00:00','2026-07-17T11:00:00',NULL,
                '2026-07-15T00:00:00Z','2026-07-15T00:00:00Z');
            INSERT INTO single_reminders VALUES(
                'reminder-1','Reminder',NULL,'2026-07-17T12:00:00',
                '2026-07-15T00:00:00Z','2026-07-15T00:00:00Z');
            INSERT INTO recurring_reminders VALUES(
                'series-1','Weekly',NULL,'09:00','Weekly','1,5',
                '2026-07-15T00:00:00Z','2026-07-15T00:00:00Z');
            """);

        var migrator = Migrator();
        var first = migrator.Migrate(db);
        var second = migrator.Migrate(db);

        Assert.Equal(4, first.MigratedItems);
        Assert.Equal(1, first.MigratedRecurrenceRules);
        Assert.Equal(0, second.MigratedItems);
        Assert.Equal(0, second.MigratedRecurrenceRules);
        Assert.Equal(4, Count(db, "life_items"));
        Assert.Equal(1, Count(db, "recurrence_rules"));
        Assert.Equal(4, Count(db, "migration_audit"));
        Assert.Equal(1, Count(db, "schema_migrations"));
    }

    [Fact]
    public void Migration_WhenOneLegacyRowIsInvalid_RollsBackTheWholeSchemaAndCopy()
    {
        using var db = OpenMemoryDatabase();
        Execute(db, """
            CREATE TABLE calendar_events(
                id TEXT PRIMARY KEY,title TEXT NOT NULL,notes TEXT,start_at TEXT NOT NULL,end_at TEXT NOT NULL,
                remind_at TEXT,created_at TEXT NOT NULL,updated_at TEXT NOT NULL);
            INSERT INTO calendar_events VALUES(
                'bad-event','Bad event',NULL,'2026-07-17T11:00:00','2026-07-17T10:00:00',NULL,
                '2026-07-15T00:00:00Z','2026-07-15T00:00:00Z');
            """);

        Assert.Throws<SqliteException>(() => Migrator().Migrate(db));

        Assert.False(TableExists(db, "life_items"));
        Assert.False(TableExists(db, "schema_migrations"));
        Assert.True(TableExists(db, "calendar_events"));
        Assert.Equal(1, Count(db, "calendar_events"));
    }

    static LifeSchemaMigrator Migrator() => new("Etc/UTC", () => FixedNow);

    static SqliteConnection OpenMemoryDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        return connection;
    }

    static void Execute(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    static long Count(SqliteConnection db, string table)
    {
        using var command = db.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (long)command.ExecuteScalar()!;
    }

    static bool TableExists(SqliteConnection db, string table)
    {
        using var command = db.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name";
        command.Parameters.AddWithValue("$name", table);
        return (long)command.ExecuteScalar()! > 0;
    }
}

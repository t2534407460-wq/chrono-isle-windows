using Microsoft.Data.Sqlite;
using ChronoIsle.App.Services.Persistence;

namespace ChronoIsle.Tests;

public sealed class LifeSchemaMigratorOffsetTests
{
    [Fact]
    public void Migration_PreservesTheOriginalWallClockWhenLegacyTextHasAnOffset()
    {
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        using (var setup = db.CreateCommand())
        {
            setup.CommandText = """
                CREATE TABLE todos(
                    id TEXT PRIMARY KEY,title TEXT NOT NULL,notes TEXT,completed INTEGER NOT NULL,
                    due_at TEXT,remind_at TEXT,created_at TEXT NOT NULL,updated_at TEXT NOT NULL);
                INSERT INTO todos VALUES(
                    'offset-todo','跨时区事项',NULL,0,'2026-07-17T09:30:00+09:00',NULL,
                    '2026-07-15T00:00:00Z','2026-07-15T00:00:00Z');
                """;
            setup.ExecuteNonQuery();
        }

        new LifeSchemaMigrator("Etc/UTC").Migrate(db);

        using var query = db.CreateCommand();
        query.CommandText = "SELECT due_local_datetime FROM life_items WHERE id='offset-todo'";
        var local = Assert.IsType<string>(query.ExecuteScalar());
        Assert.StartsWith("2026-07-17T09:30:00", local, StringComparison.Ordinal);
    }
}

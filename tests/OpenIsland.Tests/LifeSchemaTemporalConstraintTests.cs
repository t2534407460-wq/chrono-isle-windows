using Microsoft.Data.Sqlite;
using OpenIsland.App.Services.Persistence;

namespace OpenIsland.Tests;

public sealed class LifeSchemaTemporalConstraintTests
{
    [Fact]
    public void DeviceLocalFloatingTime_DoesNotPersistAFixedZone()
    {
        using var db = OpenSchema();
        using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO life_items(
                id,kind,title,status,row_version,
                due_local_datetime,due_utc_instant,due_time_semantics,
                origin_type,is_readonly,created_at,updated_at)
            VALUES(
                'floating','Todo','当地九点','Pending',1,
                '2026-07-17T09:00:00','2026-07-17T01:00:00Z','DeviceLocalFloatingWallClock',
                'Local',0,'2026-07-16T00:00:00Z','2026-07-16T00:00:00Z')
            """;

        Assert.Equal(1, command.ExecuteNonQuery());
    }

    [Fact]
    public void EditableEvent_RejectsMixedStartAndEndSemantics()
    {
        using var db = OpenSchema();
        using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO life_items(
                id,kind,title,status,row_version,
                start_local_datetime,start_utc_instant,start_iana_time_zone_id,start_time_semantics,
                end_utc_instant,end_time_semantics,
                origin_type,is_readonly,created_at,updated_at)
            VALUES(
                'mixed-event','Event','混合语义','Pending',1,
                '2026-07-17T09:00:00','2026-07-17T01:00:00Z','Asia/Shanghai','ZonedWallClock',
                '2026-07-17T02:00:00Z','AbsoluteInstant',
                'Local',0,'2026-07-16T00:00:00Z','2026-07-16T00:00:00Z')
            """;

        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
    }

    static SqliteConnection OpenSchema()
    {
        var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        new LifeSchemaMigrator("Etc/UTC").Migrate(db);
        return db;
    }
}

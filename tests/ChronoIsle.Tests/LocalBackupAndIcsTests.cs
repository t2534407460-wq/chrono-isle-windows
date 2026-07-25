using Microsoft.Data.Sqlite;
using ChronoIsle.App.Services.Domain;
using ChronoIsle.App.Services.Persistence;
using ChronoIsle.App.Services.Sync;

namespace ChronoIsle.Tests;

public sealed class LocalBackupAndIcsTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "chrono-isle-m5-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Online_backup_contains_latest_wal_commit()
    {
        var (factory, queue) = CreateDatabase("source.db");
        Import(queue, BasicTodo("wal-latest", "刚刚提交"));
        var backup = Path.Combine(directory, "backup.db");

        new SqliteOnlineBackupService(factory, queue).Create(backup, LocalBackupKind.MigrationSafety);

        using var connection = Open(backup);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT title FROM life_items WHERE title='刚刚提交'";
        Assert.Equal("刚刚提交", command.ExecuteScalar());
    }

    [Fact]
    public void Portable_backup_removes_authorization_material_and_requires_reauthentication()
    {
        var (factory, queue) = CreateDatabase("portable-source.db");
        queue.Execute(uow =>
        {
            using var command = uow.Connection.CreateCommand();
            command.Transaction = uow.Transaction;
            command.CommandText = """
                CREATE TABLE sync_accounts(id TEXT PRIMARY KEY,status TEXT,access_token TEXT,delta_link TEXT,msal_cache TEXT);
                INSERT INTO sync_accounts VALUES('a','Connected','token','delta','cache');
                """;
            command.ExecuteNonQuery();
        });
        var backup = Path.Combine(directory, "portable.db");

        var manifest = new SqliteOnlineBackupService(factory, queue).Create(backup, LocalBackupKind.UserPortable);

        Assert.False(manifest.ContainsAccountTokens);
        Assert.False(manifest.ContainsDeltaLinks);
        Assert.False(manifest.ContainsMsalCache);
        Assert.True(manifest.RequiresExternalAccountReauthentication);
        using var connection = Open(backup);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT status,access_token,delta_link,msal_cache FROM sync_accounts WHERE id='a'";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("ReauthRequired", reader.GetString(0));
        Assert.True(reader.IsDBNull(1));
        Assert.True(reader.IsDBNull(2));
        Assert.True(reader.IsDBNull(3));
    }

    [Fact]
    public void Ics_supported_weekly_rule_round_trips_with_crlf_and_escaping()
    {
        var (sourceFactory, sourceQueue) = CreateDatabase("ics-source.db");
        var input = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VTODO\r\nUID:weekly-1\r\nSUMMARY:周报\\, 复盘\r\nDUE;TZID=Asia/Shanghai:20260717T180000\r\nRRULE:FREQ=WEEKLY;INTERVAL=1;BYDAY=FR\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";
        var imported = Import(sourceQueue, input);
        Assert.Equal(0, imported.ReadOnlyMirrorCount);

        var exported = new IcsExportService(sourceFactory).Export();

        Assert.Contains("\r\n", exported);
        Assert.Contains("X-CHRONO-ISLE-KIND:TODO", exported);
        Assert.Contains("SUMMARY:周报\\, 复盘", exported);
        Assert.Contains("RRULE:FREQ=WEEKLY;INTERVAL=1;BYDAY=FR", exported);
        var (targetFactory, targetQueue) = CreateDatabase("ics-target.db");
        var roundTrip = Import(targetQueue, exported);
        Assert.Equal(1, roundTrip.ImportedCount);
        using var connection = targetFactory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM recurrence_rules WHERE frequency='Weekly' AND weekdays='5'";
        Assert.Equal(1L, command.ExecuteScalar());
    }

    [Fact]
    public void Unknown_rrule_is_preserved_as_read_only_mirror()
    {
        var (factory, queue) = CreateDatabase("unknown.db");
        var ics = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:unknown\r\nSUMMARY:复杂规则\r\nDTSTART;TZID=Asia/Shanghai:20260717T090000\r\nDTEND;TZID=Asia/Shanghai:20260717T100000\r\nRRULE:FREQ=YEARLY;BYSETPOS=1\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        var result = Import(queue, ics);

        Assert.Equal(1, result.ReadOnlyMirrorCount);
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT is_readonly,readonly_reason,raw_external_payload_id FROM life_items";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.False(string.IsNullOrWhiteSpace(reader.GetString(1)));
        Assert.False(string.IsNullOrWhiteSpace(reader.GetString(2)));
        Assert.True(ExternalMirrorPolicy.IsAllowed(true, ExternalMirrorOperation.Hide));
        Assert.False(ExternalMirrorPolicy.IsAllowed(true, ExternalMirrorOperation.Complete));
    }

    [Fact]
    public void Date_only_all_day_event_is_preserved_as_read_only_instead_of_editable_midnight_times()
    {
        var (factory, queue) = CreateDatabase("all-day.db");
        var ics = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:all-day\r\nSUMMARY:全天事项\r\nDTSTART;VALUE=DATE:20260717\r\nDTEND;VALUE=DATE:20260718\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        var result = Import(queue, ics);

        Assert.Equal(1, result.ReadOnlyMirrorCount);
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT is_readonly,readonly_reason,raw_external_payload_id FROM life_items";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Contains("all-day", reader.GetString(1), StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(reader.GetString(2)));
    }

    (SqliteConnectionFactory Factory, SqliteDbWriteQueue Queue) CreateDatabase(string name)
    {
        Directory.CreateDirectory(directory);
        var factory = new SqliteConnectionFactory(Path.Combine(directory, name));
        using var connection = factory.OpenConnection();
        new LifeSchemaMigrator("Asia/Shanghai").Migrate(connection);
        return (factory, new SqliteDbWriteQueue(factory));
    }

    static IcsImportResult Import(IDbWriteQueue queue, string ics) =>
        new IcsImportService(queue, new OccurrenceTimeResolver(new SystemTimeZoneCatalog())).Import(ics);

    static string BasicTodo(string uid, string title) =>
        $"BEGIN:VCALENDAR\r\nBEGIN:VTODO\r\nUID:{uid}\r\nSUMMARY:{title}\r\nDUE:20260717T090000Z\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";

    static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        return connection;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}

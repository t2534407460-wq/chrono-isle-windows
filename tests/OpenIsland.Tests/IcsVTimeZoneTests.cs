using Microsoft.Data.Sqlite;
using OpenIsland.App.Services.Domain;
using OpenIsland.App.Services.Persistence;
using OpenIsland.App.Services.Sync;

namespace OpenIsland.Tests;

public sealed class IcsVTimeZoneTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "open-island-ics-zone-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Export_includes_a_vtimezone_for_each_referenced_zoned_wall_clock()
    {
        var (factory, queue) = CreateDatabase();
        const string ics = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:shanghai\r\nSUMMARY:Morning meeting\r\nDTSTART;TZID=Asia/Shanghai:20260717T090000\r\nDTEND;TZID=Asia/Shanghai:20260717T100000\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        new IcsImportService(queue, new OccurrenceTimeResolver(new SystemTimeZoneCatalog())).Import(ics);

        var exported = new IcsExportService(factory).Export();

        Assert.Contains("BEGIN:VTIMEZONE\r\nTZID:Asia/Shanghai", exported, StringComparison.Ordinal);
        Assert.Contains("BEGIN:STANDARD", exported, StringComparison.Ordinal);
        Assert.Contains("END:VTIMEZONE", exported, StringComparison.Ordinal);
        Assert.Contains("DTSTART;TZID=Asia/Shanghai:20260717T090000", exported, StringComparison.Ordinal);
    }

    (SqliteConnectionFactory Factory, SqliteDbWriteQueue Queue) CreateDatabase()
    {
        Directory.CreateDirectory(directory);
        var factory = new SqliteConnectionFactory(Path.Combine(directory, "ics.db"));
        using var connection = factory.OpenConnection();
        new LifeSchemaMigrator("Asia/Shanghai").Migrate(connection);
        return (factory, new SqliteDbWriteQueue(factory));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}

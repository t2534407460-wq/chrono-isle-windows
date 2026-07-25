using System.Text;
using Microsoft.Data.Sqlite;
using ChronoIsle.App.Services.Domain;
using ChronoIsle.App.Services.Persistence;
using ChronoIsle.App.Services.Sync;

namespace ChronoIsle.Tests;

public sealed class IcsHardeningTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "chrono-isle-ics-hardening-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Recurrence_with_exdate_is_preserved_as_read_only_mirror()
    {
        var (factory, queue) = CreateDatabase("exdate.db");
        const string ics = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:exceptions\r\nSUMMARY:Weekly review\r\nDTSTART;TZID=Asia/Shanghai:20260717T090000\r\nDTEND;TZID=Asia/Shanghai:20260717T100000\r\nRRULE:FREQ=WEEKLY;BYDAY=FR\r\nEXDATE;TZID=Asia/Shanghai:20260724T090000\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        var result = new IcsImportService(queue, new OccurrenceTimeResolver(new SystemTimeZoneCatalog())).Import(ics);

        Assert.Equal(1, result.ReadOnlyMirrorCount);
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT is_readonly,readonly_reason FROM life_items";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Contains("EXDATE", reader.GetString(1), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Export_folds_long_utf8_content_without_splitting_a_rune()
    {
        var (factory, queue) = CreateDatabase("fold.db");
        var title = string.Concat(Enumerable.Repeat("\u4efb\u52a1", 80));
        var ics = $"BEGIN:VCALENDAR\r\nBEGIN:VTODO\r\nUID:fold\r\nSUMMARY:{title}\r\nDUE:20260717T090000Z\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";
        new IcsImportService(queue, new OccurrenceTimeResolver(new SystemTimeZoneCatalog())).Import(ics);

        var exported = new IcsExportService(factory).Export();

        Assert.All(exported.Split("\r\n", StringSplitOptions.RemoveEmptyEntries),
            line => Assert.InRange(Encoding.UTF8.GetByteCount(line), 1, 75));
        Assert.Contains("\r\n ", exported);
    }

    (SqliteConnectionFactory Factory, SqliteDbWriteQueue Queue) CreateDatabase(string name)
    {
        Directory.CreateDirectory(directory);
        var factory = new SqliteConnectionFactory(Path.Combine(directory, name));
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

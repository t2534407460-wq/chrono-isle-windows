using Microsoft.Data.Sqlite;
using OpenIsland.App.Services.Persistence;
using OpenIsland.App.Services.Scheduling;

namespace OpenIsland.Tests;

public sealed class OccurrenceOverrideStoreTests
{
    [Fact]
    public void SameOccurrenceIsReplacedWithoutChangingTheSeriesRule()
    {
        var path = Path.Combine(Path.GetTempPath(), $"open-island-override-{Guid.NewGuid():N}.db");
        try
        {
            var queue = new SqliteDbWriteQueue(new SqliteConnectionFactory(path));
            _ = new ReminderDeliveryStore(queue);
            var store = new OccurrenceOverrideStore(queue);
            var key = new OccurrenceOverrideKey("series", 1, new DateTime(2026, 7, 31, 9, 0, 0), new DateTimeOffset(2026, 7, 31, 1, 0, 0, TimeSpan.Zero), "Asia/Shanghai");
            store.Save(new(key, OccurrenceOverrideType.Snooze, key.OriginalStartUtc.AddMinutes(10), null, key.OriginalStartUtc));
            store.Save(new(key, OccurrenceOverrideType.Skip, null, null, key.OriginalStartUtc.AddMinutes(1)));

            using var connection = new SqliteConnection($"Data Source={path}"); connection.Open();
            using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*),override_type FROM occurrence_overrides";
            using var reader = command.ExecuteReader(); Assert.True(reader.Read());
            Assert.Equal(1L, reader.GetInt64(0)); Assert.Equal("Skip", reader.GetString(1));
        }
        finally { SqliteConnection.ClearAllPools(); foreach (var item in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(item)) File.Delete(item); }
    }
}
using System.IO;
using Microsoft.Data.Sqlite;
using ChronoIsle.App;
using ChronoIsle.App.Services;

namespace ChronoIsle.Tests;

public sealed class LifeDataConcurrencyTests
{
    [Fact]
    public async Task TwoInstances_SerializeConcurrentWritesAcrossAllLegacyStores()
    {
        using var scope = new TempDatabase();
        var instances = await Task.WhenAll(
            Task.Run(() => new LifeDataService(scope.Path)),
            Task.Run(() => new LifeDataService(scope.Path)));
        var first = instances[0];
        var second = instances[1];
        var sessionA = first.NewSession();
        var sessionB = second.NewSession();
        var future = DateTime.Today.AddDays(1).AddHours(8);
        var due = DateTime.Now.AddMinutes(-5);
        var recurringTime = TimeOnly.FromDateTime(DateTime.Now.AddMinutes(5));

        var writes = new List<Task>();
        writes.AddRange(Enumerable.Range(0, 30).Select(index => Task.Run(() =>
            (index % 2 == 0 ? first : second).Save($"todo-{index}", null, future.AddMinutes(index), null, $"todo-{index}"))));
        writes.AddRange(Enumerable.Range(0, 10).Select(index => Task.Run(() =>
            (index % 2 == 0 ? first : second).SaveEvent(
                $"event-{index}", null, future.AddHours(index), future.AddHours(index).AddMinutes(30), null, $"event-{index}"))));
        writes.AddRange(Enumerable.Range(0, 10).Select(index => Task.Run(() =>
            (index % 2 == 0 ? first : second).SaveReminder($"reminder-{index}", null, due, $"reminder-{index}"))));
        writes.AddRange(Enumerable.Range(0, 5).Select(index => Task.Run(() =>
            (index % 2 == 0 ? first : second).SaveRecurringReminder(
                $"recurring-{index}", null, recurringTime, RecurrenceKind.Daily, [], $"recurring-{index}"))));
        writes.AddRange(Enumerable.Range(0, 20).Select(index => Task.Run(() =>
            (index % 2 == 0 ? first : second).Message(index % 2 == 0 ? sessionA.Id : sessionB.Id, "user", $"message-{index}"))));
        writes.AddRange(Enumerable.Range(0, 20).Select(index => Task.Run(() =>
            (index % 2 == 0 ? first : second).SaveAction(
                index % 2 == 0 ? sessionA.Id : sessionB.Id,
                $"source-{index}", "{}", "pending", id: $"action-{index}"))));

        await Task.WhenAll(writes);

        var claimed = await Task.WhenAll(
            Task.Run(() => first.ClaimDueReminders(DateTime.Now)),
            Task.Run(() => second.ClaimDueReminders(DateTime.Now)));
        var claimedSingleReminders = claimed.SelectMany(items => items).Where(item => item.Kind == "reminder").ToList();
        Assert.Equal(10, claimedSingleReminders.Count);
        Assert.Equal(10, claimedSingleReminders.Select(item => item.Id).Distinct().Count());

        using var db = Open(scope.Path);
        Assert.Equal(30, Scalar(db, "SELECT COUNT(*) FROM todos"));
        Assert.Equal(10, Scalar(db, "SELECT COUNT(*) FROM calendar_events"));
        Assert.Equal(10, Scalar(db, "SELECT COUNT(*) FROM single_reminders"));
        Assert.Equal(5, Scalar(db, "SELECT COUNT(*) FROM recurring_reminders"));
        Assert.Equal(20, Scalar(db, "SELECT COUNT(*) FROM chat_messages"));
        Assert.Equal(20, Scalar(db, "SELECT COUNT(*) FROM assistant_actions"));
        Assert.Equal(55, Scalar(db, "SELECT COUNT(*) FROM life_items WHERE deleted_at IS NULL"));
        Assert.Equal(5, Scalar(db, "SELECT COUNT(*) FROM recurrence_rules WHERE deleted_at IS NULL"));
        Assert.Equal(1, Scalar(db, "SELECT COUNT(*) FROM schema_migrations"));
    }

    [Fact]
    public void CanonicalDualWrite_TracksCreateUpdateCompleteAndSoftDelete()
    {
        using var scope = new TempDatabase();
        var data = new LifeDataService(scope.Path);
        var due = DateTime.Today.AddDays(1).AddHours(9);
        data.Save("first title", "notes", due, due.AddMinutes(-10), "todo-1");

        using (var db = Open(scope.Path))
        {
            Assert.Equal("first title", Text(db, "SELECT title FROM life_items WHERE id='todo-1'"));
            Assert.Equal("Pending", Text(db, "SELECT status FROM life_items WHERE id='todo-1'"));
            Assert.Equal(1, Scalar(db, "SELECT row_version FROM life_items WHERE id='todo-1'"));
        }

        data.Save("updated title", "updated notes", due.AddHours(1), null, "todo-1");
        data.Complete("todo-1");
        using (var db = Open(scope.Path))
        {
            Assert.Equal(0, Scalar(db, "SELECT COUNT(*) FROM todos WHERE id='todo-1'"));
            Assert.Equal("updated title", Text(db, "SELECT title FROM archived_todos WHERE id='todo-1'"));
            Assert.Equal("updated title", Text(db, "SELECT title FROM life_items WHERE id='todo-1'"));
            Assert.Equal("Completed", Text(db, "SELECT status FROM life_items WHERE id='todo-1'"));
            Assert.Equal(1, Scalar(db, "SELECT COUNT(*) FROM life_items WHERE id='todo-1' AND deleted_at IS NOT NULL"));
            Assert.Equal(4, Scalar(db, "SELECT row_version FROM life_items WHERE id='todo-1'"));
        }

        var startsAt = due.AddHours(2);
        data.SaveEvent("event", null, startsAt, startsAt.AddHours(1), null, "event-1");
        data.SaveReminder("reminder", null, due, "reminder-1");
        data.SaveRecurringReminder("daily", null, new TimeOnly(9, 0), RecurrenceKind.Daily, [], "recurring-1");
        using (var db = Open(scope.Path))
        {
            Assert.Equal("Event", Text(db, "SELECT kind FROM life_items WHERE id='event-1'"));
            Assert.Equal("Reminder", Text(db, "SELECT kind FROM life_items WHERE id='reminder-1'"));
            Assert.Equal("Reminder", Text(db, "SELECT kind FROM life_items WHERE id='recurring-1'"));
            Assert.Equal(1, Scalar(db, "SELECT COUNT(*) FROM recurrence_rules WHERE series_item_id='recurring-1' AND deleted_at IS NULL"));
        }

        data.Delete("todo-1");
        using var deletedDb = Open(scope.Path);
        Assert.Equal(0, Scalar(deletedDb, "SELECT COUNT(*) FROM todos WHERE id='todo-1'"));
        Assert.Equal(1, Scalar(deletedDb, "SELECT COUNT(*) FROM life_items WHERE id='todo-1' AND deleted_at IS NOT NULL"));
        Assert.Equal(4, Scalar(deletedDb, "SELECT row_version FROM life_items WHERE id='todo-1'"));
    }

    static SqliteConnection Open(string path)
    {
        var db = new SqliteConnection($"Data Source={path}");
        db.Open();
        return db;
    }

    static long Scalar(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    static string Text(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar()) ?? "";
    }

    sealed class TempDatabase : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"chrono-isle-concurrency-{Guid.NewGuid():N}.db");

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { Path, Path + "-wal", Path + "-shm" })
                if (File.Exists(path)) File.Delete(path);
        }
    }
}

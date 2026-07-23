using System.IO;
using Microsoft.Data.Sqlite;
using OpenIsland.App;
using OpenIsland.App.Services;

namespace OpenIsland.Tests;

public sealed class M0AAgendaRegressionTests
{
    [Fact]
    public void ReminderOnlyTodo_UsesReminderTimeWithoutReadingANullDueDate()
    {
        using var database = new TempDatabase();
        var data = new LifeDataService(database.Path);
        var reminder = DateTime.Now.AddHours(1);
        data.Save("喝水", null, null, reminder, "reminder-only-todo");

        var item = Assert.Single(data.ReminderItems().Where(value => value.Id == "reminder-only-todo"));

        Assert.Equal(reminder, item.StartsAt);
        Assert.Equal(reminder, item.RemindAt);
    }

    [Fact]
    public void NextAgenda_DoesNotReturnAnItemThatEndedEarlierToday()
    {
        using var database = new TempDatabase();
        var data = new LifeDataService(database.Path);
        var now = DateTime.Now;
        data.Save("已过去", null, now.AddHours(-2), null, "past-todo");
        data.Save("下一项", null, now.AddHours(1), null, "future-todo");
        data.SaveEvent("已结束", null, now.AddHours(-2), now.AddHours(-1), null, "past-event");

        var next = data.NextAgenda();

        Assert.NotNull(next);
        Assert.Equal("future-todo", next.Id);
        Assert.True(next.StartsAt > now);
    }

    [Fact]
    public void NextAgenda_DoesNotPromoteOfficialSleepSchedulesAsTheNextAction()
    {
        using var database = new TempDatabase();
        var data = new LifeDataService(database.Path);

        data.SaveOfficialSleepReminderSchedule(new TimeOnly(0, 0), new TimeOnly(1, 0));

        Assert.Null(data.NextAgenda());
    }

    sealed class TempDatabase : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"open-island-agenda-{Guid.NewGuid():N}.db");

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { Path, Path + "-wal", Path + "-shm" })
                if (File.Exists(path)) File.Delete(path);
        }
    }
}

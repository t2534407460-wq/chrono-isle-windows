using Microsoft.Data.Sqlite;
using ChronoIsle.App;
using ChronoIsle.App.Services;

namespace ChronoIsle.Tests;

public sealed class ReminderSnoozeTests : IDisposable
{
    readonly string path = Path.Combine(Path.GetTempPath(), $"chrono-isle-snooze-{Guid.NewGuid():N}.db");

    [Fact]
    public void Snooze_one_off_reminder_resets_delivery_without_changing_its_identity()
    {
        var data = new LifeDataService(path);
        var dueAt = DateTime.Now.AddMinutes(-1);
        var reminder = data.SaveReminder("喝水", "测试", dueAt);
        var due = Assert.Single(data.ClaimDueReminders(DateTime.Now));
        var postponedAt = DateTime.Now.AddMinutes(10);

        var snoozed = data.SnoozeReminder(due, postponedAt);

        Assert.Equal("reminder", snoozed.Kind);
        Assert.Equal(reminder.Id, snoozed.Id);
        Assert.Equal(postponedAt, snoozed.RemindAt);
        var scheduled = Assert.Single(data.ReminderItems());
        Assert.Equal(reminder.Id, scheduled.Id);
        Assert.Equal(postponedAt, scheduled.RemindAt);
    }

    [Fact]
    public void Snooze_recurring_occurrence_creates_a_one_off_follow_up_without_changing_rule()
    {
        var data = new LifeDataService(path);
        var recurring = data.SaveRecurringReminder("运动", null, TimeOnly.FromDateTime(DateTime.Now), RecurrenceKind.Daily, []);
        var occurrence = data.OccurrenceOn(recurring, DateTime.Today)!;
        var postponedAt = DateTime.Now.AddHours(1);

        var snoozed = data.SnoozeReminder(occurrence, postponedAt);

        Assert.Equal("reminder", snoozed.Kind);
        Assert.NotEqual(recurring.Id, snoozed.Id);
        Assert.Equal(new TimeOnly(DateTime.Now.Hour, DateTime.Now.Minute), Assert.Single(data.RecurringReminders()).ReminderTime);
        Assert.Contains(data.ReminderItems(), item => item.Id == snoozed.Id && item.RemindAt == postponedAt);
    }

    [Fact]
    public void Reschedule_changes_the_task_schedule_while_snooze_remains_delivery_only()
    {
        var data = new LifeDataService(path);
        var oldTime = DateTime.Now.AddHours(2);
        var todo = data.Save("提交报告", null, oldTime, oldTime);
        var item = Assert.Single(data.AgendaFor(oldTime.Date), value => value.Id == todo.Id);
        var newTime = DateTime.Now.AddHours(5);

        var changed = data.RescheduleAgenda(item, newTime);

        Assert.Equal(newTime, changed.StartsAt);
        var stored = Assert.Single(data.Todos(), value => value.Id == todo.Id);
        Assert.Equal(newTime, stored.DueAt);
        Assert.Equal(newTime, stored.RemindAt);
    }

    [Fact]
    public void RecurringOccurrenceOverrides_ReplaceOnlyTheCurrentOccurrenceAndKeepTheSeriesRule()
    {
        var now = new DateTime(2026, 7, 21, 8, 0, 0);
        var data = new LifeDataService(path, () => now);
        var recurring = data.SaveRecurringReminder("每日报告", null, new TimeOnly(9, 0), RecurrenceKind.Daily, []);
        var occurrence = data.OccurrenceOn(recurring, now.Date)!;

        data.SkipRecurringOccurrence(occurrence);
        var moved = data.RescheduleRecurringOccurrence(occurrence, now.AddHours(3));

        Assert.Equal("reminder", moved.Kind);
        Assert.Equal(now.AddHours(3), moved.RemindAt);
        Assert.Equal(new TimeOnly(9, 0), Assert.Single(data.RecurringReminders()).ReminderTime);
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*),MAX(override_type) FROM occurrence_overrides";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal("Reschedule", reader.GetString(1));
    }
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
            if (File.Exists(candidate)) File.Delete(candidate);
    }
}

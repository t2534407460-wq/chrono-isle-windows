using ChronoIsle.App;
using ChronoIsle.App.Services.State;

namespace ChronoIsle.Tests;

public sealed class TodayCompletionProgressTests
{
    [Fact]
    public void Counts_only_todos_due_or_reminded_today_and_marks_completed()
    {
        var today = new DateTime(2026, 7, 17);
        var todos = new[]
        {
            Todo("done", true, today.AddHours(9), null),
            Todo("later", false, today.AddHours(18), null),
            Todo("reminder", false, null, today.AddHours(12)),
            Todo("tomorrow", true, today.AddDays(1), null),
            Todo("inbox", false, null, null)
        };

        var progress = TodayCompletionProgressCalculator.Calculate(todos, today);

        Assert.Equal(1, progress.Completed);
        Assert.Equal(3, progress.Total);
    }

    static TodoItem Todo(string id, bool completed, DateTime? due, DateTime? remind) =>
        new(id, id, null, completed, due, remind, null, new DateTime(2026, 7, 1), new DateTime(2026, 7, 1));
}

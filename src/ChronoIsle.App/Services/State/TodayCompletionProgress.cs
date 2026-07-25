namespace ChronoIsle.App.Services.State;

public sealed record TodayCompletionProgress(int Completed, int Total);

public static class TodayCompletionProgressCalculator
{
    public static TodayCompletionProgress Calculate(IEnumerable<TodoItem> todos, DateTime localDate)
    {
        ArgumentNullException.ThrowIfNull(todos);
        var day = localDate.Date;
        var scheduledToday = todos.Where(todo => todo.DueAt?.Date == day || todo.RemindAt?.Date == day).ToArray();
        return new(scheduledToday.Count(todo => todo.IsCompleted), scheduledToday.Length);
    }
}

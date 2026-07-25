namespace ChronoIsle.App.Services.Commanding;

/// <summary>Produces local, deterministic options for the one supported vague “this week” flow.</summary>
public static class AmbiguousTimeSuggestionPlanner
{
    public static bool CanOffer(string input, AssistantCommandEnvelope envelope) =>
        (input.Contains("这周", StringComparison.Ordinal) || input.Contains("本周", StringComparison.Ordinal)) &&
        input.Contains("找个时间", StringComparison.Ordinal) &&
        envelope.Arguments switch
        {
            CreateReminderArgumentsV1 { Title: { Length: > 0 } } => true,
            CreateTodoArgumentsV1 { Title: { Length: > 0 } } => true,
            _ => false
        };

    public static IReadOnlyList<DateTime> SuggestedTimes(DateTime now)
    {
        var preferences = new[]
        {
            (Day: DayOfWeek.Wednesday, Hour: 20),
            (Day: DayOfWeek.Saturday, Hour: 10),
            (Day: DayOfWeek.Sunday, Hour: 15)
        };
        return preferences.Select(preference =>
        {
            var days = ((int)preference.Day - (int)now.DayOfWeek + 7) % 7;
            var candidate = now.Date.AddDays(days).AddHours(preference.Hour);
            return candidate <= now ? candidate.AddDays(7) : candidate;
        }).ToArray();
    }
}

namespace ChronoIsle.App.Services.State;

public sealed record CalendarConflict(AgendaItem First, AgendaItem Second);

public static class CalendarConflictDetector
{
    public static IReadOnlyList<CalendarConflict> Find(IEnumerable<AgendaItem> agenda)
    {
        ArgumentNullException.ThrowIfNull(agenda);
        var events = agenda.Where(item => item.Kind == "event" && item.EndsAt is not null)
            .OrderBy(item => item.StartsAt).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var conflicts = new List<CalendarConflict>();
        for (var first = 0; first < events.Length; first++)
            for (var second = first + 1; second < events.Length && events[second].StartsAt < events[first].EndsAt; second++)
                if (events[first].StartsAt < events[second].EndsAt)
                    conflicts.Add(new CalendarConflict(events[first], events[second]));
        return conflicts;
    }
}

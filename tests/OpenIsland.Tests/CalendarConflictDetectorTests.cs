using OpenIsland.App;
using OpenIsland.App.Services.State;

namespace OpenIsland.Tests;

public sealed class CalendarConflictDetectorTests
{
    [Fact]
    public void Finds_only_actual_overlapping_events_not_touching_or_todos()
    {
        var day = new DateTime(2026, 7, 17);
        var agenda = new[]
        {
            Event("a", day.AddHours(9), day.AddHours(10)),
            Event("b", day.AddHours(9.5), day.AddHours(11)),
            Event("touch", day.AddHours(11), day.AddHours(12)),
            new AgendaItem("todo", "todo", "待办", null, day.AddHours(9.5), null, null, false)
        };

        var conflicts = CalendarConflictDetector.Find(agenda);

        var conflict = Assert.Single(conflicts);
        Assert.Equal("a", conflict.First.Id);
        Assert.Equal("b", conflict.Second.Id);
    }

    static AgendaItem Event(string id, DateTime start, DateTime end) =>
        new(id, "event", id, null, start, end, null, false);
}

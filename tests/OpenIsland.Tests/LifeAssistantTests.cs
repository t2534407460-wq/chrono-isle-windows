using System.IO;
using Microsoft.Data.Sqlite;
using OpenIsland.App;
using OpenIsland.App.Services;

namespace OpenIsland.Tests;

public sealed class LifeAssistantTests
{
    [Fact]
    public void Parse_EventWithoutEndTime_AsksForTheMissingEndTime()
    {
        var analysis = AssistantIntentService.Parse("""{"intent":"create_event","title":"项目会议","startDateTime":"2026-07-14T10:00:00+08:00","endDateTime":null,"missingFields":["endDateTime"]}""");

        Assert.True(analysis.IsValid);
        Assert.True(analysis.NeedsClarification);
        Assert.Equal(AssistantIntentKind.CreateEvent, analysis.Intent!.Kind);
        Assert.Contains("endDateTime", analysis.Intent.MissingFields);
    }

    [Fact]
    public void ConfirmAction_WritesNothingUntilTheUserConfirms()
    {
        using var scope = new TempDatabase();
        var data = new LifeDataService(scope.Path);
        var session = data.NewSession();
        var raw = """{"intent":"create_todo","title":"提交日报","dueDateTime":"2026-07-14T09:00:00+08:00","reminderDateTime":"2026-07-14T09:00:00+08:00","reminderRequested":true}""";
        var action = data.SaveAction(session.Id, "明早提醒我提交日报", raw, "awaiting_confirmation");

        Assert.Empty(data.Todos());

        var executor = new AssistantActionService(data, new AssistantIntentService(new OpenAiChatService()), new ConversationRouter(), new LocalAgendaQueryService(data), new OpenAiChatService());
        var result = executor.Confirm(action.Id);

        Assert.True(result.Succeeded);
        var todo = Assert.Single(data.Todos());
        Assert.Equal("提交日报", todo.Title);
        Assert.Equal("confirmed", data.Action(action.Id)!.Status);
    }

    [Fact]
    public void Migration_PreservesLegacyTodosAndAddsReminderTracking()
    {
        using var scope = new TempDatabase();
        using (var db = new SqliteConnection($"Data Source={scope.Path}"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                CREATE TABLE todos(id TEXT PRIMARY KEY,title TEXT NOT NULL,notes TEXT,completed INTEGER NOT NULL,due_at TEXT,remind_at TEXT,created_at TEXT NOT NULL,updated_at TEXT NOT NULL);
                INSERT INTO todos VALUES('legacy','旧待办',NULL,0,NULL,NULL,'2026-07-13T00:00:00.0000000','2026-07-13T00:00:00.0000000');
                """;
            command.ExecuteNonQuery();
        }

        var data = new LifeDataService(scope.Path);

        var todo = Assert.Single(data.Todos());
        Assert.Equal("旧待办", todo.Title);
        data.Save("新待办", null, null, DateTime.Now.AddHours(1));
        Assert.Equal(2, data.Todos().Count);
    }

    [Fact]
    public void AgendaFor_SortsTodosAndEventsByTheirActualTime()
    {
        using var scope = new TempDatabase();
        var data = new LifeDataService(scope.Path);
        var day = new DateTime(2026, 7, 14);
        data.SaveEvent("上午会议", null, day.AddHours(10), day.AddHours(11), day.AddHours(10));
        data.Save("先处理的待办", null, day.AddHours(9), day.AddHours(9));

        var agenda = data.AgendaFor(day);

        Assert.Equal(["先处理的待办", "上午会议"], agenda.Select(x => x.Title));
    }

    [Fact]
    public void ClaimDueReminders_OnlyReturnsEachReminderOnce()
    {
        using var scope = new TempDatabase();
        var data = new LifeDataService(scope.Path);
        var yesterday = DateTime.Now.AddDays(-1);
        data.Save("交电费", null, yesterday, yesterday);

        Assert.Single(data.ClaimDueReminders(DateTime.Now));
        Assert.Empty(data.ClaimDueReminders(DateTime.Now.AddMinutes(1)));
    }

    [Fact]
    public void IndicatorState_UsesTheRequiredPriority()
    {
        using var scope = new TempDatabase();
        var data = new LifeDataService(scope.Path);
        var now = new DateTime(2030, 1, 2, 10, 0, 0);

        Assert.Equal(IslandIndicatorState.Idle, data.GetIslandIndicatorState(now));
        data.SaveRecurringReminder("daily", null, new TimeOnly(9, 0), RecurrenceKind.Daily, []);
        Assert.Equal(IslandIndicatorState.Idle, data.GetIslandIndicatorState(now));
        data.SaveReminder("future", null, now.AddHours(2));
        Assert.Equal(IslandIndicatorState.ReminderOnly, data.GetIslandIndicatorState(now));
        data.Save("unscheduled", null, null, null);
        Assert.Equal(IslandIndicatorState.PendingTodo, data.GetIslandIndicatorState(now));
        data.Save("soon", null, now.AddMinutes(30), null);
        Assert.Equal(IslandIndicatorState.DueSoonTodo, data.GetIslandIndicatorState(now));
        data.Save("late", null, now.AddMinutes(-1), null);
        Assert.Equal(IslandIndicatorState.OverdueTodo, data.GetIslandIndicatorState(now));
    }

    [Fact]
    public void CalendarIndicator_IgnoresRecurringRemindersAndUsesTemporaryItems()
    {
        using var scope = new TempDatabase();
        var data = new LifeDataService(scope.Path);
        var now = new DateTime(2030, 1, 2, 10, 0, 0);
        data.SaveRecurringReminder("daily", null, new TimeOnly(9, 0), RecurrenceKind.Daily, []);

        Assert.Equal(IslandIndicatorState.Idle, data.GetCalendarIndicatorState(now.Date, now));
        data.SaveReminder("temporary", null, now.AddHours(2));
        Assert.Equal(IslandIndicatorState.ReminderOnly, data.GetCalendarIndicatorState(now.Date, now));
        data.Save("todo", null, now.AddHours(2), null);
        Assert.Equal(IslandIndicatorState.PendingTodo, data.GetCalendarIndicatorState(now.Date, now));
    }
    [Fact]
    public void AgendaForRange_DeduplicatesSpanningEventsAndExpandsRecurringOccurrences()
    {
        using var scope = new TempDatabase();
        var data = new LifeDataService(scope.Path);
        var day = new DateTime(2030, 1, 7);
        data.SaveEvent("spanning", null, day.AddHours(-1), day.AddHours(1), null);
        data.Save("todo", null, day.AddHours(11), null);
        data.SaveRecurringReminder("daily", null, new TimeOnly(9, 0), RecurrenceKind.Daily, []);

        var items = data.AgendaForRange(day, day.AddDays(2));

        Assert.Single(items.Where(item => item.Title == "spanning"));
        Assert.Equal(2, items.Count(item => item.Title == "daily"));
        Assert.Contains(items, item => item.Title == "todo");
    }

    [Fact]
    public void ConversationRouter_OnlyUsesCreationRoutingForCreationCandidates()
    {
        var router = new ConversationRouter();
        var now = new DateTime(2030, 1, 7, 10, 0, 0);

        Assert.Equal(ConversationRouteKind.CreateAction, router.Decide("\u660e\u5929\u5341\u70b9\u5f00\u4f1a", null, now).Kind);
        Assert.Equal(ConversationRouteKind.LocalQuery, router.Decide("\u672c\u5468\u6709\u4ec0\u4e48\u63d0\u9192", null, now).Kind);
        Assert.Equal(ConversationRouteKind.LocalQuery, router.Decide("\u4eca\u5929\u6709\u5b89\u6392", null, now).Kind);
        Assert.Equal(ConversationRouteKind.GeneralChat, router.Decide("\u4eca\u5929\u661f\u671f\u51e0", null, now).Kind);
        Assert.Equal(ConversationRouteKind.GeneralChat, router.Decide("\u5982\u4f55\u6dfb\u52a0\u63d0\u9192", null, now).Kind);
    }
    sealed class TempDatabase : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"open-island-life-{Guid.NewGuid():N}.db");

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(Path)) File.Delete(Path);
        }
    }
}

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

        var executor = new AssistantActionService(data, new ChinaStatutoryHolidayCalendar(), new AssistantIntentService(new OpenAiChatService()), new ConversationRouter(), new LocalAgendaQueryService(data), new OpenAiChatService());
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
        data.Save("late", null, now.AddMinutes(-6), null);
        Assert.Equal(IslandIndicatorState.OverdueTodo, data.GetIslandIndicatorState(now));
    }

    [Fact]
    public void IndicatorState_AllowsFiveMinuteCompletionGraceBeforeMarkingOverdue()
    {
        using var scope = new TempDatabase();
        var data = new LifeDataService(scope.Path);
        var now = new DateTime(2030, 1, 2, 10, 0, 0);

        data.Save("仍在宽限期", null, now.AddMinutes(-4).AddSeconds(-59), null);
        Assert.Equal(IslandIndicatorState.PendingTodo, data.GetIslandIndicatorState(now));

        data.Save("已过宽限", null, now.AddMinutes(-5).AddSeconds(-1), null);
        Assert.Equal(IslandIndicatorState.OverdueTodo, data.GetIslandIndicatorState(now));
    }

    [Fact]
    public void AgendaItemIndicator_UsesEachItemsOwnStatus()
    {
        using var scope = new TempDatabase();
        var data = new LifeDataService(scope.Path);
        var now = new DateTime(2030, 1, 2, 10, 0, 0);
        var reminder = data.SaveReminder("reminder", null, now.AddHours(2));
        var pending = data.Save("pending", null, now.AddHours(2), null);
        var soon = data.Save("soon", null, now.AddMinutes(30), null);
        var overdue = data.Save("overdue", null, now.AddMinutes(-6), null);
        var agenda = data.AgendaFor(now.Date).ToDictionary(item => item.Id);

        Assert.Equal(IslandIndicatorState.ReminderOnly, data.GetAgendaItemIndicatorState(agenda[reminder.Id], now));
        Assert.Equal(IslandIndicatorState.PendingTodo, data.GetAgendaItemIndicatorState(agenda[pending.Id], now));
        Assert.Equal(IslandIndicatorState.DueSoonTodo, data.GetAgendaItemIndicatorState(agenda[soon.Id], now));
        Assert.Equal(IslandIndicatorState.OverdueTodo, data.GetAgendaItemIndicatorState(agenda[overdue.Id], now));
    }

    [Fact]
    public void IndicatorState_UsesTheConfiguredPerTodoGracePeriod()
    {
        using var scope = new TempDatabase();
        var data = new LifeDataService(scope.Path);
        var now = new DateTime(2030, 1, 2, 10, 0, 0);
        var todo = data.Save("可延长宽限", null, now.AddMinutes(-6), null);

        using (var db = new SqliteConnection($"Data Source={scope.Path}"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "UPDATE life_items SET overdue_grace_minutes=10 WHERE id=$id";
            command.Parameters.AddWithValue("$id", todo.Id);
            command.ExecuteNonQuery();
        }

        Assert.Equal(IslandIndicatorState.PendingTodo, data.GetIslandIndicatorState(now));
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
        Assert.Equal(ConversationRouteKind.CreateAction, router.Decide("\u5e2e\u6211\u62c6\u89e3\u6574\u7406\u623f\u95f4", null, now).Kind);
        Assert.Equal(ConversationRouteKind.LocalQuery, router.Decide("\u672c\u5468\u6709\u4ec0\u4e48\u63d0\u9192", null, now).Kind);
        Assert.Equal(ConversationRouteKind.LocalQuery, router.Decide("\u4eca\u5929\u6709\u5b89\u6392", null, now).Kind);
        Assert.Equal(ConversationRouteKind.GeneralChat, router.Decide("\u4eca\u5929\u661f\u671f\u51e0", null, now).Kind);
        Assert.Equal(ConversationRouteKind.GeneralChat, router.Decide("\u5982\u4f55\u6dfb\u52a0\u63d0\u9192", null, now).Kind);
    }
    [Fact]
    public async Task BulkExistingReminderMutation_SupersedesStaleClarificationWithoutChangingData()
    {
        using var scope = new TempDatabase();
        var data = new LifeDataService(scope.Path);
        data.SaveRecurringReminder("weekend", null, new TimeOnly(9, 0), RecurrenceKind.Weekly,
            [DayOfWeek.Saturday, DayOfWeek.Sunday]);
        var session = data.NewSession();
        var staleDraft = data.SaveAction(session.Id, "old draft", "{}", "clarifying");
        const string input = "\u5c06\u6240\u6709\u7684\u5468\u672b\u548c\u8282\u5047\u65e5\u63d0\u9192\u6536\u52301\u70b9\u949f";
        var router = new ConversationRouter();

        Assert.Equal(ConversationRouteKind.ModificationClarification,
            router.Decide(input, staleDraft, DateTime.Now).Kind);

        var service = new AssistantActionService(data, new ChinaStatutoryHolidayCalendar(), new AssistantIntentService(new OpenAiChatService()),
            router, new LocalAgendaQueryService(data), new OpenAiChatService());
        var result = await service.HandleAsync(ProviderSettings.Default, session, [], input);

        Assert.False(result.IsFailure);
        Assert.Null(result.PendingAction);
        Assert.Contains("01:00", result.Reply);
        Assert.Contains("\u5DF2\u63A5\u5165 2025\u20132026 \u5E74", result.Reply);
        Assert.Equal(new TimeOnly(9, 0), Assert.Single(data.RecurringReminders()).ReminderTime);
        Assert.Null(data.ActiveClarification(session.Id));
        Assert.Equal("superseded", data.Action(staleDraft.Id)!.Status);
    }

    [Fact]
    public async Task HolidayReminderBatch_RequiresAnExplicitTimeThenReschedulesOnlyConfirmedOneOffTargets()
    {
        using var scope = new TempDatabase();
        var data = new LifeDataService(scope.Path);
        var holidayAt = new DateTime(2026, 2, 16, 9, 0, 0);
        var holiday = data.SaveReminder("春节提醒", null, holidayAt);
        var ordinary = data.SaveReminder("普通提醒", null, holidayAt.AddDays(20));
        data.SaveRecurringReminder("每日提醒", null, new TimeOnly(9, 0), RecurrenceKind.Daily, []);
        var session = data.NewSession();
        var service = new AssistantActionService(
            data,
            new ChinaStatutoryHolidayCalendar(),
            new AssistantIntentService(new OpenAiChatService()),
            new ConversationRouter(),
            new LocalAgendaQueryService(data),
            new OpenAiChatService());

        var start = await service.HandleAsync(
            ProviderSettings.Default,
            session,
            [],
            "将所有法定节假日提醒改到1点钟");

        Assert.Null(start.PendingAction);
        Assert.NotNull(data.ActiveHolidayReminderBatch(session.Id));
        Assert.Contains("01:00", start.Reply);

        var confirmation = await service.HandleAsync(ProviderSettings.Default, session, [], "01:00");

        var pending = Assert.IsType<AssistantAction>(confirmation.PendingAction);
        Assert.Equal("awaiting_confirmation", pending.Status);
        var executed = service.Confirm(pending.Id);

        Assert.True(executed.Succeeded);
        var reminders = data.ReminderItems().ToDictionary(item => item.Id);
        Assert.Equal(new TimeOnly(1, 0), TimeOnly.FromDateTime(reminders[holiday.Id].RemindAt!.Value));
        Assert.Equal(holidayAt.Date, reminders[holiday.Id].RemindAt!.Value.Date);
        Assert.Equal(new TimeOnly(9, 0), TimeOnly.FromDateTime(reminders[ordinary.Id].RemindAt!.Value));
        Assert.Equal(new TimeOnly(9, 0), Assert.Single(data.RecurringReminders()).ReminderTime);
        Assert.Equal("confirmed", data.Action(pending.Id)!.Status);
    }

    [Fact]
    public async Task HolidayReminderBatch_RecoversTheOriginalRequestAfterAnOldClockOnlyReply()
    {
        using var scope = new TempDatabase();
        var data = new LifeDataService(scope.Path);
        var holidayAt = new DateTime(2026, 2, 16, 9, 0, 0);
        var holiday = data.SaveReminder("春节提醒", null, holidayAt);
        var session = data.NewSession();
        var service = new AssistantActionService(
            data,
            new ChinaStatutoryHolidayCalendar(),
            new AssistantIntentService(new OpenAiChatService()),
            new ConversationRouter(),
            new LocalAgendaQueryService(data),
            new OpenAiChatService());
        var history = new List<ChatMessage>
        {
            new("first", session.Id, "user", "将所有法定节假日提醒改到1点钟", DateTime.Now.AddMinutes(-2)),
            new("second", session.Id, "assistant", "旧版本要求明确时间", DateTime.Now.AddMinutes(-1)),
            new("third", session.Id, "user", "01:00", DateTime.Now.AddSeconds(-30)),
            new("fourth", session.Id, "assistant", "旧版本未能执行", DateTime.Now.AddSeconds(-20))
        };

        var confirmation = await service.HandleAsync(ProviderSettings.Default, session, history, "01:00");

        var pending = Assert.IsType<AssistantAction>(confirmation.PendingAction);
        Assert.True(service.Confirm(pending.Id).Succeeded);
        var updated = Assert.Single(data.ReminderItems().Where(item => item.Id == holiday.Id));
        Assert.Equal(new TimeOnly(1, 0), TimeOnly.FromDateTime(updated.RemindAt!.Value));
    }

    [Fact]
    public async Task OfficialSleepSchedule_CreatesSeparateWorkdayAndHolidayRemindersAfterConfirmation()
    {
        using var scope = new TempDatabase();
        var data = new LifeDataService(scope.Path);
        var session = data.NewSession();
        var service = new AssistantActionService(
            data,
            new ChinaStatutoryHolidayCalendar(),
            new AssistantIntentService(new OpenAiChatService()),
            new ConversationRouter(),
            new LocalAgendaQueryService(data),
            new OpenAiChatService());
        var response = await service.HandleAsync(
            ProviderSettings.Default,
            session,
            [],
            "所有工作日设置凌晨12点的睡觉提醒，节假日设置凌晨1点的睡觉提醒");
        var pending = Assert.IsType<AssistantAction>(response.PendingAction);
        Assert.Equal("awaiting_confirmation", pending.Status);
        Assert.Empty(data.RecurringReminders());
        Assert.True(service.Confirm(pending.Id).Succeeded);
        var reminders = data.RecurringReminders();
        var workdays = Assert.Single(reminders.Where(item => item.Recurrence == RecurrenceKind.OfficialWorkdays));
        var holidays = Assert.Single(reminders.Where(item => item.Recurrence == RecurrenceKind.StatutoryHolidays));
        Assert.Equal(new TimeOnly(0, 0), workdays.ReminderTime);
        Assert.Equal(new TimeOnly(1, 0), holidays.ReminderTime);
        Assert.NotNull(data.OccurrenceOn(workdays, new DateTime(2026, 2, 14)));
        Assert.Null(data.OccurrenceOn(holidays, new DateTime(2026, 2, 14))); // 调休周六上班
        Assert.NotNull(data.OccurrenceOn(holidays, new DateTime(2026, 7, 18))); // 普通周六也使用节假日时间
        Assert.Null(data.OccurrenceOn(workdays, new DateTime(2026, 7, 18)));

        Assert.Null(data.OccurrenceOn(workdays, new DateTime(2026, 2, 16)));
        Assert.NotNull(data.OccurrenceOn(holidays, new DateTime(2026, 2, 16)));
        Assert.Null(data.OccurrenceOn(holidays, new DateTime(2026, 2, 14)));
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

using ChronoIsle.App;
using ChronoIsle.App.Services;
using ChronoIsle.App.Services.Commanding;
using ChronoIsle.App.Services.Persistence;
using ChronoIsle.App.Services.Scheduling;

namespace ChronoIsle.Tests;

public sealed partial class AssistantDraftRuntimeTests
{
    [ModelEvaluationFact]
    public async Task Configured_provider_handles_item_management_in_an_isolated_database()
    {
        var service = Service(new AssistantDraftInterpreter(new OpenAiChatService()));
        var provider = new ProviderSettingsService().Load();
        async Task<AssistantConversationResult> Ask(string text) => await service.HandleAsync(provider, session, [], text);
        var added = await Ask("新增一个每天23:00提醒睡觉的计划");
        if (added.Interaction?.CanConfirm == true) added = await Click(service, added.Interaction, "confirm");
        Assert.True(added.RefreshReminders, System.Text.Json.JsonSerializer.Serialize(added));
        var original = Assert.Single(data.RecurringReminders());
        var changed = await Ask("把睡觉计划改为法定工作日凌晨2点提醒");
        Assert.Equal("确认修改", changed.Interaction?.Title);
        var saved = await Click(service, changed.Interaction!, "confirm");
        Assert.True(saved.RefreshReminders, System.Text.Json.JsonSerializer.Serialize(saved));
        var reminder = Assert.Single(data.RecurringReminders());
        Assert.Equal(original.Id, reminder.Id);
        Assert.Equal("official", reminder.Schedule!.DayPattern);
        Assert.Equal(new TimeOnly(2, 0), reminder.ReminderTime);
        var found = await Ask("查看睡觉计划");
        Assert.Null(found.Interaction);
        Assert.Contains("02:00", found.Reply);
        Assert.False(found.RefreshReminders);
        var deleted = await Ask("删除睡觉计划");
        Assert.Equal("确认删除", deleted.Interaction?.Title);
        var done = await Click(service, deleted.Interaction!, "confirm");
        Assert.True(done.RefreshReminders, done.Reply);
        Assert.Empty(data.RecurringReminders());
    }

    [ModelEvaluationFact]
    public async Task Configured_provider_recognizes_the_reported_modification_request()
    {
        var original = data.SaveRecurringReminder("睡觉", null, new(23, 0), RecurrenceKind.Daily, []);
        var service = Service(new AssistantDraftInterpreter(new OpenAiChatService()));
        var unrelated = await Send(service, "提醒我生产报工");
        Assert.Equal("NeedsInput", unrelated.Interaction?.State);
        var result = await service.HandleAsync(new ProviderSettingsService().Load(), session, [],
            "将每天的睡觉任务中的定时提醒，工作日改为凌晨2点提醒");
        var turn = new AssistantDraftStore(path).Active(session.Id)!;
        Assert.Contains(Assert.Single(turn.Tasks).Operation, new[] { "update_todo", "reschedule_item" });
        Assert.False(result.IsFailure, result.Reply);
        Assert.NotEqual("确认新增", result.Interaction?.Title);
        Assert.Equal(original.Id, Assert.Single(data.RecurringReminders()).Id);
        Assert.Equal(new TimeOnly(23, 0), Assert.Single(data.RecurringReminders()).ReminderTime);
    }

    [Fact]
    public async Task Existing_sleep_schedule_request_does_not_enter_the_legacy_creation_shortcut()
    {
        data.SaveRecurringReminder("睡觉", null, new(23, 0), RecurrenceKind.Daily, []);
        const string input = "查看睡觉计划，工作日凌晨2点，节假日凌晨3点";
        var interpreter = new FixedInterpreter(new(3, "query", [new("list_items", input, Target: "睡觉")]));
        var result = await Send(Service(interpreter), input);
        Assert.Equal(1, interpreter.Calls);
        Assert.Null(result.PendingAction);
        Assert.Null(result.Interaction);
        Assert.Single(data.RecurringReminders());
    }

    [Fact]
    public async Task Existing_daily_plan_is_updated_in_place_instead_of_created_again()
    {
        var original = data.SaveRecurringReminder("睡觉", "保留备注", new(23, 0), RecurrenceKind.Daily, []);
        const string input = "将睡觉计划改为法定工作日凌晨2点提醒";
        var service = Service(new FixedInterpreter(new(3, "tasks",
            [new("update_todo", input, Target: "睡觉", ItemKind: "reminder",
                Schedule: new(DaysText: "法定工作日", TimesText: "凌晨2点"))])));
        var pending = await Send(service, input);
        Assert.Equal("NeedsConfirmation", pending.Interaction?.State);
        Assert.Contains("修改", pending.Interaction!.Title);
        Assert.DoesNotContain("创建", pending.Interaction.Explanation);
        Assert.Equal(new TimeOnly(23, 0), Assert.Single(data.RecurringReminders()).ReminderTime);
        var done = await Click(service, pending.Interaction, "confirm");
        Assert.True(done.RefreshReminders, done.Reply);
        var saved = Assert.Single(data.RecurringReminders());
        Assert.Equal(original.Id, saved.Id);
        Assert.Equal("保留备注", saved.Notes);
        Assert.Equal("official", saved.Schedule!.DayPattern);
        Assert.Equal(new[] { new TimeOnly(2, 0) }, saved.Schedule.Times);
        await Click(service, pending.Interaction, "confirm");
        Assert.Single(data.RecurringReminders());
    }

    [Fact]
    public async Task Interval_change_preserves_window_exclusions_and_days()
    {
        var (creator, _) = WorkService();
        var first = await Send(creator, WorkRequest);
        var prepared = await Click(creator, first.Interaction!, "submit", WorkAnswers("restart"));
        await Click(creator, prepared.Interaction!, "confirm");
        var before = Assert.Single(data.RecurringReminders());
        const string input = "将站起来活动的提醒间隔改为1小时";
        var updater = Service(new FixedInterpreter(new(3, "tasks",
            [new("update_todo", input, Target: "站起来活动", Schedule: new(IntervalText: "1小时"))])));
        var pending = await Send(updater, input);
        Assert.Equal("NeedsConfirmation", pending.Interaction?.State);
        var done = await Click(updater, pending.Interaction!, "confirm");
        Assert.True(done.RefreshReminders, done.Reply);
        var after = Assert.Single(data.RecurringReminders());
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(60, after.Schedule!.IntervalMinutes);
        Assert.Equal(before.Schedule!.Window, after.Schedule.Window);
        Assert.Equal(before.Schedule.Exclusions, after.Schedule.Exclusions);
        Assert.Equal(before.Schedule.DayPattern, after.Schedule.DayPattern);
        Assert.Equal(before.Schedule.Rhythm, after.Schedule.Rhythm);
    }

    [Fact]
    public async Task Delete_with_recurrence_context_never_compiles_a_create_or_asks_for_time()
    {
        var item = data.SaveRecurringReminder("睡觉", null, new(23, 0), RecurrenceKind.Daily, []);
        const string input = "删除每天的睡觉提醒计划";
        var service = Service(new FixedInterpreter(new(3, "tasks",
            [new("delete_todo", input, Target: "睡觉", RepeatText: "每天", Schedule: new(DaysText: "每天"))])));
        var pending = await Send(service, input);
        Assert.Equal("NeedsConfirmation", pending.Interaction?.State);
        Assert.Contains("删除", pending.Interaction!.Title);
        Assert.Single(data.RecurringReminders());
        var done = await Click(service, pending.Interaction, "confirm");
        Assert.True(done.RefreshReminders, done.Reply);
        Assert.Empty(data.RecurringReminders());
        Assert.Empty(data.RecurringAgendaFor(DateTime.Today.AddDays(1)).Where(i => i.Id == item.Id));
    }

    [Fact]
    public async Task Query_by_name_returns_the_saved_plan_without_clarification_or_writes()
    {
        var item = data.SaveRecurringReminder("睡觉", "保持作息", new(23, 0), RecurrenceKind.Daily, []);
        data.Save("买牛奶", null, null, null);
        const string input = "查看睡觉计划";
        var service = Service(new FixedInterpreter(new(3, "query", [new("list_items", input, Target: "睡觉")])));
        var result = await Send(service, input);
        Assert.Null(result.Interaction);
        Assert.False(result.RefreshReminders);
        Assert.Contains("睡觉", result.Reply);
        Assert.Contains("23:00", result.Reply);
        Assert.DoesNotContain("买牛奶", result.Reply);
        var after = Assert.Single(data.RecurringReminders());
        Assert.Equal(item.Id, after.Id);
        Assert.Equal(item.UpdatedAt, after.UpdatedAt);
        Assert.Equal(item.ReminderTime, after.ReminderTime);
    }

    [Fact]
    public async Task Same_name_plan_selection_updates_only_the_selected_series_and_recovers_after_restart()
    {
        var first = data.SaveRecurringReminder("睡觉", "第一份", new(23, 0), RecurrenceKind.Daily, []);
        var second = data.SaveRecurringReminder("睡觉", "第二份", new(22, 0), RecurrenceKind.Weekdays, []);
        const string input = "把睡觉提醒改到凌晨两点";
        var service = Service(new FixedInterpreter(new(3, "tasks", [new("reschedule_item", input, Target: "睡觉", TimeText: "凌晨两点")])));
        var choice = await Send(service, input);
        var field = Assert.Single(choice.Interaction!.Fields);
        Assert.Equal("0.candidate", field.Key);
        var option = Assert.Single(field.Options.Where(o => o.Label.Contains("22:00")));
        var pending = await Click(service, choice.Interaction, "submit", new() { [field.Key] = option.Value });
        Assert.True(pending.Interaction!.CanConfirm);
        var restarted = Service();
        var restored = restarted.GetInteraction(session.Id)!;
        var result = await Click(restarted, restored, "confirm");
        Assert.True(result.RefreshReminders, result.Reply);
        Assert.Equal(2, data.RecurringReminders().Count);
        Assert.Equal(new TimeOnly(23, 0), data.RecurringReminders().Single(r => r.Id == first.Id).ReminderTime);
        var changed = data.RecurringReminders().Single(r => r.Id == second.Id);
        Assert.Equal("第二份", changed.Notes);
        Assert.Equal("weekdays", changed.Schedule!.DayPattern);
        Assert.Equal(new TimeOnly(2, 0), changed.ReminderTime);
    }

    [Fact]
    public async Task Schedule_update_rejects_stale_target_and_cancel_does_not_write()
    {
        var item = data.SaveRecurringReminder("喝水", "旧备注", new(9, 0), RecurrenceKind.Daily, []);
        const string input = "把喝水改为每天10:00和15:00提醒";
        var service = Service(new FixedInterpreter(new(3, "tasks", [new("update_todo", input, Target: "喝水",
            Schedule: new(DaysText: "每天", TimesText: "10:00和15:00"))])));
        var first = await Send(service, input);
        await Click(service, first.Interaction!, "cancel");
        Assert.Null(Assert.Single(data.RecurringReminders()).Schedule);
        var pending = await Send(service, input);
        data.SaveRecurringReminder(item.Title, "手动改过", item.ReminderTime, item.Recurrence, [], item.Id);
        var stale = await Click(service, pending.Interaction!, "confirm");
        Assert.False(stale.RefreshReminders);
        var current = Assert.Single(data.RecurringReminders());
        Assert.Equal("手动改过", current.Notes);
        Assert.Equal(new TimeOnly(9, 0), current.ReminderTime);
        Assert.Null(current.Schedule);
    }

    [Fact]
    public async Task Correcting_pending_update_keeps_operation_and_replaces_old_time()
    {
        data.SaveRecurringReminder("睡觉", null, new(23, 0), RecurrenceKind.Daily, []);
        var first = Service(new FixedInterpreter(new(3, "tasks", [new("update_todo", "睡觉改为凌晨2点", Target: "睡觉", TimeText: "凌晨2点")])));
        var pending = await Send(first, "睡觉改为凌晨2点");
        var editor = Service(new FixedInterpreter(new(3, "tasks", [new("update_todo", "改为凌晨3点", Target: "睡觉", TimeText: "凌晨3点")])));
        var edited = await Send(editor, "改为凌晨3点");
        Assert.Equal("确认修改", edited.Interaction!.Title);
        Assert.Contains("03:00", edited.Interaction.Summary);
        Assert.DoesNotContain("02:00", edited.Interaction.Summary);
        await Click(editor, pending.Interaction!, "confirm");
        Assert.Equal(new TimeOnly(23, 0), Assert.Single(data.RecurringReminders()).ReminderTime);
        var done = await Click(editor, edited.Interaction, "confirm");
        Assert.True(done.RefreshReminders, done.Reply);
        Assert.Equal(new TimeOnly(3, 0), Assert.Single(data.RecurringReminders()).ReminderTime);
    }

    [Fact]
    public async Task Missing_target_never_falls_back_to_creating_a_plan()
    {
        var service = Service(new FixedInterpreter(new(3, "tasks", [new("update_todo", "修改不存在的计划", Target: "不存在",
            Schedule: new(TimesText: "02:00"))])));
        var pending = await Send(service, "修改不存在的计划");
        Assert.Equal("Blocked", pending.Interaction!.State);
        Assert.Empty(pending.Interaction.Fields);
        Assert.Contains("没有可供", pending.Reply);
        Assert.Empty(data.RecurringReminders());
        Assert.False(pending.Interaction.CanConfirm);
    }

    [Theory]
    [InlineData("60", null)]
    [InlineData(null, "09:00-18:00")]
    public async Task Conflicting_schedule_modes_are_not_silently_dropped(string? interval, string? window)
    {
        data.SaveRecurringReminder("喝水", null, new(9, 0), RecurrenceKind.Daily, []);
        var service = Service(new FixedInterpreter(new(3, "tasks", [new("update_todo", "修改喝水计划", Target: "喝水",
            Schedule: new(IntervalText: interval, WindowText: window, TimesText: "10:00"))])));
        var result = await Send(service, "修改喝水计划");
        Assert.Equal("NeedsInput", result.Interaction?.State);
        Assert.Contains("固定时刻", result.Interaction!.Explanation);
        Assert.Contains(result.Interaction.Fields, f => f.Key == "correction.scope");
        Assert.Equal(new TimeOnly(9, 0), Assert.Single(data.RecurringReminders()).ReminderTime);
    }

    [Fact]
    public async Task Query_all_includes_undated_todos_and_date_query_filters_by_name()
    {
        data.Save("买牛奶", null, null, null);
        data.SaveReminder("喝水", null, DateTime.Today.AddDays(1).AddHours(9));
        data.SaveReminder("睡觉", null, DateTime.Today.AddDays(1).AddHours(23));
        var all = await Send(Service(new FixedInterpreter(new(3, "query", [new("list_items", "查看所有事项", TimeText: "所有")]))), "查看所有事项");
        Assert.Null(all.Interaction);
        Assert.Contains("买牛奶", all.Reply);
        var named = await Send(Service(new FixedInterpreter(new(3, "query", [new("list_items", "查看明天睡觉提醒", Target: "睡觉", TimeText: "明天")]))), "查看明天睡觉提醒");
        Assert.Null(named.Interaction);
        Assert.Contains("睡觉", named.Reply);
        Assert.DoesNotContain("喝水", named.Reply);
        Assert.False(named.RefreshReminders);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Schedule_update_suppresses_old_queued_time_and_delivers_new_time(bool queuedInOutbox)
    {
        var original = data.SaveRecurringReminder("喝水", null, new(9, 0), RecurrenceKind.Daily, []);
        var day = DateTime.Today.AddDays(1);
        var occurrence = data.RecurringAgendaFor(day).Single();
        var now = day.AddHours(8);
        using var scheduler = new ReminderService(data, new LifePreferencesService(), new WindowsNotificationService(), localNow: () => now);
        scheduler.Schedule(occurrence);
        if (queuedInOutbox)
        {
            var store = new ReminderDeliveryStore(LifeDataStoreRuntimeRegistry.GetOrCreate(path).WriteQueue);
            var detector = new ReminderDueDetector(store);
            var lease = Assert.IsType<ReminderOccurrenceLease>(detector.TryClaimDue(new DateTimeOffset(occurrence.StartsAt), "before-update"));
            Assert.True(store.EnqueueNotification(new("before-update", original.Id, lease.OccurrenceKey, "ToastAndIsland", new DateTimeOffset(occurrence.StartsAt))));
            Assert.True(detector.MarkDelivered(lease, new DateTimeOffset(occurrence.StartsAt)));
        }
        const string input = "把喝水提醒改为每天上午十点";
        var service = Service(new FixedInterpreter(new(3, "tasks", [new("update_todo", input, Target: "喝水", TimeText: "上午十点")])));
        var pending = await Send(service, input);
        var updated = await Click(service, pending.Interaction!, "confirm");
        Assert.True(updated.RefreshReminders, updated.Reply);
        var received = new List<AgendaItem>();
        scheduler.ReminderDue += (_, item) => received.Add(item);
        now = day.AddHours(9);
        await scheduler.PollNowAsync();
        Assert.Empty(received);
        now = day.AddHours(10);
        scheduler.RefreshSchedule();
        await scheduler.PollNowAsync();
        Assert.Equal(original.Id, Assert.Single(received).Id);
        Assert.Equal(now, received[0].StartsAt);
    }
}

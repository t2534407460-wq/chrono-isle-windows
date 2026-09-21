using ChronoIsle.App;
using ChronoIsle.App.Services;
using ChronoIsle.App.Services.Commanding;
using ChronoIsle.App.Services.Domain;
using ChronoIsle.App.Services.Persistence;
using ChronoIsle.App.Services.Scheduling;

namespace ChronoIsle.Tests;

public sealed partial class AssistantDraftRuntimeTests
{
    const string WorkRequest = "每天工作时间间隔2小时提醒我站起来活动，除午休时间外";

    (AssistantActionService service, FixedInterpreter interpreter) WorkService()
    {
        // Reproduce the previously lossy model output: no schedule and incomplete time.
        var interpreter = new FixedInterpreter(new(3, "tasks",
            [new("create_reminder", WorkRequest, "站起来活动", TimeText: "每天工作时间间隔2小时", RepeatText: "每天")]));
        return (Service(interpreter), interpreter);
    }

    static Dictionary<string,string> WorkAnswers(string rhythm = "skip") => new()
    {
        ["0.schedule.days"] = "weekdays", ["0.schedule.window"] = "09:00-18:00",
        ["0.schedule.exclusion"] = "12:00-13:00", ["0.schedule.first"] = "after_interval",
        ["0.schedule.rhythm"] = rhythm
    };

    [Theory]
    [InlineData(null, "official")]
    [InlineData("1,2,3,4,5", "official")]
    [InlineData("1,3,5", "daily")]
    [InlineData("1,3,5", "weekdays")]
    [InlineData("1,3,5", "custom")]
    [InlineData("1,2,3,4,5", "official", "Failed")]
    public async Task Continue_planning_accepts_evening_window_ending_at_midnight(string? previousWeekdays, string days, string state = "NeedsInput")
    {
        var turn = new AssistantDraftTurn(Guid.NewGuid().ToString("N"), session.Id, 1, state,
            "下班后到晚上12点，每两小时提醒我活动", DateTimeOffset.Now, "Asia/Shanghai", DateTimeOffset.UtcNow.AddMinutes(15),
            [new("create_reminder", "下班后到晚上12点，每两小时提醒我活动", "活动",
                RepeatText: previousWeekdays == "1,2,3,4,5" ? "工作日" : null,
                Schedule: new("120", "18:00-00:00", "none", days, FirstTrigger: "after_interval", WeekdaysText: previousWeekdays))],
            [new("0.schedule.window", "每天在哪个时间段内提醒？", "time_range", [], "18:00-00:00")],
            new Dictionary<int,AssistantPlanCandidateBindingV2>(), new Dictionary<string,AssistantPlanCandidateBindingV2>(),
            Reply: "任务没有完成，草稿已保留，请修改信息后再试。", InteractionVersion: 2);
        new AssistantDraftStore(path).Save(turn);
        // A retry may understand the original sentence again; retain all structured answers.
        var service = state == "Failed"
            ? Service(new FixedInterpreter(new(3, "tasks", [new("create_reminder", turn.SourceText, "活动", RepeatText: "工作日")])))
            : Service();
        var card = service.GetInteraction(session.Id)!;
        if (state == "Failed") Assert.Equal(turn.Reply, card.Explanation);
        var result = await Click(service, card, state == "Failed" ? "retry" : "submit",
            new() { ["0.schedule.window"] = "18:00-00:00" });
        Assert.Equal("NeedsConfirmation", result.Interaction?.State);
        Assert.Contains("20:00", result.Interaction!.Summary);
        Assert.Contains("22:00", result.Interaction.Summary);
        Assert.Empty(data.RecurringReminders());
        await Click(service, result.Interaction, "confirm");
        var reminder = Assert.Single(data.RecurringReminders());
        Assert.Equal(new[] { new TimeOnly(20,0), new TimeOnly(22,0) }, reminder.Schedule!.Times);
        Assert.Equal(days, reminder.Schedule.DayPattern);
        Assert.Equal(days == "weekdays" ? new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday }
            : days == "custom" ? new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday } : [], reminder.Schedule.Weekdays);
        var nextDay = reminder.Schedule.Next(DateTime.Now)[0].Date;
        Assert.Single(data.ClaimDueReminders(nextDay.AddHours(20)));
        Assert.Single(data.ClaimDueReminders(nextDay.AddHours(22)));
        Assert.Empty(data.ClaimDueReminders(nextDay.AddDays(1)));
    }

    [Theory]
    [InlineData("18:00-02:00", "跨午夜")]
    [InlineData("00:00-00:00", "不能相同")]
    [InlineData("6-12", "24 小时制")]
    public async Task Invalid_window_returns_specific_error_and_preserves_input(string window, string expected)
    {
        var turn = new AssistantDraftTurn(Guid.NewGuid().ToString("N"), session.Id, 1, "NeedsInput",
            "下班后提醒我活动", DateTimeOffset.Now, "Asia/Shanghai", DateTimeOffset.UtcNow.AddMinutes(15),
            [new("create_reminder", "下班后提醒我活动", "活动",
                Schedule: new("120", window, "none", "official", FirstTrigger: "after_interval"))],
            [new("0.schedule.window", "每天在哪个时间段内提醒？", "time_range", [], window)],
            new Dictionary<int,AssistantPlanCandidateBindingV2>(), new Dictionary<string,AssistantPlanCandidateBindingV2>(),
            InteractionVersion: 2);
        new AssistantDraftStore(path).Save(turn);
        var service = Service();
        var result = await Click(service, service.GetInteraction(session.Id)!, "submit",
            new() { ["0.schedule.window"] = window });
        Assert.Equal("NeedsInput", result.Interaction?.State);
        var field = Assert.Single(result.Interaction!.Fields);
        Assert.Equal(window, field.Value);
        Assert.Contains(expected, field.Error);
        Assert.Contains(expected, result.Reply);
        Assert.Empty(data.RecurringReminders());
    }

    [Theory]
    [InlineData("skip", "11:00,13:00,15:00,17:00")]
    [InlineData("restart", "11:00,15:00,17:00")]
    public async Task Work_window_questions_compile_preview_and_deliver_one_series(string rhythm, string expected)
    {
        var (service, interpreter) = WorkService();
        var first = await Send(service, WorkRequest);
        var card = first.Interaction!;
        Assert.Equal("NeedsInput", card.State);
        Assert.Contains(card.Fields, f => f.Key == "0.schedule.window" && f.Kind == "time_range");
        Assert.Contains(card.Fields, f => f.Key == "0.schedule.weekdays" && f.DependsOn == "0.schedule.days");
        Assert.DoesNotContain(card.Fields, f => f.Kind == "time");
        Assert.Contains("120", card.Summary);
        Assert.Empty(data.RecurringReminders());
        var prepared = await Click(service, card, "submit", WorkAnswers(rhythm));
        Assert.False(prepared.IsFailure, prepared.Reply);
        Assert.Equal("NeedsConfirmation", prepared.Interaction?.State);
        Assert.NotEmpty(prepared.Interaction!.Preview!);
        Assert.Empty(data.RecurringReminders());
        var restarted = Service();
        Assert.Equal(prepared.Interaction.Preview, restarted.GetInteraction(session.Id)!.Preview);
        var complete = await Click(restarted, prepared.Interaction, "confirm");
        Assert.True(complete.RefreshReminders, complete.Reply);
        var reminder = Assert.Single(data.RecurringReminders());
        Assert.Equal(expected, string.Join(",", reminder.Schedule!.Times.Select(t => t.ToString("HH:mm"))));
        Assert.Equal(1, interpreter.Calls);
        Assert.Empty(data.ReminderItems().Where(r => r.Kind == "reminder"));
        await Click(restarted, prepared.Interaction, "confirm");
        Assert.Single(data.RecurringReminders());
        var day = DateTime.Today.AddDays(1);
        while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) day = day.AddDays(1);
        Assert.Equal(reminder.Schedule.Times.Count, data.RecurringAgendaFor(day).Count);
        foreach (var time in reminder.Schedule.Times)
        {
            var now = day.Add(time.ToTimeSpan());
            var due = Assert.Single(data.ClaimDueReminders(now));
            Assert.Equal(now, due.StartsAt);
            Assert.Equal(now, data.FindAgendaItem("recurring", reminder.Id, now)!.StartsAt);
            Assert.Empty(data.ClaimDueReminders(now.AddSeconds(1)));
        }
        Assert.Empty(data.ClaimDueReminders(day.AddHours(18)));
        var saturday = day;
        while (saturday.DayOfWeek != DayOfWeek.Saturday) saturday = saturday.AddDays(1);
        Assert.Empty(data.RecurringAgendaFor(saturday));
    }

    [Fact]
    public async Task Custom_days_are_required_only_when_selected_and_invalid_ranges_remain_editable()
    {
        var (service, _) = WorkService();
        var card = (await Send(service, WorkRequest)).Interaction!;
        var answers = WorkAnswers();
        answers["0.schedule.days"] = "custom";
        Assert.Equal(card.Revision, (await Click(service, card, "submit", answers)).Interaction!.Revision);
        answers["0.schedule.weekdays"] = "1,3,5";
        answers["0.schedule.exclusion"] = "19:00-20:00";
        var invalid = await Click(service, card, "submit", answers);
        Assert.Equal("NeedsInput", invalid.Interaction?.State);
        Assert.Contains("排除时段", invalid.Interaction!.Summary);
        Assert.Contains(invalid.Interaction.Fields, f => f.Key == "0.schedule.exclusion");
        Assert.Contains(invalid.Interaction.Fields, f => f.Key == "0.schedule.interval" && f.Kind == "duration");
        Assert.Empty(data.RecurringReminders());
    }

    [Fact]
    public async Task Editing_schedule_preserves_answers_and_invalidates_old_confirmation()
    {
        var (service, _) = WorkService();
        var first = await Send(service, WorkRequest);
        var preview = (await Click(service, first.Interaction!, "submit", WorkAnswers())).Interaction!;
        var edit = (await Click(service, preview, "edit")).Interaction!;
        var answers = edit.Fields.Where(f => f.DependsOn is null).ToDictionary(f => f.Key, f => f.Value ?? "");
        Assert.Equal("09:00-18:00", answers["0.schedule.window"]);
        Assert.Equal("120", answers["0.schedule.interval"]);
        answers["0.schedule.interval"] = "60";
        answers["0.schedule.until"] = DateTime.Today.AddDays(14).ToString("yyyy-MM-dd");
        var revised = await Click(service, edit, "submit", answers);
        Assert.Equal("NeedsConfirmation", revised.Interaction?.State);
        await Click(service, preview, "confirm");
        Assert.Empty(data.RecurringReminders());
        await Click(service, revised.Interaction!, "confirm");
        var item = Assert.Single(data.RecurringReminders());
        Assert.Equal(60, item.Schedule!.IntervalMinutes);
        Assert.Empty(data.RecurringAgendaFor(DateTime.Today.AddDays(15)));
        Assert.Throws<InvalidOperationException>(() => data.SaveRecurringReminder(item.Title, item.Notes,
            new TimeOnly(8, 0), item.Recurrence, item.Weekdays, item.Id));
        data.SaveRecurringReminder("活动一下", item.Notes, item.ReminderTime, item.Recurrence, item.Weekdays, item.Id);
        Assert.Equal(item.Schedule.Times, Assert.Single(data.RecurringReminders()).Schedule!.Times);
    }

    [Fact]
    public async Task Old_lossy_draft_is_upgraded_to_scenario_questions_without_execution()
    {
        var turn = new AssistantDraftTurn(Guid.NewGuid().ToString("N"), session.Id, 1, "NeedsInput",
            WorkRequest, DateTimeOffset.Now, "Asia/Shanghai", DateTimeOffset.UtcNow.AddMinutes(10),
            [new("create_reminder", WorkRequest, "站起来活动", RepeatText: "每天")],
            [new("0.timeText", "提醒时间", "time", [])], new Dictionary<int,AssistantPlanCandidateBindingV2>(),
            new Dictionary<string,AssistantPlanCandidateBindingV2>());
        new AssistantDraftStore(path).Save(turn);
        var card = Service().GetInteraction(session.Id)!;
        Assert.Contains(card.Fields, f => f.Kind == "time_range");
        Assert.DoesNotContain(card.Fields, f => f.Kind == "time");
        Assert.Empty(data.RecurringReminders());
    }

    [Fact]
    public async Task Event_duration_uses_duration_editor_and_preserves_start()
    {
        var interpreter = new FixedInterpreter(new(3, "tasks",
            [new("create_event", "明天下午三点开项目会", "项目会", TimeText: "明天下午三点")]));
        var service = Service(interpreter);
        var card = (await Send(service, "明天下午三点开项目会")).Interaction!;
        Assert.Equal("duration", Assert.Single(card.Fields).Kind);
        var result = await Click(service, card, "submit", new() { ["0.durationText"] = "90" });
        if (result.Interaction?.CanConfirm == true) result = await Click(service, result.Interaction, "confirm");
        Assert.True(result.RefreshReminders, result.Reply);
        var item = Assert.Single(data.AgendaFor(DateTime.Today.AddDays(1)).Where(i => i.Kind == "event"));
        Assert.Equal(TimeSpan.FromMinutes(90), item.EndsAt - item.StartsAt);
        Assert.Equal(1, interpreter.Calls);
    }

    [Fact]
    public async Task Query_custom_dates_have_conditional_controls_and_do_not_write()
    {
        var service = Service(new FixedInterpreter(new(3, "query", [new("list_items", "查看事项")])));
        var card = (await Send(service, "查看事项")).Interaction!;
        Assert.Contains(card.Fields, f => f.Kind == "date" && f.DependsValue == "自定义");
        var result = await Click(service, card, "submit", new()
        { ["0.timeText"] = "自定义", ["0.dueText"] = "2026-09-21", ["0.endText"] = "2026-09-25" });
        Assert.Null(result.Interaction);
        Assert.False(result.RefreshReminders);
    }

    [Fact]
    public async Task Scheduler_queue_suppresses_lunch_and_delivers_each_later_occurrence_once()
    {
        var (service, _) = WorkService();
        var card = (await Send(service, WorkRequest)).Interaction!;
        var preview = (await Click(service, card, "submit", WorkAnswers())).Interaction!;
        await Click(service, preview, "confirm");
        var day = DateTime.Today.AddDays(1);
        while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) day = day.AddDays(1);
        var now = day.AddHours(10);
        using var scheduler = new ReminderService(data, new LifePreferencesService(), new WindowsNotificationService(),
            localNow: () => now);
        var received = new List<DateTime>();
        scheduler.ReminderDue += (_, item) => received.Add(item.StartsAt);
        // Register the 11:00 occurrence before sleep, then wake during lunch.
        scheduler.Schedule(data.RecurringAgendaFor(day).First());
        now = day.AddHours(12).AddMinutes(30);
        await scheduler.PollNowAsync();
        Assert.Empty(received);
        now = day.AddHours(13);
        await scheduler.PollNowAsync();
        await scheduler.PollNowAsync();
        Assert.Equal(new[] { now }, received);
        now = day.AddHours(15);
        await scheduler.PollNowAsync();
        Assert.Equal(new[] { day.AddHours(13), day.AddHours(15) }, received);
        Assert.Null(scheduler.Health.LastErrorMessage);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Removed_interval_plan_does_not_deliver_queued_occurrences_or_notifications(
        bool delete, bool restart, bool alreadyInOutbox)
    {
        var (service, _) = WorkService();
        var card = (await Send(service, WorkRequest)).Interaction!;
        var preview = (await Click(service, card, "submit", WorkAnswers())).Interaction!;
        await Click(service, preview, "confirm");
        var reminder = Assert.Single(data.RecurringReminders());
        var dueAt = reminder.Schedule!.Next(DateTime.Now)[0];
        var occurrence = data.RecurringAgendaFor(dueAt.Date).Single(item => item.StartsAt == dueAt);
        var now = dueAt.AddMinutes(-10);
        ReminderService CreateScheduler() => new(data, new LifePreferencesService(),
            new WindowsNotificationService(), localNow: () => now);
        var scheduler = CreateScheduler();
        try
        {
            // The task was registered before the user removed it, as in the reported case.
            scheduler.Schedule(occurrence);
            if (alreadyInOutbox)
            {
                var store = new ReminderDeliveryStore(LifeDataStoreRuntimeRegistry.GetOrCreate(path).WriteQueue);
                var detector = new ReminderDueDetector(store);
                var lease = Assert.IsType<ReminderOccurrenceLease>(
                    detector.TryClaimDue(new DateTimeOffset(dueAt), "queued-before-removal"));
                Assert.True(store.EnqueueNotification(new("queued-before-removal", occurrence.Id,
                    lease.OccurrenceKey, "ToastAndIsland", new DateTimeOffset(dueAt))));
                Assert.True(detector.MarkDelivered(lease, new DateTimeOffset(dueAt)));
            }
            if (delete) scheduler.Delete(occurrence);
            else scheduler.Archive(occurrence);
            Assert.Empty(data.RecurringReminders());
            if (restart)
            {
                scheduler.Dispose();
                scheduler = CreateScheduler();
            }
            // Suppressing a removed plan must not suppress unrelated active reminders.
            var other = data.SaveReminder("保留的其他提醒", null, dueAt);
            var received = new List<AgendaItem>();
            scheduler.ReminderDue += (_, item) => received.Add(item);
            now = dueAt;
            await scheduler.PollNowAsync();
            now = dueAt.AddMinutes(2);
            await scheduler.PollNowAsync();
            Assert.Equal(other.Id, Assert.Single(received).Id);
            Assert.Null(scheduler.Health.LastErrorMessage);
            Assert.Null(scheduler.NotificationHealth.LastError);
            using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT delivery_status FROM reminder_occurrences WHERE item_id=$id";
            command.Parameters.AddWithValue("$id", occurrence.Id);
            Assert.Equal("Delivered", command.ExecuteScalar());
            command.CommandText = "SELECT COUNT(*) FROM notification_outbox WHERE item_id=$id";
            Assert.Equal(alreadyInOutbox ? 1L : 0L, command.ExecuteScalar());
        }
        finally { scheduler.Dispose(); }
    }

    [Fact]
    public async Task Natural_language_correction_preserves_previous_structured_answers()
    {
        var initial = new AssistantTaskDraft("create_reminder", WorkRequest, "站起来活动", RepeatText: "每天");
        var corrected = initial with { Evidence = "改成每隔1小时", Schedule = new(IntervalText: "1小时") };
        var service = Service(new SequenceInterpreter([new(3,"tasks",[initial]), new(3,"tasks",[corrected])]));
        var card = (await Send(service, WorkRequest)).Interaction!;
        var preview = (await Click(service, card, "submit", WorkAnswers())).Interaction!;
        var next = await Send(service, "改成每隔1小时");
        Assert.Equal("NeedsConfirmation", next.Interaction?.State);
        Assert.Contains("09:00-18:00", next.Interaction!.Summary);
        Assert.Contains("12:00-13:00", next.Interaction.Summary);
        Assert.Contains("60", next.Interaction.Summary);
        await Click(service, preview, "confirm");
        Assert.Empty(data.RecurringReminders());
        await Click(service, next.Interaction, "confirm");
        Assert.Equal(60, Assert.Single(data.RecurringReminders()).Schedule!.IntervalMinutes);
    }

    sealed class SequenceInterpreter(AssistantUnderstanding[] answers) : IAssistantDraftInterpreter
    {
        int index;
        public Task<AssistantUnderstanding> UnderstandAsync(ProviderSettings provider, string input,
            AssistantDraftTurn turn, IReadOnlyList<ChatMessage> history, CancellationToken token) => Task.FromResult(answers[index++]);
    }

    [Fact]
    public async Task Model_omitted_daily_rule_is_recovered_from_grounded_request()
    {
        var text = "每天的凌晨12点提醒我睡觉";
        var draft = new AssistantTaskDraft("create_reminder", text, "睡觉", TimeText: "凌晨12点");
        var turn = new AssistantDraftTurn(Guid.NewGuid().ToString("N"), session.Id, 1, "Understanding",
            text, DateTimeOffset.Now, "Asia/Shanghai", DateTimeOffset.UtcNow.AddMinutes(10), [draft], [],
            new Dictionary<int,AssistantPlanCandidateBindingV2>(), new Dictionary<string,AssistantPlanCandidateBindingV2>());
        var compiled = AssistantDraftCompiler.Compile(turn);
        Assert.Equal(AssistantCommandName.CreateRecurringTask, Assert.Single(compiled.Commands).Command);
        Assert.Empty(compiled.Fields);
        var interpreter = new FixedInterpreter(new(3,"tasks",[draft]));
        var result = await Send(Service(interpreter), "每天的凌晨12点提醒我睡觉，谢谢");
        Assert.True(result.RefreshReminders);
        Assert.Single(data.RecurringReminders());
    }

    [Fact]
    public async Task Simple_lunch_reminder_is_not_misclassified_as_an_interval()
    {
        var result = await Send(Service(new FixedInterpreter(new(3, "tasks", [new("create_reminder", "明天中午12点提醒我午休", "午休", TimeText: "明天中午12点")]))), "明天中午12点提醒我午休");
        Assert.True(result.RefreshReminders, result.Reply);
        Assert.Equal("午休", Assert.Single(data.ReminderItems().Where(i => i.Kind == "reminder")).Title);
        Assert.Empty(data.RecurringReminders());
    }

    [Fact]
    public async Task Fixed_daily_times_are_one_confirmed_series_and_unsupported_constraints_block()
    {
        var text = "每天上午9点和下午3点提醒我喝水";
        var service = Service(new FixedInterpreter(new(3, "tasks",
            [new("create_reminder", text, "喝水", TimeText: "上午9点和下午3点", RepeatText: "每天")])));
        var result = await Send(service, text);
        Assert.Equal("NeedsConfirmation", result.Interaction?.State);
        await Click(service, result.Interaction!, "confirm");
        Assert.Equal(new[] { new TimeOnly(9,0), new TimeOnly(15,0) }, Assert.Single(data.RecurringReminders()).Schedule!.Times);
        var unsupported = Service(new FixedInterpreter(new(3, "tasks",
            [new("create_reminder", "开车时不要提醒", "喝水", UnhandledConstraints: ["开车时不要提醒"])])));
        var blocked = await Send(unsupported, "开车时不要提醒");
        Assert.Equal("Blocked", blocked.Interaction?.State);
        var edit = (await Click(unsupported, blocked.Interaction!, "edit")).Interaction!;
        Assert.Equal("request", Assert.Single(edit.Fields).Key);
        Assert.Single(data.RecurringReminders());
    }
}

public sealed class ReminderDailyScheduleTests
{
    [Theory]
    [InlineData("skip", "start", "09:00,11:00,13:00,15:00,17:00")]
    [InlineData("skip", "after_interval", "11:00,13:00,15:00,17:00")]
    [InlineData("restart", "start", "09:00,11:00,15:00,17:00")]
    [InlineData("restart", "after_interval", "11:00,15:00,17:00")]
    public void Working_window_generates_explicit_times(string rhythm, string first, string expected)
    {
        var times = ReminderDailySchedule.GenerateTimes(new(new(9,0),new(18,0)),120,
            [new(new(12,0),new(13,0))],first,rhythm);
        Assert.Equal(expected,string.Join(",",times.Select(t=>t.ToString("HH:mm"))));
    }
    [Theory]
    [InlineData("skip")]
    [InlineData("restart")]
    public void Midnight_end_is_exclusive_and_exclusions_can_end_at_midnight(string rhythm)
    {
        var window = new ReminderTimeWindow(new(18,0),new(0,0));
        Assert.Null(window.ValidationError);
        Assert.True(window.Contains(new(23,59)));
        Assert.False(window.Contains(TimeOnly.MinValue));
        Assert.Equal(new[] { new TimeOnly(20,0) }, ReminderDailySchedule.GenerateTimes(window,120,
            [new(new(22,0),new(0,0))],"after_interval",rhythm));
        var json = AssistantDraftJson.Serialize(window);
        Assert.DoesNotContain("endMinute", json);
        Assert.DoesNotContain("validationError", json);
        Assert.Equal(window, AssistantDraftJson.Read<ReminderTimeWindow>(json));
    }
    [Fact]
    public void Queued_occurrences_cannot_escape_lunch_or_resume_as_a_flood()
    {
        var day = new DateOnly(2026,9,21);
        var schedule = new ReminderDailySchedule([new(11,0),new(13,0),new(15,0),new(17,0)],
            "weekdays",[],day,day,"活动",120,new(new(9,0),new(18,0)),[new(new(12,0),new(13,0))],"after_interval","skip");
        schedule.Validate();
        DateTime At(int h) => day.ToDateTime(new(h,0));
        Assert.True(schedule.CanDeliver(At(11),At(11).AddSeconds(20)));
        Assert.False(schedule.CanDeliver(At(11),At(12)));
        Assert.False(schedule.CanDeliver(At(11),At(15)));
        Assert.True(schedule.CanDeliver(At(15),At(15)));
        Assert.False(schedule.CanDeliver(At(17),At(18)));
        Assert.False(schedule.CanDeliver(At(11),At(11).AddDays(1)));
        Assert.Empty(schedule.Next(At(18)));
    }
}

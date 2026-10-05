using ChronoIsle.App.Services.Commanding;
using ChronoIsle.App;
using ChronoIsle.App.Services.Domain;
using ChronoIsle.App.Services;
using ChronoIsle.App.Services.Persistence;
using ChronoIsle.App.Services.Scheduling;

namespace ChronoIsle.Tests;

public sealed partial class AssistantDraftRuntimeTests
{
    [Fact]
    public async Task Unmatched_target_lists_all_items_and_selected_deletes_are_confirmed_together()
    {
        for (var i = 0; i < 25; i++) data.Save($"事项{i:00}", null, null, null);
        var service = Service(new FixedInterpreter(new(3, "tasks", [new("delete_todo", "删除这些", Target: "这些")])));
        var result = await Send(service, "删除这些");
        var field = Assert.Single(result.Interaction!.Fields);
        Assert.Equal("multichoice", field.Kind);
        Assert.Equal(25, field.Options.Count);
        var chosen = field.Options.Take(2).ToArray();
        var preview = await Click(service, result.Interaction, "submit", new() { [field.Key] = string.Join(',', chosen.Select(o => o.Value)) });
        Assert.Equal("NeedsConfirmation", preview.Interaction?.State);
        Assert.Equal(25, data.Todos().Count);
        foreach (var option in chosen) Assert.Contains(option.Label.Split('·')[0].Trim(), preview.Interaction!.Summary);
        var done = await Click(service, preview.Interaction!, "confirm");
        Assert.True(done.RefreshReminders, done.Reply);
        Assert.Equal(23, data.Todos().Count);
        await Click(service, preview.Interaction!, "confirm");
        Assert.Equal(23, data.Todos().Count);
    }

    [Fact]
    public async Task Ambiguous_target_offers_unmatched_items_too_and_empty_selection_cannot_execute()
    {
        data.Save("购物", null, null, null);
        data.Save("购物", null, null, null);
        data.Save("睡觉", null, null, null);
        var service = Service(new FixedInterpreter(new(3, "tasks", [new("delete_todo", "删除购物", Target: "购物")])));
        var result = await Send(service, "删除购物");
        var field = Assert.Single(result.Interaction!.Fields);
        Assert.Equal(3, field.Options.Count);
        Assert.Null(field.Value);
        var invalid = await Click(service, result.Interaction, "submit", new() { [field.Key] = "" });
        Assert.Equal("NeedsInput", invalid.Interaction?.State);
        Assert.Equal(3, data.Todos().Count);
    }

    [Fact]
    public async Task Editing_delete_confirmation_can_reselect_target()
    {
        data.Save("甲", null, null, null);
        data.Save("乙", null, null, null);
        var service = Service(new FixedInterpreter(new(3, "tasks", [new("delete_todo", "删除甲", Target: "甲")])));
        var pending = await Send(service, "删除甲");
        var edit = await Click(service, pending.Interaction!, "edit");
        Assert.True(edit.Interaction!.CanSubmit);
        Assert.Contains(edit.Interaction.Fields, f => f.Key.EndsWith(".candidate"));
        await Click(service, pending.Interaction!, "confirm");
        Assert.Equal(2, data.Todos().Count);
    }

    [Fact]
    public async Task Holiday_time_change_keeps_workday_midnight_and_existing_identity()
    {
        var original = data.SaveRecurringReminder("睡觉", "保留备注", TimeOnly.MinValue, RecurrenceKind.Daily, []);
        const string request = "将节假日改为凌晨2点睡；工作日保持不变，仍为12点睡";
        var service = Service(new FixedInterpreter(new(3, "tasks", [new("update_todo", request,
            Schedule: new(DayOverrides: [new("节假日", "凌晨2点")]))])));
        var choose = await Send(service, request);
        var item = Assert.Single(choose.Interaction!.Fields);
        var chosen = await Click(service, choose.Interaction, "submit", new() { [item.Key] = Assert.Single(item.Options).Value });
        var category = Assert.Single(chosen.Interaction!.Fields);
        Assert.Equal("0.schedule.override0days", category.Key);
        Assert.Null(category.Value);
        var preview = await Click(service, chosen.Interaction, "submit", new() { [category.Key] = "rest" });
        Assert.Equal("NeedsConfirmation", preview.Interaction?.State);
        Assert.Contains("其余执行日保持：00:00", preview.Interaction!.Summary);
        Assert.Contains("02:00", preview.Interaction.Summary);
        Assert.Null(Assert.Single(data.RecurringReminders()).Schedule);
        var saved = await Click(service, Service().GetInteraction(session.Id)!, "confirm");
        Assert.True(saved.RefreshReminders, saved.Reply);
        var after = Assert.Single(data.RecurringReminders());
        Assert.Equal(original.Id, after.Id);
        Assert.Equal("保留备注", after.Notes);
        var schedule = after.Schedule! with { StartsOn = new(2026, 1, 1) };
        Assert.Equal(new DateTime(2026, 10, 4, 2, 0, 0), Assert.Single(schedule.Occurrences(new(2026, 10, 4))));
        Assert.Equal(new DateTime(2026, 10, 10, 0, 0, 0), Assert.Single(schedule.Occurrences(new(2026, 10, 10))));
        Assert.False(schedule.CanDeliver(new(2026, 10, 4, 0, 0, 0), new(2026, 10, 4, 2, 0, 0)));
        Assert.True(schedule.CanDeliver(new(2026, 10, 4, 2, 0, 0), new(2026, 10, 4, 2, 0, 0)));
    }

    [Fact]
    public async Task Unsupported_request_can_be_corrected_in_fields_without_another_model_call()
    {
        data.SaveRecurringReminder("睡觉", null, TimeOnly.MinValue, RecurrenceKind.Daily, []);
        var interpreter = new FixedInterpreter(new(3, "tasks", [new("update_todo", "修改睡觉", Target: "睡觉",
            UnhandledConstraints: ["工作日保持不变，仍为12点睡"])]));
        var service = Service(interpreter);
        var correction = await Send(service, "修改睡觉");
        Assert.Equal("NeedsInput", correction.Interaction?.State);
        var choice = correction.Interaction!.Fields.Single(f => f.Key == "0.candidate");
        var preview = await Click(service, correction.Interaction, "submit", new()
        {
            [choice.Key] = choice.Options[0].Value, ["correction.scope"] = "partial",
            ["correction.days"] = "rest", ["correction.times"] = "02:00"
        });
        Assert.Equal("NeedsConfirmation", preview.Interaction?.State);
        Assert.Equal(1, interpreter.Calls);
        Assert.Null(Assert.Single(data.RecurringReminders()).Schedule);
        var done = await Click(service, preview.Interaction!, "confirm");
        Assert.True(done.RefreshReminders, done.Reply);
        Assert.Equal(TimeOnly.MinValue, Assert.Single(data.RecurringReminders()).Schedule!.Times[0]);
    }

    [Fact]
    public async Task Batch_partial_updates_preserve_each_targets_own_default_time()
    {
        var a = data.SaveRecurringReminder("甲", "A", new(23, 0), RecurrenceKind.Daily, []);
        var b = data.SaveRecurringReminder("乙", "B", TimeOnly.MinValue, RecurrenceKind.Daily, []);
        var service = Service(new FixedInterpreter(new(3, "tasks", [new("update_todo", "修改休息日的时间",
            Schedule: new(DayOverrides: [new("法定休息日", "02:00")]))])));
        var choose = await Send(service, "修改休息日的时间");
        var field = Assert.Single(choose.Interaction!.Fields);
        var preview = await Click(service, choose.Interaction, "submit", new() { [field.Key] = string.Join(',', field.Options.Select(o => o.Value)) });
        Assert.Equal("NeedsConfirmation", preview.Interaction?.State);
        await Click(service, preview.Interaction!, "confirm");
        Assert.Equal(new TimeOnly(23, 0), data.RecurringReminders().Single(r => r.Id == a.Id).Schedule!.Times[0]);
        Assert.Equal(TimeOnly.MinValue, data.RecurringReminders().Single(r => r.Id == b.Id).Schedule!.Times[0]);
        Assert.All(data.RecurringReminders(), r => Assert.Equal(new TimeOnly(2, 0), r.Schedule!.DayOverrides![0].Times[0]));
    }

    [Fact]
    public async Task Oversized_selection_keeps_all_checks_and_never_partially_executes()
    {
        for (var i = 0; i < 4; i++) data.Save($"事项{i}", null, null, null);
        var service = Service(new FixedInterpreter(new(3, "tasks", [new("delete_todo", "删除事项")])));
        var first = await Send(service, "删除事项");
        var field = Assert.Single(first.Interaction!.Fields);
        var value = string.Join(',', field.Options.Select(o => o.Value));
        var rejected = await Click(service, first.Interaction, "submit", new() { [field.Key] = value });
        Assert.Equal("NeedsInput", rejected.Interaction?.State);
        Assert.Equal(value, Assert.Single(rejected.Interaction!.Fields).Value);
        Assert.Contains("最多", Assert.Single(rejected.Interaction.Fields).Error);
        Assert.Equal(4, data.Todos().Count);
    }

    [Fact]
    public async Task Reselecting_during_clarification_keeps_patch_but_reads_new_targets_original_time()
    {
        data.SaveRecurringReminder("甲", null, new(23, 0), RecurrenceKind.Daily, []);
        var b = data.SaveRecurringReminder("乙", null, TimeOnly.MinValue, RecurrenceKind.Daily, []);
        var service = Service(new FixedInterpreter(new(3, "tasks", [new("update_todo", "甲的节假日改为02:00", Target: "甲",
            Schedule: new(DayOverrides: [new("节假日", "02:00")]))])));
        var first = await Send(service, "甲的节假日改为02:00");
        Assert.True(first.Interaction!.CanReselect);
        var choose = await Click(service, first.Interaction, "select_targets");
        var field = Assert.Single(choose.Interaction!.Fields);
        var selected = await Click(service, choose.Interaction, "submit", new() { [field.Key] = field.Options.Single(o => o.Label.StartsWith("乙 ·")).Value });
        var preview = await Click(service, selected.Interaction!, "submit", new() { ["0.schedule.override0days"] = "rest" });
        Assert.Equal("NeedsConfirmation", preview.Interaction?.State);
        Assert.Contains("其余执行日保持：00:00", preview.Interaction!.Summary);
        await Click(service, preview.Interaction, "confirm");
        Assert.Null(data.RecurringReminders().Single(r => r.Id != b.Id).Schedule);
        Assert.Equal(TimeOnly.MinValue, data.RecurringReminders().Single(r => r.Id == b.Id).Schedule!.Times[0]);
    }

    [Fact]
    public async Task Editing_confirmation_target_does_not_copy_unchanged_original_fields_to_new_item()
    {
        data.SaveRecurringReminder("甲", null, new(23, 0), RecurrenceKind.Daily, []);
        var b = data.SaveRecurringReminder("乙", null, TimeOnly.MinValue, RecurrenceKind.Daily, []);
        var service = Service(new FixedInterpreter(new(3, "tasks", [new("update_todo", "甲的周末改为02:00", Target: "甲",
            Schedule: new(DayOverrides: [new("周末", "02:00")]))])));
        var first = await Send(service, "甲的周末改为02:00");
        var edit = await Click(service, first.Interaction!, "edit");
        var fields = edit.Interaction!.Fields;
        var values = fields.Where(f => f.DependsOn is null || fields.First(d => d.Key == f.DependsOn).Value == f.DependsValue)
            .ToDictionary(f => f.Key, f => f.Value ?? "");
        values["0.candidate"] = fields.Single(f => f.Key == "0.candidate").Options.Single(o => o.Label.StartsWith("乙 ·")).Value;
        var preview = await Click(service, edit.Interaction, "submit", values);
        Assert.Equal("NeedsConfirmation", preview.Interaction?.State);
        await Click(service, preview.Interaction!, "confirm");
        Assert.Equal(TimeOnly.MinValue, data.RecurringReminders().Single(r => r.Id == b.Id).Schedule!.Times[0]);
        Assert.Null(data.RecurringReminders().Single(r => r.Id != b.Id).Schedule);
    }

    [Fact]
    public async Task Editing_override_category_replaces_it_instead_of_leaving_the_old_rule()
    {
        data.SaveRecurringReminder("睡觉", null, TimeOnly.MinValue, RecurrenceKind.Daily, []);
        var service = Service(new FixedInterpreter(new(3, "tasks", [new("update_todo", "睡觉周末改为02:00", Target: "睡觉",
            Schedule: new(DayOverrides: [new("周末", "02:00")]))])));
        var first = await Send(service, "睡觉周末改为02:00");
        await Click(service, first.Interaction!, "confirm");
        var next = await Send(service, "睡觉周末改为02:00");
        var edit = await Click(service, next.Interaction!, "edit");
        var fields = edit.Interaction!.Fields;
        var values = fields.Where(f => f.DependsOn is null || fields.First(d => d.Key == f.DependsOn).Value == f.DependsValue)
            .ToDictionary(f => f.Key, f => f.Value ?? "");
        values["0.schedule.override0days"] = "rest";
        values["0.schedule.override0times"] = "03:00";
        var preview = await Click(service, edit.Interaction, "submit", values);
        Assert.Equal("NeedsConfirmation", preview.Interaction?.State);
        await Click(service, preview.Interaction!, "confirm");
        var rule = Assert.Single(Assert.Single(data.RecurringReminders()).Schedule!.DayOverrides!);
        Assert.Equal("rest", rule.DayPattern);
        Assert.Equal(new TimeOnly(3, 0), Assert.Single(rule.Times));
    }

    [Fact]
    public async Task Override_without_any_existing_execution_day_requires_correction()
    {
        data.SaveRecurringReminder("睡觉", null, TimeOnly.MinValue, RecurrenceKind.Weekdays, []);
        var service = Service(new FixedInterpreter(new(3, "tasks", [new("update_todo", "睡觉周末改为02:00", Target: "睡觉",
            Schedule: new(DayOverrides: [new("周末", "02:00")]))])));
        var result = await Send(service, "睡觉周末改为02:00");
        Assert.Equal("NeedsInput", result.Interaction?.State);
        Assert.Contains("不相交", result.Reply);
        Assert.False(result.Interaction!.CanConfirm);
        Assert.Null(Assert.Single(data.RecurringReminders()).Schedule);
    }

    [Fact]
    public async Task Old_stuck_request_card_upgrades_to_structured_choices_without_executing()
    {
        data.SaveRecurringReminder("睡觉", null, TimeOnly.MinValue, RecurrenceKind.Daily, []);
        var service = Service(new FixedInterpreter(new(3, "tasks", [new("update_todo", "修改睡觉", Target: "睡觉",
            UnhandledConstraints: ["工作日保持不变"])])));
        await Send(service, "修改睡觉");
        var store = new AssistantDraftStore(path);
        var active = store.Active(session.Id)!;
        store.Save(active with { InteractionVersion = 2, Fields = [new("request", "调整需求", "text", [], "修改睡觉")] }, active.Revision);
        var restored = Service().GetInteraction(session.Id)!;
        Assert.Contains(restored.Fields, f => f.Key == "correction.scope");
        Assert.DoesNotContain(restored.Fields, f => f.Key == "request");
        Assert.Null(Assert.Single(data.RecurringReminders()).Schedule);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Partial_day_change_suppresses_old_notifications_in_cache_and_outbox(bool queuedInOutbox)
    {
        var original = data.SaveRecurringReminder("睡觉", null, new(0, 0), RecurrenceKind.Daily, []);
        var day = DateTime.Today.AddDays(1);
        while (day.DayOfWeek != DayOfWeek.Sunday) day = day.AddDays(1);
        var occurrence = Assert.Single(data.RecurringAgendaFor(day));
        var now = day.AddMinutes(-5);
        using var scheduler = new ReminderService(data, new LifePreferencesService(), new WindowsNotificationService(), localNow: () => now);
        scheduler.Schedule(occurrence);
        if (queuedInOutbox)
        {
            var store = new ReminderDeliveryStore(LifeDataStoreRuntimeRegistry.GetOrCreate(path).WriteQueue);
            var detector = new ReminderDueDetector(store);
            var lease = Assert.IsType<ReminderOccurrenceLease>(detector.TryClaimDue(new DateTimeOffset(occurrence.StartsAt), "before-partial-update"));
            Assert.True(store.EnqueueNotification(new("before-partial-update", original.Id, lease.OccurrenceKey, "ToastAndIsland", new DateTimeOffset(occurrence.StartsAt))));
            Assert.True(detector.MarkDelivered(lease, new DateTimeOffset(occurrence.StartsAt)));
        }
        var service = Service(new FixedInterpreter(new(3, "tasks", [new("update_todo", "睡觉周末改为02:00", Target: "睡觉",
            Schedule: new(DayOverrides: [new("周末", "02:00")]))])));
        var pending = await Send(service, "睡觉周末改为02:00");
        Assert.True((await Click(service, pending.Interaction!, "confirm")).RefreshReminders);
        var received = new List<AgendaItem>();
        scheduler.ReminderDue += (_, item) => received.Add(item);
        now = day;
        await scheduler.PollNowAsync();
        Assert.Empty(received);
        now = day.AddHours(2);
        scheduler.RefreshSchedule();
        await scheduler.PollNowAsync();
        Assert.Equal(original.Id, Assert.Single(received).Id);
        Assert.Equal(now, received[0].StartsAt);
    }

    [Fact]
    public void Override_json_roundtrip_preserves_old_schedule_and_rejects_overlapping_day_categories()
    {
        var old = new ReminderDailySchedule([new(0, 0)], "daily", [], new(2026, 1, 1), null, "原规则");
        var restored = AssistantDraftJson.Read<ReminderDailySchedule>(AssistantDraftJson.Serialize(old));
        restored.Validate();
        Assert.Null(restored.DayOverrides);
        var invalid = old with { DayOverrides = [new("rest", [new(2, 0)]), new("weekends", [new(3, 0)])] };
        Assert.Throws<ArgumentException>(() => invalid.Validate());
    }

    [ModelEvaluationFact]
    public async Task Configured_provider_handles_reported_partial_change_through_selection_and_confirmation()
    {
        data.SaveRecurringReminder("睡觉", null, TimeOnly.MinValue, RecurrenceKind.Daily, []);
        var service = Service(new AssistantDraftInterpreter(new ChronoIsle.App.Services.OpenAiChatService()));
        var result = await service.HandleAsync(new ChronoIsle.App.Services.ProviderSettingsService().Load(), session, [],
            "将节假日改为凌晨2点睡；工作日保持不变，仍为12点睡");
        for (var i = 0; i < 3 && result.Interaction?.State == "NeedsInput"; i++)
        {
            var answers = result.Interaction.Fields.ToDictionary(f => f.Key, f => f.Key.EndsWith(".candidate") ? f.Options[0].Value
                : f.Key.EndsWith("days") ? "rest" : f.Key == "correction.scope" ? "partial" : "02:00");
            result = await Click(service, result.Interaction, "submit", answers);
        }
        Assert.Equal("NeedsConfirmation", result.Interaction?.State);
        var done = await Click(service, result.Interaction!, "confirm");
        Assert.True(done.RefreshReminders, done.Reply);
        var schedule = Assert.Single(data.RecurringReminders()).Schedule!;
        Assert.Equal(TimeOnly.MinValue, Assert.Single(schedule.Times));
        Assert.Equal(new TimeOnly(2, 0), Assert.Single(Assert.Single(schedule.DayOverrides!).Times));
    }
}

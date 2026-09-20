using System.Net;
using System.Net.Http;
using ChronoIsle.App;
using ChronoIsle.App.Services;
using ChronoIsle.App.Services.Commanding;

namespace ChronoIsle.Tests;

// These tests exercise the shared SQLite pool and WPF-facing view model; isolate their lifecycle.
[CollectionDefinition("Assistant draft runtime", DisableParallelization = true)]
public sealed class AssistantDraftRuntimeCollection { }

[Collection("Assistant draft runtime")]
public sealed partial class AssistantDraftRuntimeTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "chronoisle-draft-tests", Guid.NewGuid().ToString("N"));
    readonly LifeDataService data;
    readonly ChatSession session;
    readonly string path;

    public AssistantDraftRuntimeTests()
    {
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "life.db");
        data = new LifeDataService(path);
        session = data.NewSession();
    }

    AssistantActionService Service(IAssistantDraftInterpreter? interpreter = null) => new(data,
        new ChinaStatutoryHolidayCalendar(), new ConversationRouter(), new LocalAgendaQueryService(data),
        new NoChat(), draftInterpreter: interpreter ?? new NeverInterpreter());

    Task<AssistantConversationResult> Send(AssistantActionService service, string text) =>
        service.HandleAsync(ProviderSettings.Default, session, [], text);

    Task<AssistantConversationResult> Click(AssistantActionService service, AssistantInteraction card,
        string action, Dictionary<string, string>? values = null) => service.InteractAsync(
            ProviderSettings.Default, session, card.RequestId, card.Revision, action, values ?? []);

    [Fact]
    public async Task Daily_midnight_reminder_is_locally_compiled_and_committed_once()
    {
        var service = Service();
        var result = await Send(service, "每天的凌晨12点提醒我睡觉");
        Assert.False(result.IsFailure);
        Assert.True(result.RefreshReminders);
        Assert.Null(result.Interaction);
        var item = Assert.Single(data.RecurringReminders());
        Assert.Equal("睡觉", item.Title);
        Assert.Equal(TimeOnly.MinValue, item.ReminderTime);
        Assert.Equal(RecurrenceKind.Daily, item.Recurrence);
    }

    [Fact]
    public async Task Missing_time_uses_typed_card_without_another_model_call_and_restores_after_restart()
    {
        var service = Service();
        var first = await Send(service, "提醒我睡觉");
        var card = Assert.IsType<AssistantInteraction>(first.Interaction);
        Assert.Equal("NeedsInput", card.State);
        Assert.Equal("datetime", Assert.Single(card.Fields).Kind);
        Assert.Empty(data.ReminderItems());
        var restarted = Service();
        Assert.Equal(card.RequestId, restarted.GetInteraction(session.Id)!.RequestId);
        var when = DateTime.Now.AddDays(2).ToString("yyyy-MM-dd") + " 00:00";
        var result = await Click(restarted, card, "submit", new() { ["0.timeText"] = when });
        Assert.True(result.RefreshReminders);
        Assert.Equal("睡觉", Assert.Single(data.ReminderItems()).Title);
        await Click(restarted, card, "submit", new() { ["0.timeText"] = when });
        Assert.Single(data.ReminderItems());
    }

    [Fact]
    public async Task Ambiguous_twelve_oclock_requires_explicit_date_and_time()
    {
        var result = await Send(Service(), "12点提醒我睡觉");
        Assert.Equal("NeedsInput", result.Interaction?.State);
        Assert.Equal("datetime", Assert.Single(result.Interaction!.Fields).Kind);
        Assert.Empty(data.ReminderItems());
    }

    [Fact]
    public async Task Multiple_targets_are_options_and_old_confirmations_cannot_execute_after_edit()
    {
        data.Save("买牛奶", null, null, null);
        data.Save("买牛奶", null, null, null);
        var service = Service(new FixedInterpreter(new(3, "tasks", [new("reschedule_item", "把买牛奶改到明天下午三点", Target: "买牛奶", TimeText: "明天下午三点")])));
        var result = await Send(service, "把买牛奶改到明天下午三点");
        var card = result.Interaction!;
        var choice = Assert.Single(card.Fields);
        Assert.Equal(2, choice.Options.Count);
        var selected = await Click(service, card, "submit", new() { [choice.Key] = choice.Options[1].Value });
        Assert.True(selected.Interaction!.CanConfirm);
        var confirmation = selected.Interaction;
        var edit = await Click(service, confirmation, "edit");
        Assert.True(edit.Interaction!.CanSubmit);
        var stale = await Click(service, confirmation, "confirm");
        Assert.False(stale.RefreshReminders);
        Assert.All(data.Todos(), item => Assert.Null(item.DueAt));
        await Click(service, edit.Interaction, "cancel");
        Assert.Null(service.GetInteraction(session.Id));
    }

    [Fact]
    public async Task Card_selection_rejects_forged_candidate_and_cross_session_requests()
    {
        data.Save("购物", null, null, null);
        data.Save("购物", null, null, null);
        var service = Service(new FixedInterpreter(new(3, "tasks", [new("delete_todo", "删除购物", Target: "购物")])));
        var result = await Send(service, "删除购物");
        var card = result.Interaction!;
        var forged = await Click(service, card, "submit", new() { [card.Fields[0].Key] = "invented-id" });
        Assert.False(forged.RefreshReminders);
        var other = await service.InteractAsync(ProviderSettings.Default, data.NewSession(), card.RequestId, card.Revision, "confirm", new Dictionary<string, string>());
        Assert.True(other.IsFailure);
        Assert.Equal(2, data.Todos().Count);
    }

    [Fact]
    public async Task Multi_step_plan_keeps_completed_fields_and_writes_nothing_until_all_confirmed()
    {
        var interpreter = new FixedInterpreter(new(3, "tasks",
            [new("create_todo", "买牛奶", "买牛奶"), new("create_reminder", "提醒我睡觉", "睡觉")]));
        var service = Service(interpreter);
        var result = await Send(service, "添加待办买牛奶，然后提醒我睡觉");
        Assert.Empty(data.Todos());
        var card = result.Interaction!;
        Assert.Equal("1.timeText", Assert.Single(card.Fields).Key);
        var next = await Click(service, card, "submit", new() { ["1.timeText"] = DateTime.Now.AddDays(1).ToString("yyyy-MM-dd") + " 23:00" });
        Assert.True(next.Interaction!.CanConfirm);
        Assert.Empty(data.Todos());
        var done = await Click(service, next.Interaction, "confirm");
        Assert.True(done.RefreshReminders);
        Assert.Single(data.Todos());
        Assert.Single(data.ReminderItems());
        Assert.Equal(1, interpreter.Calls);
    }

    [Fact]
    public async Task Cancellation_preserves_draft_and_does_not_write()
    {
        var interpreter = new BlockingInterpreter();
        var service = Service(interpreter);
        using var cancellation = new CancellationTokenSource();
        var handling = service.HandleAsync(ProviderSettings.Default, session, [], "帮我安排一下事情", cancellationToken: cancellation.Token);
        await interpreter.Started.Task;
        cancellation.Cancel();
        var result = await handling;
        Assert.Equal("Interrupted", result.Interaction?.State);
        Assert.Empty(data.Todos());
        Assert.Empty(data.ReminderItems());
    }

    [Fact]
    public async Task Query_range_is_a_choice_and_runs_without_model_on_selection()
    {
        var interpreter = new FixedInterpreter(new(3, "query", [new("list_items", "查看事项")]));
        var service = Service(interpreter);
        var result = await Send(service, "查看事项");
        Assert.Equal("choice", Assert.Single(result.Interaction!.Fields.Where(f => f.DependsOn is null)).Kind);
        var completed = await Click(service, result.Interaction, "submit", new() { ["0.timeText"] = "明天" });
        Assert.Null(completed.Interaction);
        Assert.False(completed.RefreshReminders);
        Assert.Equal(1, interpreter.Calls);
    }

    [Fact]
    public async Task Receipt_recovers_commit_even_if_ui_result_was_not_saved()
    {
        var pipeline = new AssistantPlanPipeline(path);
        var request = Guid.NewGuid().ToString("N");
        var turn = new AssistantDraftTurn(request, session.Id, 1, "Prepared", "添加待办购物",
            DateTimeOffset.Now, "Asia/Shanghai", DateTimeOffset.UtcNow.AddMinutes(15),
            [new("create_todo", "添加待办购物", "购物")], [], new Dictionary<int, AssistantPlanCandidateBindingV2>(),
            new Dictionary<string, AssistantPlanCandidateBindingV2>(), "创建：购物", $"draft_{request}_1");
        var store = new AssistantDraftStore(path);
        store.Save(turn);
        var plan = pipeline.PrepareDraftPlan(request, 1, AssistantDraftCompiler.Compile(turn).Commands, []);
        pipeline.ConfirmPlan(plan.ConfirmationId!);
        var service = Service();
        var result = await Click(service, service.GetInteraction(session.Id)!, "retry");
        Assert.True(result.RefreshReminders);
        Assert.Null(result.Interaction);
        Assert.Single(data.Todos());
    }

    [Theory]
    [InlineData("{\"version\":3,\"kind\":\"tasks\",\"tasks\":[{\"operation\":\"create_todo\",\"evidence\":\"购物\",\"title\":\"购物\",\"id\":\"forged\"}]}")]
    [InlineData("{\"version\":3,\"version\":3,\"kind\":\"chat\",\"tasks\":[],\"reply\":\"你好\"}")]
    [InlineData("{\"version\":3,\"kind\":\"tasks\",\"tasks\":[{\"operation\":\"create_reminder\",\"evidence\":\"购物\",\"title\":\"购物\",\"timeText\":\"明天09:00\"}]}")]
    public void Model_drafts_reject_unknown_fields_duplicates_and_invented_time(string response) =>
        Assert.ThrowsAny<Exception>(() => AssistantDraftInterpreter.Parse(response, "购物"));

    [Theory]
    [InlineData("明天凌晨十二点", "2026-09-21 00:00")]
    [InlineData("明天下午三点半", "2026-09-21 15:30")]
    [InlineData("十分钟后", "2026-09-21 00:05")]
    [InlineData("下周一上午九点", "2026-09-21 09:00")]
    [InlineData("2026-10-01 00:00", "2026-10-01 00:00")]
    public void Time_resolution_uses_fixed_reference(string text, string expected)
    {
        var result = AssistantDraftCompiler.ParseTime(text, new DateTimeOffset(2026, 9, 20, 23, 55, 0, TimeSpan.FromHours(8)), "Asia/Shanghai");
        Assert.NotNull(result);
        Assert.Equal(expected, $"{result.LocalDate:yyyy-MM-dd} {result.LocalTime:HH:mm}");
    }

    [Theory]
    [InlineData("睡前")]
    [InlineData("明天十二点")]
    [InlineData("明天25:00")]
    [InlineData("明天09:70")]
    [InlineData("2026-02-30 09:00")]
    public void Ambiguous_or_invalid_time_is_not_invented(string text) =>
        Assert.Null(AssistantDraftCompiler.ParseTime(text, DateTimeOffset.Now, "Asia/Shanghai"));


    [Fact]
    public async Task Daily_series_enters_delivery_on_each_day_and_can_be_deleted_by_confirmed_command()
    {
        var creator = Service();
        await Send(creator, "每天凌晨12点提醒我睡觉");
        var firstDue = DateTime.Today.AddDays(1).AddMinutes(1);
        Assert.Single(data.ClaimDueReminders(firstDue).Where(i => i.Title == "睡觉"));
        Assert.Empty(data.ClaimDueReminders(firstDue).Where(i => i.Title == "睡觉"));
        Assert.Single(data.ClaimDueReminders(firstDue.AddDays(1)).Where(i => i.Title == "睡觉"));
        var service = Service(new FixedInterpreter(new(3, "tasks", [new("delete_todo", "删除睡觉", Target: "睡觉")])));
        var plan = await Send(service, "删除睡觉");
        Assert.True(plan.Interaction!.CanConfirm);
        var deleted = await Click(service, plan.Interaction, "confirm");
        Assert.True(deleted.RefreshReminders);
        Assert.Empty(data.RecurringReminders());
        Assert.Empty(data.ClaimDueReminders(firstDue.AddDays(2)).Where(i => i.Title == "睡觉"));
    }

    [Fact]
    public async Task Recurring_series_respects_future_start_and_updates_the_scheduling_projection()
    {
        var future = DateTime.Today.AddDays(5).ToString("yyyy-MM-dd") + " 09:00";
        var service = Service(new FixedInterpreter(new(3, "tasks",
            [new("create_reminder", "五天后开始每天提醒", "测试周期", TimeText: future, RepeatText: "每天")])));
        await Send(service, "五天后开始每天提醒");
        Assert.Empty(data.ClaimDueReminders(DateTime.Today.AddDays(1).AddHours(10)));
        Assert.Single(data.ClaimDueReminders(DateTime.Today.AddDays(5).AddHours(10)));
        var mover = Service(new FixedInterpreter(new(3, "tasks",
            [new("reschedule_item", "调整测试周期", Target: "测试周期", TimeText: DateTime.Today.AddDays(6).ToString("yyyy-MM-dd") + " 10:30")])));
        var pending = await Send(mover, "调整测试周期");
        var result = await Click(mover, pending.Interaction!, "confirm");
        Assert.True(result.RefreshReminders);
        Assert.Equal(new TimeOnly(10, 30), Assert.Single(data.RecurringReminders()).ReminderTime);
    }

    [Fact]
    public async Task Changed_target_requires_new_confirmation_and_does_not_overwrite_user_edit()
    {
        var item = data.Save("项目评审", null, null, null);
        var service = Service(new FixedInterpreter(new(3, "tasks",
            [new("reschedule_item", "调整项目评审", Target: "项目评审", TimeText: "明天15:00")])));
        var pending = await Send(service, "调整项目评审");
        data.Save("项目评审", "用户已更新", DateTime.Now.AddDays(3), null, item.Id);
        var result = await Click(service, pending.Interaction!, "confirm");
        Assert.False(result.RefreshReminders);
        Assert.Equal("用户已更新", Assert.Single(data.Todos()).Notes);
    }

    [Fact]
    public async Task Mixed_query_and_write_preserves_target_step_mapping()
    {
        var item = data.Save("买牛奶", null, null, null);
        var service = Service(new FixedInterpreter(new(3, "tasks",
            [new("list_items", "查看今天的待办", TimeText: "今天"), new("complete_todo", "完成买牛奶", Target: "买牛奶")])));
        var result = await Send(service, "查看今天的待办并完成买牛奶");
        Assert.True(result.Interaction!.CanConfirm, System.Text.Json.JsonSerializer.Serialize(result));
        var completed = await Click(service, result.Interaction, "confirm");
        Assert.True(completed.RefreshReminders);
        Assert.True(data.Todos().Single(t => t.Id == item.Id).IsCompleted);
    }

    [Fact]
    public async Task View_model_keeps_quick_ask_session_and_restores_the_same_task_on_selection()
    {
        var service = Service();
        var viewModel = new ChronoIsle.App.ViewModels.LifeViewModel(data, service, new ProviderSettingsService(), null!,
            new ChronoIsle.App.Services.State.IslandStateCoordinator());
        await viewModel.SubmitQuickAskAsync("提醒我睡觉");
        var id = viewModel.SelectedSession!.Id;
        var request = viewModel.Interaction!.RequestId;
        var field = Assert.Single(viewModel.InteractionFields);
        field.Date = DateTime.Today.AddDays(1);
        await viewModel.SubmitInteractionCommand.ExecuteAsync(null);
        Assert.Equal(DateTime.Today.AddDays(1), field.Date);
        Assert.Equal(id, viewModel.SelectedSession.Id);
        viewModel.BeginQuickAskConversation();
        viewModel.SelectedSession = viewModel.Sessions.Single(s => s.Id == id);
        Assert.Equal(request, viewModel.Interaction!.RequestId);
        await viewModel.CancelInteractionCommand.ExecuteAsync(null);
        Assert.Equal(id, viewModel.SelectedSession.Id);
        Assert.Null(viewModel.Interaction);
    }

    [Theory]
    [InlineData("每天凌晨12点", "凌晨12点")]
    [InlineData(null, "每天凌晨12点")]
    public async Task Duplicate_model_reminder_time_is_normalized(string? time, string reminder)
    {
        var service = Service(new FixedInterpreter(new(3, "tasks",
            [new("create_reminder", "每天凌晨12点提醒睡觉", "睡觉", TimeText: time, ReminderText: reminder, RepeatText: "每天")])));
        var result = await Send(service, "每天凌晨12点提醒睡觉");
        Assert.Null(result.Interaction);
        Assert.Equal(TimeOnly.MinValue, Assert.Single(data.RecurringReminders()).ReminderTime);
    }

    [Fact]
    public async Task Unsupported_recurring_task_can_be_changed_to_single_without_a_clarification_loop()
    {
        var service = Service(new FixedInterpreter(new(3, "tasks",
            [new("create_recurring_task", "每月1号提醒", "购物", TimeText: "明天09:00", RepeatText: "每月1号")])));
        var result = await Send(service, "每月1号提醒");
        var card = result.Interaction!;
        var next = await Click(service, card, "submit", new() { ["0.repeatText"] = "不重复" });
        Assert.Null(next.Interaction);
        Assert.Single(data.ReminderItems());
    }

    [Fact]
    public void Reminder_with_time_after_verb_uses_language_understanding() =>
        Assert.Null(AssistantDraftInterpreter.TryLocal("提醒我凌晨12点45去洗澡"));

    [Fact]
    public async Task Event_end_clock_inherits_start_date()
    {
        var service = Service(new FixedInterpreter(new(3, "tasks",
            [new("create_event", "明天14:00到15:00安排项目评审", "项目评审", TimeText: "明天14:00", EndText: "15:00")])));
        var result = await Send(service, "明天14:00到15:00安排项目评审");
        Assert.False(result.IsFailure);
        Assert.Null(result.Interaction);
        Assert.True(result.RefreshReminders);
    }

    [Fact]
    public async Task Old_time_suggestion_can_be_selected_from_card_without_a_model_call()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            Kind = "time_suggestion_v1", Command = "create_reminder", Title = "喝水",
            Notes = (string?)null, Priority = (string?)null,
            Options = new[] { DateTime.Today.AddDays(1).AddHours(9), DateTime.Today.AddDays(1).AddHours(15), DateTime.Today.AddDays(1).AddHours(20) }
        });
        var service = Service();
        data.SaveAction(session.Id, "提醒喝水", json, "clarifying", null);
        var card = service.GetInteraction(session.Id)!;
        Assert.Equal(3, card.Fields[0].Options.Count);
        var result = await Click(service, card, "submit", new() { ["selection"] = "2" });
        Assert.True(result.RefreshReminders);
        Assert.Null(service.GetInteraction(session.Id));
        Assert.Equal("喝水", Assert.Single(data.ReminderItems()).Title);
        await Click(service, card, "submit", new() { ["selection"] = "2" });
        Assert.Single(data.ReminderItems());
    }

    [Fact]
    public async Task Invalid_clear_of_required_reminder_time_rolls_back_without_losing_schedule()
    {
        await Send(Service(), "每天上午九点提醒我喝水");
        var service = Service(new FixedInterpreter(new(3, "tasks",
            [new("update_todo", "清除喝水的提醒", Target: "喝水", ClearFields: ["remind"])])));
        var result = await Send(service, "清除喝水的提醒");
        Assert.True(result.Interaction!.CanConfirm, System.Text.Json.JsonSerializer.Serialize(result));
        var done = await Click(service, result.Interaction, "confirm");
        Assert.False(done.RefreshReminders);
        Assert.True(done.IsFailure);
        Assert.Single(data.RecurringReminders());
        Assert.Single(data.ClaimDueReminders(DateTime.Today.AddDays(1).AddHours(10)));
    }

    [Fact]
    public async Task General_chat_preserves_streaming_and_pending_task()
    {
        var service = new AssistantActionService(data, new ChinaStatutoryHolidayCalendar(),
            new ConversationRouter(), new LocalAgendaQueryService(data), new StreamingChat(),
            draftInterpreter: new FixedInterpreter(new(3, "chat", [], "你好")));
        var pending = await Send(service, "提醒我喝水");
        var chunks = new List<string>();
        var result = await service.HandleAsync(ProviderSettings.Default, session, [], "你好", chunks.Add);
        Assert.Equal(new[] { "你", "好" }, chunks);
        Assert.Equal(pending.Interaction!.RequestId, result.Interaction!.RequestId);
        Assert.Empty(data.ReminderItems());
    }

    sealed class StreamingChat : IChatCompletionClient
    {
        public async IAsyncEnumerable<string> StreamComplete(ProviderSettings provider, IEnumerable<ModelMessage> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.Yield(); yield return "你"; yield return "好"; }
        public Task<string> Complete(ProviderSettings provider, IEnumerable<ModelMessage> messages, bool jsonObject = false) => throw new NotSupportedException();
        public Task<string> Reply(ProviderSettings provider, IEnumerable<ChatMessage> history, string input) => throw new NotSupportedException();
        public Task Test(ProviderSettings provider) => throw new NotSupportedException();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(directory, true);
    }

    sealed class NeverInterpreter : IAssistantDraftInterpreter
    {
        public Task<AssistantUnderstanding> UnderstandAsync(ProviderSettings provider, string input, AssistantDraftTurn turn, IReadOnlyList<ChatMessage> history, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Local draft unexpectedly called model.");
    }
    sealed class FixedInterpreter(AssistantUnderstanding understanding) : IAssistantDraftInterpreter
    {
        public int Calls;
        public Task<AssistantUnderstanding> UnderstandAsync(ProviderSettings provider, string input, AssistantDraftTurn turn, IReadOnlyList<ChatMessage> history, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(understanding); }
    }
    sealed class BlockingInterpreter : IAssistantDraftInterpreter
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<AssistantUnderstanding> UnderstandAsync(ProviderSettings provider, string input, AssistantDraftTurn turn, IReadOnlyList<ChatMessage> history, CancellationToken cancellationToken)
        { Started.SetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); throw new InvalidOperationException(); }
    }
    sealed class NoChat : IChatCompletionClient
    {
        public Task<string> Complete(ProviderSettings provider, IEnumerable<ModelMessage> messages, bool jsonObject = false) => throw new NotSupportedException();
        public Task<string> Reply(ProviderSettings provider, IEnumerable<ChatMessage> history, string input) => throw new NotSupportedException();
        public Task Test(ProviderSettings provider) => throw new NotSupportedException();
    }
}

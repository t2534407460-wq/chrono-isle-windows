using System.Text.Json;
using ChronoIsle.App;
using ChronoIsle.App.Services;
using ChronoIsle.App.Services.Commanding;

namespace ChronoIsle.Tests;

public sealed class AssistantChatCommandBridgeTests : IDisposable
{
    readonly string path = Path.Combine(Path.GetTempPath(), $"chrono-isle-chat-bridge-{Guid.NewGuid():N}.db");

    [Fact]
    public void Strict_command_parser_rejects_model_local_ids()
    {
        var parsed = AssistantCommandIntentService.Parse("""
            {
              "schemaVersion":1,
              "command":"create_todo",
              "arguments":{"title":"写日报","notes":null,"due":null,"remind":null,"recurrence":null,"priority":null,"clientRequestId":"forged"},
              "missingFields":[],
              "ambiguityReasons":[]
            }
            """);

        Assert.False(parsed.IsValid);
        Assert.Contains("未创建任何事项", parsed.ErrorMessage);
    }

    [Fact]
    public void Chat_confirmation_bridge_confirms_the_pipeline_once()
    {
        var data = new LifeDataService(path);
        var pipeline = new AssistantCommandPipeline(path);
        var session = data.NewSession();
        var created = pipeline.SubmitParsed("创建清理桌面", new AssistantCommandEnvelope(
            AssistantCommandSchema.V1,
            AssistantCommandName.CreateTodo,
            new CreateTodoArgumentsV1("清理桌面", null, null, null, null, null),
            [], []));
        var pending = pipeline.SubmitParsed("完成清理桌面", new AssistantCommandEnvelope(
            AssistantCommandSchema.V1,
            AssistantCommandName.CompleteTodo,
            new CompleteTodoArgumentsV1(new AssistantTargetSelectorV1("清理桌面", AssistantItemKindV1.Todo, null)),
            [], []));
        var action = data.SaveAction(session.Id, "完成清理桌面", JsonSerializer.Serialize(new
        {
            Kind = "pipeline_confirmation_v1",
            ConfirmationId = pending.ConfirmationId,
            Command = "complete_todo"
        }), "awaiting_confirmation");
        var chat = new OpenAiChatService();
        var service = new AssistantActionService(
            data,
            new ChinaStatutoryHolidayCalendar(),
            new ConversationRouter(),
            new LocalAgendaQueryService(data),
            chat,
            pipeline: pipeline);

        var first = service.Confirm(action.Id);
        var second = service.Confirm(action.Id);

        Assert.Equal(AssistantCommandPipelineState.Succeeded, created.State);
        Assert.Equal(AssistantCommandPipelineState.AwaitingConfirmation, pending.State);
        Assert.True(first.Succeeded);
        Assert.False(second.Succeeded);
        Assert.True(Assert.Single(data.Todos()).IsCompleted);
        Assert.Equal("confirmed", data.Action(action.Id)!.Status);
    }

    [Fact]
    public async Task Command_parser_uses_an_injected_chat_client()
    {
        var parser = new AssistantCommandIntentService(new FakeChatCompletionClient(
            """{"schemaVersion":1,"command":"create_todo","arguments":{"title":"buy milk"},"missingFields":[],"ambiguityReasons":[]}"""));

        var result = await parser.AnalyzeAsync(ProviderSettings.Default, [], "add todo buy milk");

        Assert.True(result.IsValid);
        Assert.Equal(AssistantCommandName.CreateTodo, result.Envelope!.Command);
    }

    [Fact]
    public async Task Explicit_half_hour_reminder_uses_local_command_when_model_output_is_invalid()
    {
        var data = new LifeDataService(path);
        var session = data.NewSession();
        var service = new AssistantActionService(
            data,
            new ChinaStatutoryHolidayCalendar(),
            new ConversationRouter(),
            new LocalAgendaQueryService(data),
            new FakeChatCompletionClient("not valid command json"));

        var result = await service.HandleAsync(
            ProviderSettings.Default,
            session,
            [],
            "12点半提醒我去洗澡");

        var reminder = Assert.Single(data.ReminderItems());
        Assert.False(result.IsFailure);
        Assert.Equal("去洗澡", reminder.Title);
        Assert.Equal(30, reminder.RemindAt!.Value.Minute);
        Assert.Contains("已设置提醒：去洗澡", result.Reply);
        Assert.DoesNotContain("create_reminder", result.Reply);
    }
    [Fact]
    public async Task Explicit_prefix_reminder_accepts_midnight_clock()
    {
        var data = new LifeDataService(path);
        var session = data.NewSession();
        var service = new AssistantActionService(
            data,
            new ChinaStatutoryHolidayCalendar(),
            new ConversationRouter(),
            new LocalAgendaQueryService(data),
            new FakeChatCompletionClient("not valid command json"));

        var result = await service.HandleAsync(
            ProviderSettings.Default,
            session,
            [],
            "提醒我凌晨12点45去洗澡");

        var reminder = Assert.Single(data.ReminderItems());
        Assert.False(result.IsFailure);
        Assert.Equal("去洗澡", reminder.Title);
        Assert.Equal(new TimeOnly(0, 45), TimeOnly.FromDateTime(reminder.RemindAt!.Value));
    }

    [Fact]
    public async Task Explicit_chinese_afternoon_reminder_does_not_depend_on_model_schema()
    {
        var data = new LifeDataService(path);
        var session = data.NewSession();
        var service = new AssistantActionService(
            data,
            new ChinaStatutoryHolidayCalendar(),
            new ConversationRouter(),
            new LocalAgendaQueryService(data),
            new FakeChatCompletionClient("not valid command json"));

        var result = await service.HandleAsync(
            ProviderSettings.Default,
            session,
            [],
            "下午四点提醒我腌鸡胸肉");

        var reminder = Assert.Single(data.ReminderItems());
        Assert.False(result.IsFailure);
        Assert.Equal("腌鸡胸肉", reminder.Title);
        Assert.Equal(
            new TimeOnly(16, 0),
            TimeOnly.FromDateTime(reminder.RemindAt!.Value));
        Assert.DoesNotContain("schema_rejected", result.Reply);
    }

    sealed class FakeChatCompletionClient(string response) : IChatCompletionClient
    {
        public Task<string> Reply(ProviderSettings provider, IEnumerable<ChatMessage> history, string input) => Task.FromResult(response);
        public Task<string> Complete(ProviderSettings provider, IEnumerable<ModelMessage> messages, bool jsonObject = false) => Task.FromResult(response);
        public Task Test(ProviderSettings provider) => Task.CompletedTask;
    }
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(path)) File.Delete(path);
    }
}

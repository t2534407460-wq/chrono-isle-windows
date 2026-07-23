using System.Text.Json;
using OpenIsland.App;
using OpenIsland.App.Services;
using OpenIsland.App.Services.Commanding;

namespace OpenIsland.Tests;

public sealed class AssistantChatCommandBridgeTests : IDisposable
{
    readonly string path = Path.Combine(Path.GetTempPath(), $"open-island-chat-bridge-{Guid.NewGuid():N}.db");

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

using ChronoIsle.App;
using ChronoIsle.App.Services;
using ChronoIsle.App.Services.Commanding;

namespace ChronoIsle.Tests;

public sealed class AssistantRuntimeV2RegressionTests : IDisposable
{
    readonly string directory = Path.Combine(
        Path.GetTempPath(), "chrono-isle-runtime-v2-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Runtime_service_has_no_v1_command_parser_dependency()
    {
        var serviceType = typeof(AssistantActionService);

        Assert.DoesNotContain(
            serviceType.GetConstructors().SelectMany(constructor => constructor.GetParameters()),
            parameter => parameter.ParameterType == typeof(AssistantCommandIntentService));
        Assert.DoesNotContain(
            serviceType.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
            field => field.FieldType == typeof(AssistantCommandIntentService));
    }

    [Fact]
    public async Task Relative_reminder_uses_v2_pipeline_without_schema_rejection()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "life.db");
        var data = new LifeDataService(path);
        var session = data.NewSession();
        const string input = "帮我设置 10 分钟后的洗澡提醒";
        var plan = new AssistantConversationPlanV2(2,
        [
            new("s1", ConversationSegmentKindV2.Command, ConversationOperationV2.CreateReminder, input, [])
        ]);
        var reminder = new AssistantCommandEnvelope(
            AssistantCommandSchema.V1,
            AssistantCommandName.CreateReminder,
            new CreateReminderArgumentsV1(
                "洗澡",
                null,
                new AssistantTimeExpressionV1(null, null, "10 分钟后", null, "10 分钟后"),
                null,
                null,
                null),
            [],
            []);
        var commandPipeline = new AssistantCommandPipeline(path);
        var service = new AssistantActionService(
            data,
            new ChinaStatutoryHolidayCalendar(),
            new ConversationRouter(),
            new LocalAgendaQueryService(data),
            new NoOpChatClient(),
            pipeline: commandPipeline,
            planner: new FixedPlanner(plan),
            argumentParser: new FixedArgumentParser(reminder),
            assistantPlanPipeline: new AssistantPlanPipeline(path, commandPipeline));

        var result = await service.HandleAsync(ProviderSettings.Default, session, [], input);

        Assert.False(result.IsFailure);
        Assert.DoesNotContain("schema_rejected", result.Reply, StringComparison.Ordinal);
        Assert.Equal("洗澡", Assert.Single(data.ReminderItems()).Title);
    }

    [Fact]
    public async Task Explicit_relative_reminder_bypasses_model_json()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "local-relative.db");
        var data = new LifeDataService(path);
        var session = data.NewSession();
        var commandPipeline = new AssistantCommandPipeline(path);
        var service = new AssistantActionService(
            data,
            new ChinaStatutoryHolidayCalendar(),
            new ConversationRouter(),
            new LocalAgendaQueryService(data),
            new NoOpChatClient(),
            pipeline: commandPipeline,
            planner: new ThrowingPlanner(),
            argumentParser: new ThrowingArgumentParser(),
            assistantPlanPipeline: new AssistantPlanPipeline(path, commandPipeline));

        var result = await service.HandleAsync(
            ProviderSettings.Default,
            session,
            [],
            "帮我设置 10 分钟后的洗澡提醒");

        Assert.False(result.IsFailure);
        Assert.DoesNotContain("schema_rejected", result.Reply, StringComparison.Ordinal);
        Assert.Equal("洗澡", Assert.Single(data.ReminderItems()).Title);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (!Directory.Exists(directory)) return;
        try { Directory.Delete(directory, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    sealed class ThrowingPlanner : IConversationPlanner
    {
        public Task<ConversationPlanResultV2> PlanAsync(
            ProviderSettings provider,
            AssistantTurnContextV2 context,
            string input,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("模型规划器不应被调用。");
    }

    sealed class ThrowingArgumentParser : IOperationArgumentParser
    {
        public Task<AssistantCommandParseResult> ParseAsync(
            ProviderSettings provider,
            ConversationSegmentV2 segment,
            AssistantTurnContextV2 context,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("模型参数解析器不应被调用。");
    }

    sealed class FixedPlanner(AssistantConversationPlanV2 plan) : IConversationPlanner
    {
        public Task<ConversationPlanResultV2> PlanAsync(
            ProviderSettings provider,
            AssistantTurnContextV2 context,
            string input,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConversationPlanResultV2(plan, null, null, false));
    }

    sealed class FixedArgumentParser(AssistantCommandEnvelope envelope) : IOperationArgumentParser
    {
        public Task<AssistantCommandParseResult> ParseAsync(
            ProviderSettings provider,
            ConversationSegmentV2 segment,
            AssistantTurnContextV2 context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AssistantCommandParseResult(envelope, "{}", null));
    }

    sealed class NoOpChatClient : IChatCompletionClient
    {
        public Task<string> Reply(
            ProviderSettings provider,
            IEnumerable<ChatMessage> history,
            string input) => throw new NotSupportedException();

        public Task<string> Complete(
            ProviderSettings provider,
            IEnumerable<ModelMessage> messages,
            bool jsonObject = false) => throw new NotSupportedException();

        public Task Test(ProviderSettings provider) => throw new NotSupportedException();
    }
}

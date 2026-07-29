using ChronoIsle.App;
using ChronoIsle.App.Services;
using ChronoIsle.App.Services.Commanding;

namespace ChronoIsle.Tests;

public sealed class AssistantActionServiceV2Tests : IDisposable
{
    readonly string directory = Path.Combine(
        Path.GetTempPath(), "chrono-isle-action-v2-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Mixed_turn_combines_chat_query_then_local_write_result()
    {
        var (data, session, path) = CreateData();
        var day = DateTime.Today.AddHours(10);
        data.SaveEvent("产品会", null, day, day.AddHours(1), day);
        const string input = "你好，今天有安排吗，提醒我明天交报告";
        var plan = new AssistantConversationPlanV2(2,
        [
            new("s1", ConversationSegmentKindV2.Chat, ConversationOperationV2.None, "你好", []),
            new("s2", ConversationSegmentKindV2.Query, ConversationOperationV2.ListItems, "今天有安排吗", []),
            new("s3", ConversationSegmentKindV2.Command, ConversationOperationV2.CreateReminder,
                "提醒我明天交报告", [])
        ]);
        var reminder = new AssistantCommandEnvelope(
            AssistantCommandSchema.V1,
            AssistantCommandName.CreateReminder,
            new CreateReminderArgumentsV1(
                "交报告",
                null,
                new AssistantTimeExpressionV1(
                    DateOnly.FromDateTime(DateTime.Today.AddDays(1)),
                    new TimeOnly(9, 0),
                    null,
                    null,
                    "明天"),
                null,
                null,
                null),
            [],
            []);
        var service = CreateService(
            data,
            path,
            new FixedPlanner(plan),
            new FixedArgumentParser(new Dictionary<ConversationOperationV2, AssistantCommandEnvelope>
            {
                [ConversationOperationV2.CreateReminder] = reminder
            }));

        var result = await service.HandleAsync(ProviderSettings.Default, session, [], input);

        Assert.False(result.IsFailure);
        Assert.Null(result.PendingAction);
        Assert.Single(data.ReminderItems().Where(item => item.Title == "交报告"));
        Assert.True(result.Reply.IndexOf("聊天回应", StringComparison.Ordinal) <
                    result.Reply.IndexOf("产品会", StringComparison.Ordinal));
        Assert.True(result.Reply.IndexOf("产品会", StringComparison.Ordinal) <
                    result.Reply.IndexOf("已设置提醒：交报告", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Multiple_creates_produce_one_pending_plan_and_confirm_all()
    {
        var (data, session, path) = CreateData();
        const string input = "新增甲任务，再新增乙任务";
        var plan = new AssistantConversationPlanV2(2,
        [
            new("s1", ConversationSegmentKindV2.Command, ConversationOperationV2.CreateTodo, "新增甲任务", []),
            new("s2", ConversationSegmentKindV2.Command, ConversationOperationV2.CreateTodo, "新增乙任务", [])
        ]);
        var parser = new EvidenceArgumentParser(new Dictionary<string, AssistantCommandEnvelope>
        {
            ["新增甲任务"] = CreateTodo("甲任务"),
            ["新增乙任务"] = CreateTodo("乙任务")
        });
        var service = CreateService(data, path, new FixedPlanner(plan), parser);

        var result = await service.HandleAsync(ProviderSettings.Default, session, [], input);

        var pending = Assert.IsType<AssistantAction>(result.PendingAction);
        var pendingPlan = Assert.IsType<AssistantPendingPlan>(result.PendingPlan);
        Assert.Equal(2, pendingPlan.Steps.Count);
        Assert.Empty(data.Todos());
        Assert.Contains("整体执行", result.Reply);

        var confirmed = await service.HandleAsync(ProviderSettings.Default, session, [], "确认全部");

        Assert.False(confirmed.IsFailure);
        Assert.Equal(["甲任务", "乙任务"], data.Todos().Select(todo => todo.Title).Order().ToArray());
    }

    [Fact]
    public async Task Missing_time_is_saved_as_turn_context_and_completed_on_the_next_turn()
    {
        var (data, session, path) = CreateData();
        var plan = new AssistantConversationPlanV2(2,
        [
            new("s1", ConversationSegmentKindV2.Command, ConversationOperationV2.CreateReminder,
                "提醒我交报告", [])
        ]);
        var incomplete = new AssistantCommandEnvelope(
            AssistantCommandSchema.V1,
            AssistantCommandName.CreateReminder,
            new CreateReminderArgumentsV1("交报告", null, null, null, null, null),
            ["remind"],
            []);
        var complete = new AssistantCommandEnvelope(
            AssistantCommandSchema.V1,
            AssistantCommandName.CreateReminder,
            new CreateReminderArgumentsV1(
                "交报告", null,
                new AssistantTimeExpressionV1(
                    DateOnly.FromDateTime(DateTime.Today.AddDays(1)),
                    new TimeOnly(9, 0), null, null, "明天九点"),
                null, null, null),
            [],
            []);
        var service = CreateService(
            data,
            path,
            new FixedPlanner(plan),
            new SequenceArgumentParser(incomplete, complete));

        var first = await service.HandleAsync(
            ProviderSettings.Default, session, [], "提醒我交报告");

        Assert.Equal("clarifying", first.PendingAction?.Status);
        Assert.Empty(data.ReminderItems());

        var second = await service.HandleAsync(
            ProviderSettings.Default, session, [], "明天九点");

        Assert.False(second.IsFailure);
        Assert.Null(second.PendingAction);
        Assert.Equal("交报告", Assert.Single(data.ReminderItems()).Title);
    }

    [Fact]
    public async Task Long_term_query_lists_unscheduled_items_without_guessing_a_date()
    {
        var (data, session, path) = CreateData();
        new AssistantCommandPipeline(path).SubmitParsed(
            "创建长期事项学习外语",
            new AssistantCommandEnvelope(
                AssistantCommandSchema.V1,
                AssistantCommandName.CreateLongTermItem,
                new CreateLongTermItemArgumentsV1("学习外语", null, null, null, null),
                [],
                []));
        var plan = new AssistantConversationPlanV2(2,
        [
            new("s1", ConversationSegmentKindV2.Query, ConversationOperationV2.ListItems,
                "查询长期事项", [])
        ]);
        var service = CreateService(
            data,
            path,
            new FixedPlanner(plan),
            new FixedArgumentParser(new Dictionary<ConversationOperationV2, AssistantCommandEnvelope>()));

        var result = await service.HandleAsync(
            ProviderSettings.Default, session, [], "查询长期事项");

        Assert.Contains("学习外语", result.Reply, StringComparison.Ordinal);
        Assert.DoesNotContain("补充日期", result.Reply, StringComparison.Ordinal);
    }
    static AssistantCommandEnvelope CreateTodo(string title) => new(
        AssistantCommandSchema.V1,
        AssistantCommandName.CreateTodo,
        new CreateTodoArgumentsV1(title, null, null, null, null, null),
        [],
        []);

    (LifeDataService Data, ChatSession Session, string Path) CreateData()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".db");
        var data = new LifeDataService(path);
        return (data, data.NewSession(), path);
    }

    static AssistantActionService CreateService(
        LifeDataService data,
        string path,
        IConversationPlanner planner,
        IOperationArgumentParser parser)
    {
        var commandPipeline = new AssistantCommandPipeline(path);
        var planPipeline = new AssistantPlanPipeline(path, commandPipeline);
        return new AssistantActionService(
            data,
            new ChinaStatutoryHolidayCalendar(),
            new ConversationRouter(),
            new LocalAgendaQueryService(data),
            new SummaryChatClient(),
            pipeline: commandPipeline,
            planner: planner,
            argumentParser: parser,
            assistantPlanPipeline: planPipeline);
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

    sealed class FixedArgumentParser(
        IReadOnlyDictionary<ConversationOperationV2, AssistantCommandEnvelope> values)
        : IOperationArgumentParser
    {
        public Task<AssistantCommandParseResult> ParseAsync(
            ProviderSettings provider,
            ConversationSegmentV2 segment,
            AssistantTurnContextV2 context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AssistantCommandParseResult(values[segment.Operation], "{}", null));
    }

    sealed class EvidenceArgumentParser(
        IReadOnlyDictionary<string, AssistantCommandEnvelope> values)
        : IOperationArgumentParser
    {
        public Task<AssistantCommandParseResult> ParseAsync(
            ProviderSettings provider,
            ConversationSegmentV2 segment,
            AssistantTurnContextV2 context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AssistantCommandParseResult(values[segment.Evidence], "{}", null));
    }

    sealed class SequenceArgumentParser(params AssistantCommandEnvelope[] values)
        : IOperationArgumentParser
    {
        readonly Queue<AssistantCommandEnvelope> queue = new(values);

        public Task<AssistantCommandParseResult> ParseAsync(
            ProviderSettings provider,
            ConversationSegmentV2 segment,
            AssistantTurnContextV2 context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AssistantCommandParseResult(queue.Dequeue(), "{}", null));
    }
    sealed class SummaryChatClient : IChatCompletionClient
    {
        public Task<string> Reply(
            ProviderSettings provider,
            IEnumerable<ChatMessage> history,
            string input) => Task.FromResult("聊天回应");

        public Task<string> Complete(
            ProviderSettings provider,
            IEnumerable<ModelMessage> messages,
            bool jsonObject = false)
        {
            var values = messages.ToArray();
            return Task.FromResult(values.Any(message => message.Content.Contains("LOCAL_DATA:", StringComparison.Ordinal))
                ? "查询概括"
                : "聊天回应");
        }

        public Task Test(ProviderSettings provider) => Task.CompletedTask;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (!Directory.Exists(directory)) return;
        try { Directory.Delete(directory, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

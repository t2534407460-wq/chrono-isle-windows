using ChronoIsle.App;
using ChronoIsle.App.Services;
using ChronoIsle.App.Services.Commanding;

namespace ChronoIsle.Tests;

public sealed class AssistantConversationPlanningV2Tests
{
    [Fact]
    public void PlanParser_AcceptsMixedIntentsAndOrdersDependencies()
    {
        const string input = "你好，查今天安排，把会议改到三点";
        const string json = """
            {
              "schemaVersion": 2,
              "segments": [
                {"segmentRef":"s1","kind":"chat","operation":"none","evidence":"你好","dependsOn":[]},
                {"segmentRef":"s2","kind":"query","operation":"list_items","evidence":"查今天安排","dependsOn":[]},
                {"segmentRef":"s3","kind":"command","operation":"reschedule_item","evidence":"把会议改到三点","dependsOn":["s2"]}
              ]
            }
            """;

        Assert.True(ConversationPlanJsonV2.TryDeserialize(json, input, out var plan, out _, out _));
        Assert.Equal(["s1", "s2", "s3"], plan!.Segments.Select(segment => segment.SegmentRef));
        Assert.Equal(ConversationOperationV2.RescheduleItem, plan.Segments[2].Operation);
    }

    [Fact]
    public void PlanParser_RejectsInventedEvidenceAndCycles()
    {
        const string input = "查今天安排";
        const string invented = """
            {"schemaVersion":2,"segments":[
              {"segmentRef":"s1","kind":"command","operation":"delete_todo","evidence":"删除全部事项","dependsOn":[]}
            ]}
            """;
        Assert.False(ConversationPlanJsonV2.TryDeserialize(invented, input, out _, out var code, out _));
        Assert.Equal("evidence_mismatch", code);

        const string cycle = """
            {"schemaVersion":2,"segments":[
              {"segmentRef":"s1","kind":"query","operation":"list_items","evidence":"查","dependsOn":["s2"]},
              {"segmentRef":"s2","kind":"query","operation":"summarize_period","evidence":"今天安排","dependsOn":["s1"]}
            ]}
            """;
        Assert.False(ConversationPlanJsonV2.TryDeserialize(cycle, input, out _, out code, out _));
        Assert.Equal("route_invalid", code);
    }

    [Fact]
    public void PlanParser_ToleratesCodeFenceCaseAndTrailingComma()
    {
        const string response = """
            ```json
            {"SchemaVersion":2,"Segments":[
              {"SegmentRef":"s1","Kind":"CHAT","Operation":"NONE","Evidence":"你好","DependsOn":[]},
            ]}
            ```
            """;

        Assert.True(ConversationPlanJsonV2.TryDeserialize(response, "你好", out var plan, out _, out _));
        Assert.Equal(ConversationSegmentKindV2.Chat, plan!.Segments.Single().Kind);
    }

    [Fact]
    public void PlanParser_RejectsDuplicateAndExtraFields()
    {
        const string duplicate = """
            {"schemaVersion":2,"schemaVersion":2,"segments":[
              {"segmentRef":"s1","kind":"chat","operation":"none","evidence":"你好","dependsOn":[]}
            ]}
            """;
        Assert.False(ConversationPlanJsonV2.TryDeserialize(duplicate, "你好", out _, out var code, out _));
        Assert.Equal("route_invalid", code);

        const string extra = """
            {"schemaVersion":2,"confidence":1,"segments":[
              {"segmentRef":"s1","kind":"chat","operation":"none","evidence":"你好","dependsOn":[]}
            ]}
            """;
        Assert.False(ConversationPlanJsonV2.TryDeserialize(extra, "你好", out _, out code, out _));
        Assert.Equal("route_invalid", code);
    }

    [Fact]
    public async Task Planner_RepairsOnlyOnce()
    {
        var chat = new QueueChatClient(
            """{"schemaVersion":2,"segments":[]}""",
            """{"schemaVersion":2,"segments":[{"segmentRef":"s1","kind":"chat","operation":"none","evidence":"你好","dependsOn":[]}]}""");
        var planner = new ConversationPlannerV2(chat);

        var result = await planner.PlanAsync(ProviderSettings.Default with { ApiKey = "test" }, AssistantTurnContextV2.Empty, "你好");

        Assert.True(result.IsValid);
        Assert.True(result.Repaired);
        Assert.Equal(2, chat.CallCount);
    }

    [Fact]
    public void OperationParser_NormalizesArgumentsAndDropsExtraFields()
    {
        const string response = """
            ```json
            {
              "arguments": {
                "Title": "洗澡",
                "Remind": {
                  "LocalDate": "2026-07-30",
                  "LocalTime": "00:30:00",
                  "OriginalText": "明天十二点半",
                  "invented": "ignored"
                },
                "Priority": "NORMAL",
                "databaseId": "must-not-pass"
              },
              "missingFields": null,
              "ambiguityReasons": []
            }
            ```
            """;

        Assert.True(OperationArgumentParserV2.TryNormalizeAndParse(
            response,
            ConversationOperationV2.CreateReminder,
            out var envelope,
            out var error), error);
        var arguments = Assert.IsType<CreateReminderArgumentsV1>(envelope!.Arguments);
        Assert.Equal("洗澡", arguments.Title);
        Assert.Equal(AssistantPriorityV1.Normal, arguments.Priority);
        Assert.Equal(new TimeOnly(0, 30), arguments.Remind!.LocalTime);
    }

    [Fact]
    public void OperationParser_NormalizesScalarRelativeReminder()
    {
        const string response = """
            {
              "arguments": {
                "title": "测试",
                "remind": "十分钟后"
              },
              "missingFields": [],
              "ambiguityReasons": []
            }
            """;

        Assert.True(OperationArgumentParserV2.TryNormalizeAndParse(
            response,
            ConversationOperationV2.CreateReminder,
            out var envelope,
            out var error), error);
        var arguments = Assert.IsType<CreateReminderArgumentsV1>(envelope!.Arguments);
        Assert.Equal("十分钟后", arguments.Remind!.RelativeExpression);
        Assert.Equal("十分钟后", arguments.Remind.OriginalText);
    }

    [Fact]
    public async Task OperationParser_PromptRequiresCompleteRelativeTimeObject()
    {
        var chat = new QueueChatClient("""
            {
              "arguments": {
                "title": "测试",
                "remind": {
                  "relativeExpression": "十分钟后",
                  "originalText": "十分钟后"
                }
              },
              "missingFields": [],
              "ambiguityReasons": []
            }
            """);
        var parser = new OperationArgumentParserV2(chat);
        var segment = new ConversationSegmentV2(
            "s1",
            ConversationSegmentKindV2.Command,
            ConversationOperationV2.CreateReminder,
            "十分钟后提醒我测试",
            []);

        var result = await parser.ParseAsync(
            ProviderSettings.Default with { ApiKey = "test" },
            segment,
            AssistantTurnContextV2.Empty);

        Assert.True(result.IsValid, result.ErrorMessage);
        var prompt = chat.LastMessages.First(message => message.Role == "system").Content;
        Assert.Contains(
            """
            "remind":{"localDate":null,"localTime":null,"relativeExpression":null,"timeZoneHint":null,"originalText":null}
            """,
            prompt,
            StringComparison.Ordinal);
        Assert.Contains("Do not return a time as a JSON string.", prompt, StringComparison.Ordinal);
    }

    sealed class QueueChatClient(params string[] responses) : IChatCompletionClient
    {
        readonly Queue<string> queue = new(responses);
        public int CallCount { get; private set; }
        public IReadOnlyList<ModelMessage> LastMessages { get; private set; } = [];

        public Task<string> Reply(ProviderSettings provider, IEnumerable<ChatMessage> history, string input) =>
            throw new NotSupportedException();

        public Task<string> Complete(ProviderSettings provider, IEnumerable<ModelMessage> messages, bool jsonObject = false)
        {
            CallCount++;
            LastMessages = messages.ToArray();
            return Task.FromResult(queue.Dequeue());
        }

        public Task Test(ProviderSettings provider) => throw new NotSupportedException();
    }
}

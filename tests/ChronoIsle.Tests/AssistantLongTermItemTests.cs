using ChronoIsle.App.Services.Commanding;
using Microsoft.Data.Sqlite;

namespace ChronoIsle.Tests;

public sealed class AssistantLongTermItemTests : IDisposable
{
    readonly string directory = Path.Combine(
        Path.GetTempPath(), "chrono-isle-long-term-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void V2_contract_accepts_long_term_creation_as_a_distinct_item_kind()
    {
        const string input = "创建长期事项学习英语";
        const string planJson = """
            {"schemaVersion":2,"segments":[{
              "segmentRef":"s1","kind":"command","operation":"create_long_term_item",
              "evidence":"创建长期事项学习英语","dependsOn":[]
            }]}
            """;
        const string argumentsJson = """
            {"arguments":{
              "title":"学习英语","notes":null,"due":null,"remind":null,"priority":"normal"
            },"missingFields":[],"ambiguityReasons":[]}
            """;

        Assert.True(ConversationPlanJsonV2.TryDeserialize(
            planJson, input, out var plan, out _, out _));
        Assert.Equal(
            ConversationOperationV2.CreateLongTermItem,
            Assert.Single(plan!.Segments).Operation);
        Assert.True(OperationArgumentParserV2.TryNormalizeAndParse(
            argumentsJson,
            ConversationOperationV2.CreateLongTermItem,
            out var envelope,
            out var error),
            error);
        Assert.Equal(AssistantCommandName.CreateLongTermItem, envelope!.Command);
        Assert.Equal(
            "学习英语",
            Assert.IsType<CreateLongTermItemArgumentsV1>(envelope.Arguments).Title);
    }

    [Fact]
    public void Long_term_item_can_be_created_updated_rescheduled_completed_and_deleted()
    {
        var (path, pipeline) = CreatePipeline();
        var single = new AssistantCommandPipeline(path);
        var created = single.SubmitParsed(
            "创建长期事项学习英语",
            CreateLongTerm("学习英语"));
        var id = Assert.Single(created.ItemIds);

        var candidate = Assert.Single(pipeline.FindCandidateBindings("修改学习英语", "s1"));
        Assert.Equal(AssistantItemKindV1.LongTerm, candidate.Kind);
        var target = Target(candidate);

        Confirm(pipeline, "修改学习英语", new AssistantCommandEnvelope(
            AssistantCommandSchema.V1,
            AssistantCommandName.UpdateTodo,
            new UpdateTodoArgumentsV1(
                target,
                new UpdateTodoChangesV1("长期学习英语", "每天至少半小时", null, null, null, null)),
            [],
            []), candidate);

        var renamed = Assert.Single(pipeline.FindCandidateBindings("改期长期学习英语", "s2"));
        Confirm(pipeline, "改期长期学习英语", new AssistantCommandEnvelope(
            AssistantCommandSchema.V1,
            AssistantCommandName.RescheduleItem,
            new RescheduleItemArgumentsV1(Target(renamed), At(2030, 1, 2, 9), null),
            [],
            []), renamed);

        var rescheduled = Assert.Single(pipeline.FindCandidateBindings("完成长期学习英语", "s3"));
        Confirm(pipeline, "完成长期学习英语", new AssistantCommandEnvelope(
            AssistantCommandSchema.V1,
            AssistantCommandName.CompleteTodo,
            new CompleteTodoArgumentsV1(Target(rescheduled)),
            [],
            []), rescheduled);

        Assert.Equal("LongTerm", TextScalar(path, "SELECT item_type FROM life_items WHERE id=$id", id));
        Assert.Equal("Completed", TextScalar(path, "SELECT status FROM life_items WHERE id=$id", id));
        Assert.StartsWith(
            "2030-01-02T09:00:00",
            TextScalar(path, "SELECT due_local_datetime FROM life_items WHERE id=$id", id),
            StringComparison.Ordinal);

        var second = single.SubmitParsed("创建长期事项阅读计划", CreateLongTerm("阅读计划"));
        var secondId = Assert.Single(second.ItemIds);
        var toDelete = Assert.Single(pipeline.FindCandidateBindings("删除阅读计划", "s4"));
        Confirm(pipeline, "删除阅读计划", new AssistantCommandEnvelope(
            AssistantCommandSchema.V1,
            AssistantCommandName.DeleteTodo,
            new DeleteTodoArgumentsV1(Target(toDelete)),
            [],
            []), toDelete);
        Assert.NotNull(TextScalar(path, "SELECT deleted_at FROM life_items WHERE id=$id", secondId));
    }

    [Theory]
    [InlineData("Reminder", "提醒")]
    [InlineData("Event", "事件")]
    public void Existing_reminders_and_events_support_update_complete_and_delete(
        string kind,
        string titlePrefix)
    {
        var (path, pipeline) = CreatePipeline();
        var single = new AssistantCommandPipeline(path);
        var firstTitle = titlePrefix + "甲";
        var first = single.SubmitParsed("创建" + firstTitle, Create(kind, firstTitle));
        var firstId = Assert.Single(first.ItemIds);
        var candidate = Assert.Single(pipeline.FindCandidateBindings("修改" + firstTitle, "s1"));

        Confirm(pipeline, "修改" + firstTitle, new AssistantCommandEnvelope(
            AssistantCommandSchema.V1,
            AssistantCommandName.UpdateTodo,
            new UpdateTodoArgumentsV1(
                Target(candidate),
                new UpdateTodoChangesV1(firstTitle + "已修改", "新说明", null, null, null, null)),
            [],
            []), candidate);

        var renamed = Assert.Single(pipeline.FindCandidateBindings("完成" + firstTitle + "已修改", "s2"));
        Confirm(pipeline, "完成" + firstTitle + "已修改", new AssistantCommandEnvelope(
            AssistantCommandSchema.V1,
            AssistantCommandName.CompleteTodo,
            new CompleteTodoArgumentsV1(Target(renamed)),
            [],
            []), renamed);
        Assert.Equal("Completed", TextScalar(path, "SELECT status FROM life_items WHERE id=$id", firstId));

        var secondTitle = titlePrefix + "乙";
        var second = single.SubmitParsed("创建" + secondTitle, Create(kind, secondTitle));
        var secondId = Assert.Single(second.ItemIds);
        var toDelete = Assert.Single(pipeline.FindCandidateBindings("删除" + secondTitle, "s3"));
        Confirm(pipeline, "删除" + secondTitle, new AssistantCommandEnvelope(
            AssistantCommandSchema.V1,
            AssistantCommandName.DeleteTodo,
            new DeleteTodoArgumentsV1(Target(toDelete)),
            [],
            []), toDelete);
        Assert.NotNull(TextScalar(path, "SELECT deleted_at FROM life_items WHERE id=$id", secondId));
    }

    static AssistantCommandEnvelope CreateLongTerm(string title) => new(
        AssistantCommandSchema.V1,
        AssistantCommandName.CreateLongTermItem,
        new CreateLongTermItemArgumentsV1(title, null, null, null, null),
        [],
        []);

    static AssistantCommandEnvelope Create(string kind, string title) =>
        kind == "Reminder"
            ? new(
                AssistantCommandSchema.V1,
                AssistantCommandName.CreateReminder,
                new CreateReminderArgumentsV1(title, null, At(2030, 1, 3, 9), null, null, null),
                [],
                [])
            : new(
                AssistantCommandSchema.V1,
                AssistantCommandName.CreateEvent,
                new CreateEventArgumentsV1(title, null, At(2030, 1, 3, 9), At(2030, 1, 3, 10), null),
                [],
                []);

    static AssistantTargetSelectorV1 Target(AssistantPlanCandidateBindingV2 candidate) =>
        new(candidate.Title, candidate.Kind, null, candidate.CandidateRef);

    static void Confirm(
        AssistantPlanPipeline pipeline,
        string source,
        AssistantCommandEnvelope command,
        AssistantPlanCandidateBindingV2 candidate)
    {
        var pending = pipeline.SubmitPlan(source, [command], [candidate]);
        Assert.Equal(AssistantPlanPipelineState.AwaitingConfirmation, pending.State);
        Assert.Equal(AssistantPlanPipelineState.Succeeded, pipeline.ConfirmPlan(pending.ConfirmationId!).State);
    }

    static AssistantTimeExpressionV1 At(int year, int month, int day, int hour) => new(
        new DateOnly(year, month, day),
        new TimeOnly(hour, 0),
        null,
        "Etc/UTC",
        $"{year:D4}-{month:D2}-{day:D2} {hour:D2}:00");

    (string Path, AssistantPlanPipeline Pipeline) CreatePipeline()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".db");
        return (path, new AssistantPlanPipeline(
            path,
            utcNow: () => new DateTimeOffset(2026, 7, 29, 1, 0, 0, TimeSpan.Zero)));
    }

    static string? TextScalar(string path, string sql, string id)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToString(value);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (!Directory.Exists(directory)) return;
        try { Directory.Delete(directory, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

using Microsoft.Data.Sqlite;
using ChronoIsle.App.Services;
using ChronoIsle.App.Services.Commanding;

namespace ChronoIsle.Tests;

public sealed class AssistantCommandPipelineTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "chrono-isle-command-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Ambiguous_or_incomplete_command_writes_no_business_item()
    {
        var (path, pipeline, _) = CreatePipeline();
        var envelope = new AssistantCommandEnvelope(
            AssistantCommandSchema.V1,
            AssistantCommandName.CreateReminder,
            new CreateReminderArgumentsV1("整理计划", null, null, null, null, null),
            [], []);

        var result = pipeline.SubmitParsed("这周找个时间提醒我整理计划", envelope);

        Assert.Equal(AssistantCommandPipelineState.ClarificationRequired, result.State);
        Assert.Equal(0, Scalar(path, "SELECT COUNT(*) FROM life_items WHERE title='整理计划'"));
        Assert.Equal(0, Scalar(path, "SELECT COUNT(*) FROM action_events"));
    }

    [Fact]
    public void Confirm_retry_executes_mutation_once()
    {
        var (path, pipeline, _) = CreatePipeline();
        var created = pipeline.SubmitParsed("创建测试任务", CreateTodo("测试任务"));
        var completion = new AssistantCommandEnvelope(
            AssistantCommandSchema.V1,
            AssistantCommandName.CompleteTodo,
            new CompleteTodoArgumentsV1(new AssistantTargetSelectorV1("测试任务", AssistantItemKindV1.Todo, null)),
            [], []);
        var pending = pipeline.SubmitParsed("完成测试任务", completion);

        var first = pipeline.Confirm(pending.ConfirmationId!);
        var second = pipeline.Confirm(pending.ConfirmationId!);

        Assert.Equal(AssistantCommandPipelineState.Succeeded, created.State);
        Assert.Equal(AssistantCommandPipelineState.Succeeded, first.State);
        Assert.Equal(AssistantCommandPipelineState.Succeeded, second.State);
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM action_events WHERE client_request_id='" + pending.ClientRequestId + "'"));
        Assert.Equal(2, Scalar(path, "SELECT row_version FROM life_items WHERE title='测试任务'"));
        Assert.Equal("Completed", TextScalar(path, "SELECT status FROM life_items WHERE title='测试任务'"));
    }

    [Fact]
    public void Confirmation_becomes_stale_after_target_version_changes()
    {
        var (path, pipeline, _) = CreatePipeline();
        var created = pipeline.SubmitParsed("创建会变化的任务", CreateTodo("会变化的任务"));
        var itemId = Assert.Single(created.ItemIds);
        var update = new AssistantCommandEnvelope(
            AssistantCommandSchema.V1,
            AssistantCommandName.UpdateTodo,
            new UpdateTodoArgumentsV1(
                new AssistantTargetSelectorV1("会变化的任务", AssistantItemKindV1.Todo, null),
                new UpdateTodoChangesV1("新标题", null, null, null, null, null)),
            [], []);
        var pending = pipeline.SubmitParsed("把会变化的任务改名", update);
        new LifeDataService(path).Complete(itemId);

        var result = pipeline.Confirm(pending.ConfirmationId!);

        Assert.Equal(AssistantCommandPipelineState.Stale, result.State);
        Assert.Equal(0, Scalar(path, "SELECT COUNT(*) FROM life_items WHERE title='新标题'"));
    }

    [Fact]
    public void Confirmation_expires_after_fifteen_minutes()
    {
        var (path, pipeline, clock) = CreatePipeline();
        pipeline.SubmitParsed("创建待删除任务", CreateTodo("待删除任务"));
        var deletion = new AssistantCommandEnvelope(
            AssistantCommandSchema.V1,
            AssistantCommandName.DeleteTodo,
            new DeleteTodoArgumentsV1(new AssistantTargetSelectorV1("待删除任务", AssistantItemKindV1.Todo, null)),
            [], []);
        var pending = pipeline.SubmitParsed("删除待删除任务", deletion);
        clock.Now = clock.Now.AddMinutes(16);

        var result = pipeline.Confirm(pending.ConfirmationId!);

        Assert.Equal(AssistantCommandPipelineState.Expired, result.State);
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM life_items WHERE title='待删除任务' AND deleted_at IS NULL"));
    }

    [Fact]
    public void Strict_schema_rejects_model_supplied_local_identifier()
    {
        var json = """
            {
              "schemaVersion":1,
              "command":"create_todo",
              "arguments":{"title":"任务","notes":null,"due":null,"remind":null,"recurrence":null,"priority":null,"clientRequestId":"cr_forged"},
              "missingFields":[],
              "ambiguityReasons":[]
            }
            """;

        var error = Assert.Throws<AssistantCommandContractException>(() => AssistantCommandEnvelopeJson.Deserialize(json));
        Assert.Equal("schema_rejected", error.Code);
    }

    [Fact]
    public void Detailed_audit_can_be_redacted_and_manually_cleared()
    {
        var (path, pipeline, clock) = CreatePipeline();
        pipeline.SubmitParsed("创建私人任务", CreateTodo("私人任务"));
        clock.Now = clock.Now.AddDays(31);

        Assert.True(pipeline.RedactAuditOlderThan30Days() > 0);
        Assert.Null(TextScalar(path, "SELECT source_text FROM user_requests LIMIT 1"));
        Assert.Null(TextScalar(path, "SELECT command_json FROM action_events LIMIT 1"));

        pipeline.SubmitParsed("创建另一个私人任务", CreateTodo("另一个私人任务"));
        Assert.True(pipeline.ClearLocalDetailedAudit() > 0);
        Assert.Equal(0, Scalar(path, "SELECT COUNT(*) FROM user_requests WHERE source_text IS NOT NULL"));
    }

    static AssistantCommandEnvelope CreateTodo(string title) => new(
        AssistantCommandSchema.V1,
        AssistantCommandName.CreateTodo,
        new CreateTodoArgumentsV1(title, null, null, null, null, null),
        [], []);

    (string Path, AssistantCommandPipeline Pipeline, MutableClock Clock) CreatePipeline()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".db");
        var clock = new MutableClock { Now = new DateTimeOffset(2026, 7, 16, 1, 0, 0, TimeSpan.Zero) };
        var pipeline = new AssistantCommandPipeline(path, utcNow: () => clock.Now);
        return (path, pipeline, clock);
    }

    static long Scalar(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    static string? TextScalar(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToString(value);
    }

    public void Dispose()
    {
        if (!Directory.Exists(directory)) return;
        try { Directory.Delete(directory, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    sealed class MutableClock { public DateTimeOffset Now { get; set; } }
}

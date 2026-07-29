using ChronoIsle.App;
using ChronoIsle.App.Services;
using ChronoIsle.App.Services.Commanding;
using Microsoft.Data.Sqlite;

namespace ChronoIsle.Tests;

public sealed class AssistantPlanPipelineV2Tests : IDisposable
{
    readonly string directory = Path.Combine(
        Path.GetTempPath(), "chrono-isle-plan-v2-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Multiple_writes_wait_for_one_confirmation_and_replay_is_idempotent()
    {
        var (path, pipeline, _) = CreatePipeline();

        var pending = pipeline.SubmitPlan(
            "新增甲任务和乙任务",
            [CreateTodo("甲任务"), CreateTodo("乙任务")]);

        Assert.Equal(AssistantPlanPipelineState.AwaitingConfirmation, pending.State);
        Assert.Equal(0, Scalar(path, "SELECT COUNT(*) FROM life_items"));

        var first = pipeline.ConfirmPlan(pending.ConfirmationId!);
        var replay = pipeline.ConfirmPlan(pending.ConfirmationId!);

        Assert.Equal(AssistantPlanPipelineState.Succeeded, first.State);
        Assert.Equal("idempotent_replay", replay.Code);
        Assert.Equal(2, Scalar(path, "SELECT COUNT(*) FROM life_items WHERE deleted_at IS NULL"));
    }

    [Fact]
    public void Any_execution_failure_rolls_back_all_writes()
    {
        var (path, pipeline, _) = CreatePipeline();
        var invalidEvent = new AssistantCommandEnvelope(
            AssistantCommandSchema.V1,
            AssistantCommandName.CreateEvent,
            new CreateEventArgumentsV1(
                "倒序会议",
                null,
                At(10, "上午十点"),
                At(9, "上午九点"),
                null),
            [],
            []);

        var pending = pipeline.SubmitPlan(
            "新增原子任务，并新增倒序会议",
            [CreateTodo("原子任务"), invalidEvent]);
        var result = pipeline.ConfirmPlan(pending.ConfirmationId!);

        Assert.Equal(AssistantPlanPipelineState.Failed, result.State);
        Assert.Equal(0, Scalar(path, "SELECT COUNT(*) FROM life_items WHERE deleted_at IS NULL"));
        Assert.Equal("Failed", TextScalar(path,
            $"SELECT status FROM assistant_plan_confirmations_v2 WHERE confirmation_id='{pending.ConfirmationId}'"));
    }

    [Fact]
    public void Candidate_reference_binds_one_local_target_and_detects_version_change()
    {
        var (path, pipeline, _) = CreatePipeline();
        var single = new AssistantCommandPipeline(path);
        var created = single.SubmitParsed("创建候选任务", CreateTodo("候选任务"));
        var candidate = Assert.Single(pipeline.FindCandidateBindings("完成候选任务", "s1"));
        var completion = new AssistantCommandEnvelope(
            AssistantCommandSchema.V1,
            AssistantCommandName.CompleteTodo,
            new CompleteTodoArgumentsV1(
                new AssistantTargetSelectorV1(
                    candidate.Title, candidate.Kind, null, candidate.CandidateRef)),
            [],
            []);
        var pending = pipeline.SubmitPlan("完成候选任务", [completion], [candidate]);

        new LifeDataService(path).Complete(Assert.Single(created.ItemIds));
        var result = pipeline.ConfirmPlan(pending.ConfirmationId!);

        Assert.Equal(AssistantPlanPipelineState.Stale, result.State);
        Assert.Equal("target_version_changed", result.Code);
    }

    [Fact]
    public void Duplicate_visible_titles_return_multiple_temporary_candidates()
    {
        var (path, pipeline, _) = CreatePipeline();
        var single = new AssistantCommandPipeline(path);
        single.SubmitParsed("创建第一个同名事项", CreateTodo("同名事项"));
        single.SubmitParsed("创建第二个同名事项", CreateTodo("同名事项"));

        var candidates = pipeline.FindCandidateBindings("删除同名事项", "s1");

        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, candidate => Assert.StartsWith("s1_c", candidate.CandidateRef));
        Assert.Equal(2, candidates.Select(candidate => candidate.ItemId).Distinct().Count());
    }

    [Fact]
    public void Dependent_steps_can_mutate_the_same_target_in_order()
    {
        var (path, pipeline, _) = CreatePipeline();
        var single = new AssistantCommandPipeline(path);
        single.SubmitParsed("创建连续任务", CreateTodo("连续任务"));
        var candidate = Assert.Single(pipeline.FindCandidateBindings("修改连续任务", "s1"));
        var target = new AssistantTargetSelectorV1(
            candidate.Title, candidate.Kind, null, candidate.CandidateRef);
        var update = new AssistantCommandEnvelope(
            AssistantCommandSchema.V1,
            AssistantCommandName.UpdateTodo,
            new UpdateTodoArgumentsV1(
                target,
                new UpdateTodoChangesV1("连续任务已改名", null, null, null, null, null)),
            [],
            []);
        var complete = new AssistantCommandEnvelope(
            AssistantCommandSchema.V1,
            AssistantCommandName.CompleteTodo,
            new CompleteTodoArgumentsV1(target),
            [],
            []);

        var pending = pipeline.SubmitPlan(
            "修改连续任务，然后完成它",
            [update, complete],
            [candidate]);
        var result = pipeline.ConfirmPlan(pending.ConfirmationId!);

        Assert.Equal(AssistantPlanPipelineState.Succeeded, result.State);
        Assert.Equal("Completed", TextScalar(path,
            "SELECT status FROM life_items WHERE title='连续任务已改名'"));
        Assert.Equal(3, Scalar(path,
            "SELECT row_version FROM life_items WHERE title='连续任务已改名'"));
    }

    [Fact]
    public void V2_cutover_marks_unconsumed_v1_confirmation_and_clarification_stale()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".db");
        var data = new LifeDataService(path);
        var legacy = new AssistantCommandPipeline(path);
        legacy.SubmitParsed("创建旧任务", CreateTodo("旧任务"));
        var oldPending = legacy.SubmitParsed(
            "删除旧任务",
            new AssistantCommandEnvelope(
                AssistantCommandSchema.V1,
                AssistantCommandName.DeleteTodo,
                new DeleteTodoArgumentsV1(
                    new AssistantTargetSelectorV1("旧任务", AssistantItemKindV1.Todo, null)),
                [],
                []));
        var session = data.NewSession();
        var oldAction = data.SaveAction(session.Id, "旧澄清", "{}", "clarifying");

        _ = new AssistantPlanPipeline(path);

        Assert.Equal("Stale", TextScalar(path,
            $"SELECT status FROM assistant_confirmations WHERE confirmation_id='{oldPending.ConfirmationId}'"));
        Assert.Equal("superseded", TextScalar(path,
            $"SELECT status FROM assistant_actions WHERE id='{oldAction.Id}'"));
        Assert.Equal(1, Scalar(path,
            "SELECT COUNT(*) FROM life_items WHERE title='旧任务' AND deleted_at IS NULL"));
    }

    static AssistantCommandEnvelope CreateTodo(string title) => new(
        AssistantCommandSchema.V1,
        AssistantCommandName.CreateTodo,
        new CreateTodoArgumentsV1(title, null, null, null, null, null),
        [],
        []);

    static AssistantTimeExpressionV1 At(int hour, string originalText) => new(
        new DateOnly(2026, 7, 30),
        new TimeOnly(hour, 0),
        null,
        "Etc/UTC",
        originalText);

    (string Path, AssistantPlanPipeline Pipeline, MutableClock Clock) CreatePipeline()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".db");
        var clock = new MutableClock
        {
            Now = new DateTimeOffset(2026, 7, 29, 1, 0, 0, TimeSpan.Zero)
        };
        var pipeline = new AssistantPlanPipeline(path, utcNow: () => clock.Now);
        return (path, pipeline, clock);
    }

    static long Scalar(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    static string? TextScalar(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToString(value);
    }

    static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();
        return connection;
    }

    public void Dispose()
    {
        if (!Directory.Exists(directory)) return;
        try { Directory.Delete(directory, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    sealed class MutableClock
    {
        public DateTimeOffset Now { get; set; }
    }
}

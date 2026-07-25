using Microsoft.Data.Sqlite;
using ChronoIsle.App.Services.Commanding;

namespace ChronoIsle.Tests;

public sealed class AssistantSubmissionIdempotencyTests
{
    [Fact]
    public void Retrying_auto_create_with_same_local_submission_executes_once()
    {
        var directory = Path.Combine(Path.GetTempPath(), "chrono-isle-submission-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "life.db");
        try
        {
            var firstPipeline = new AssistantCommandPipeline(path);
            var submission = firstPipeline.BeginSubmission();
            var envelope = CreateTodo("幂等任务");

            var first = firstPipeline.SubmitParsed(submission, "创建幂等任务", envelope);
            var secondPipeline = new AssistantCommandPipeline(path);
            var retry = secondPipeline.SubmitParsed(submission, "创建幂等任务", envelope);

            Assert.Equal(AssistantCommandPipelineState.Succeeded, first.State);
            Assert.Equal(AssistantCommandPipelineState.Succeeded, retry.State);
            Assert.Equal("idempotent_replay", retry.Code);
            Assert.Equal(first.ItemIds, retry.ItemIds);
            Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM life_items WHERE title='幂等任务'"));
            Assert.Equal(1, Scalar(path, $"SELECT COUNT(*) FROM action_events WHERE client_request_id='{submission.ClientRequestId}'"));
        }
        finally
        {
            try { Directory.Delete(directory, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void Local_submission_cannot_be_reused_for_different_payload()
    {
        var directory = Path.Combine(Path.GetTempPath(), "chrono-isle-submission-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "life.db");
        try
        {
            var pipeline = new AssistantCommandPipeline(path);
            var submission = pipeline.BeginSubmission();
            pipeline.SubmitParsed(submission, "创建任务甲", CreateTodo("任务甲"));

            var replay = pipeline.SubmitParsed(submission, "创建任务乙", CreateTodo("任务乙"));

            Assert.Equal(AssistantCommandPipelineState.Rejected, replay.State);
            Assert.Equal("submission_token_payload_changed", replay.Code);
            Assert.Equal(0, Scalar(path, "SELECT COUNT(*) FROM life_items WHERE title='任务乙'"));
        }
        finally
        {
            try { Directory.Delete(directory, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    static AssistantCommandEnvelope CreateTodo(string title) => new(
        AssistantCommandSchema.V1,
        AssistantCommandName.CreateTodo,
        new CreateTodoArgumentsV1(title, null, null, null, null, null),
        [], []);

    static long Scalar(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
}

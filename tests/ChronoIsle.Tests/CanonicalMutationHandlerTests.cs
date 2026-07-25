using Microsoft.Data.Sqlite;
using ChronoIsle.App.Services.Commanding;

namespace ChronoIsle.Tests;

public sealed class CanonicalMutationHandlerTests
{
    [Fact]
    public void Update_reschedule_and_delete_each_use_expected_version_and_increment_once()
    {
        var directory = Path.Combine(Path.GetTempPath(), "chrono-isle-canonical-handler-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "life.db");
        try
        {
            var pipeline = new AssistantCommandPipeline(path);
            var create = new AssistantCommandEnvelope(
                AssistantCommandSchema.V1,
                AssistantCommandName.CreateTodo,
                new CreateTodoArgumentsV1("版本任务", null, At(2026, 7, 17, 9), null, null, null),
                [], []);
            var created = pipeline.SubmitParsed("创建版本任务", create);

            var update = new AssistantCommandEnvelope(
                AssistantCommandSchema.V1,
                AssistantCommandName.UpdateTodo,
                new UpdateTodoArgumentsV1(
                    Target("版本任务"),
                    new UpdateTodoChangesV1("已改名任务", "说明", null, null, null, null)),
                [], []);
            var updated = pipeline.Confirm(pipeline.SubmitParsed("修改版本任务", update).ConfirmationId!);

            var reschedule = new AssistantCommandEnvelope(
                AssistantCommandSchema.V1,
                AssistantCommandName.RescheduleItem,
                new RescheduleItemArgumentsV1(Target("已改名任务"), At(2026, 7, 18, 15), null),
                [], []);
            var rescheduled = pipeline.Confirm(pipeline.SubmitParsed("重排已改名任务", reschedule).ConfirmationId!);

            var delete = new AssistantCommandEnvelope(
                AssistantCommandSchema.V1,
                AssistantCommandName.DeleteTodo,
                new DeleteTodoArgumentsV1(Target("已改名任务")),
                [], []);
            var deleted = pipeline.Confirm(pipeline.SubmitParsed("删除已改名任务", delete).ConfirmationId!);

            Assert.Equal(AssistantCommandPipelineState.Succeeded, created.State);
            Assert.Equal(AssistantCommandPipelineState.Succeeded, updated.State);
            Assert.Equal(AssistantCommandPipelineState.Succeeded, rescheduled.State);
            Assert.Equal(AssistantCommandPipelineState.Succeeded, deleted.State);
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
            connection.Open();
            using var query = connection.CreateCommand();
            query.CommandText = "SELECT title,notes,row_version,due_local_datetime,deleted_at FROM life_items WHERE id=$id";
            query.Parameters.AddWithValue("$id", Assert.Single(created.ItemIds));
            using var reader = query.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("已改名任务", reader.GetString(0));
            Assert.Equal("说明", reader.GetString(1));
            Assert.Equal(4, reader.GetInt64(2));
            Assert.StartsWith("2026-07-18T15:00:00", reader.GetString(3), StringComparison.Ordinal);
            Assert.False(reader.IsDBNull(4));
        }
        finally
        {
            try { Directory.Delete(directory, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    static AssistantTargetSelectorV1 Target(string title) =>
        new(title, AssistantItemKindV1.Todo, null);

    static AssistantTimeExpressionV1 At(int year, int month, int day, int hour) =>
        new(new DateOnly(year, month, day), new TimeOnly(hour, 0), null, null, $"{year:D4}-{month:D2}-{day:D2} {hour:D2}:00");
}

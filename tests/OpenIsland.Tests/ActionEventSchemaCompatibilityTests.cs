using Microsoft.Data.Sqlite;
using OpenIsland.App.Services.Commanding;
using OpenIsland.App.Services.Persistence;
using OpenIsland.App.Services.Reporting;

namespace OpenIsland.Tests;

public sealed class ActionEventSchemaCompatibilityTests
{
    [Fact]
    public void Report_schema_created_first_does_not_block_later_command_audit()
    {
        var directory = Path.Combine(Path.GetTempPath(), "open-island-action-event-schema", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "life.db");
        try
        {
            var factory = new SqliteConnectionFactory(path);
            _ = new ReportService(new SqliteDbWriteQueue(factory));
            var pipeline = new AssistantCommandPipeline(path);

            var result = pipeline.SubmitParsed("创建兼容任务", new AssistantCommandEnvelope(
                AssistantCommandSchema.V1, AssistantCommandName.CreateTodo,
                new CreateTodoArgumentsV1("兼容任务", null, null, null, null, null), [], []));

            Assert.Equal(AssistantCommandPipelineState.Succeeded, result.State);
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT event_type,created_at,created_at_utc FROM action_events LIMIT 1";
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("AssistantCommand", reader.GetString(0));
            Assert.False(reader.IsDBNull(1));
            Assert.False(reader.IsDBNull(2));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

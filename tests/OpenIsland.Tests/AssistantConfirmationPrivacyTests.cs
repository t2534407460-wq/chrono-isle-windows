using Microsoft.Data.Sqlite;
using OpenIsland.App.Services.Commanding;

namespace OpenIsland.Tests;

public sealed class AssistantConfirmationPrivacyTests
{
    [Fact]
    public void Clearing_audit_redacts_terminal_confirmation_but_keeps_structural_identity()
    {
        var directory = Path.Combine(Path.GetTempPath(), "open-island-confirmation-privacy-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "life.db");
        try
        {
            var pipeline = new AssistantCommandPipeline(path);
            pipeline.SubmitParsed("创建隐私任务", CreateTodo("隐私任务"));
            var command = new AssistantCommandEnvelope(
                AssistantCommandSchema.V1,
                AssistantCommandName.CompleteTodo,
                new CompleteTodoArgumentsV1(new AssistantTargetSelectorV1("隐私任务", AssistantItemKindV1.Todo, null)),
                [], []);
            var pending = pipeline.SubmitParsed("完成隐私任务", command);
            pipeline.Confirm(pending.ConfirmationId!);

            pipeline.ClearLocalDetailedAudit();

            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
            connection.Open();
            using var query = connection.CreateCommand();
            query.CommandText = """
                SELECT envelope_json,target_snapshot_json,display_snapshot,command_hash,client_request_id
                FROM assistant_confirmations WHERE confirmation_id=$id
                """;
            query.Parameters.AddWithValue("$id", pending.ConfirmationId!);
            using var reader = query.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("{}", reader.GetString(0));
            Assert.Equal("[]", reader.GetString(1));
            Assert.Equal("[redacted]", reader.GetString(2));
            Assert.Equal(64, reader.GetString(3).Length);
            Assert.Equal(pending.ClientRequestId, reader.GetString(4));
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
}

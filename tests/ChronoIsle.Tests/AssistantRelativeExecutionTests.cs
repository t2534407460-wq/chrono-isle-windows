using Microsoft.Data.Sqlite;
using ChronoIsle.App.Services.Commanding;

namespace ChronoIsle.Tests;

public sealed class AssistantRelativeExecutionTests
{
    [Theory]
    [InlineData("10分钟后")]
    [InlineData("1 小时后")]
    [InlineData("一小时后")]
    [InlineData("半小时后")]
    public void Exact_relative_reminder_is_resolved_by_local_code(string expression)
    {
        var directory = Path.Combine(Path.GetTempPath(), "chrono-isle-relative-command-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "life.db");
        try
        {
            var now = new DateTimeOffset(2026, 7, 16, 1, 0, 0, TimeSpan.Zero);
            var pipeline = new AssistantCommandPipeline(path, utcNow: () => now);
            var envelope = new AssistantCommandEnvelope(
                AssistantCommandSchema.V1,
                AssistantCommandName.CreateReminder,
                new CreateReminderArgumentsV1(
                    "喝水", null,
                    new AssistantTimeExpressionV1(null, null, expression, null, expression),
                    null, null, null),
                [], []);

            var result = pipeline.SubmitParsed($"{expression}提醒我喝水", envelope);

            Assert.Equal(AssistantCommandPipelineState.Succeeded, result.State);
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM life_items WHERE kind='Reminder' AND title='喝水' AND remind_utc_instant IS NOT NULL";
            Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar()));
        }
        finally
        {
            try { Directory.Delete(directory, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

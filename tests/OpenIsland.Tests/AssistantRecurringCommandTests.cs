using Microsoft.Data.Sqlite;
using OpenIsland.App.Services.Commanding;

namespace OpenIsland.Tests;

public sealed class AssistantRecurringCommandTests
{
    [Fact]
    public void MonthlyDay31_CreatesRuleWhoseFirstOccurrenceSkipsFebruary()
    {
        var path = Path.Combine(Path.GetTempPath(), $"open-island-recurring-{Guid.NewGuid():N}.db");
        try
        {
            var now = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
            var pipeline = new AssistantCommandPipeline(path, utcNow: () => now);
            var command = new AssistantCommandEnvelope(AssistantCommandSchema.V1,
                AssistantCommandName.CreateRecurringTask,
                new CreateRecurringTaskArgumentsV1("month end", null, AssistantItemKindV1.Todo,
                    new AssistantTimeExpressionV1(new DateOnly(2026, 2, 1), new TimeOnly(9, 0), null, "Etc/UTC", "monthly"),
                    new AssistantRecurrenceRuleV1(AssistantRecurrenceFrequencyV1.Monthly, 1, null, 31,
                        new AssistantRecurrenceEndV1(AssistantRecurrenceEndKindV1.Never, null, null)), null), [], []);

            var pending = pipeline.SubmitParsed("create monthly task", command);
            var result = pending.ConfirmationId is null ? pending : pipeline.Confirm(pending.ConfirmationId);

            Assert.Equal(AssistantCommandPipelineState.Succeeded, result.State);
            using var connection = new SqliteConnection($"Data Source={path}");
            connection.Open();
            using var query = connection.CreateCommand();
            query.CommandText = "SELECT month_day,next_occurrence_utc FROM recurrence_rules";
            using var reader = query.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(31, reader.GetInt64(0));
            Assert.StartsWith("2026-03-31T09:00:00", reader.GetString(1), StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var candidate in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(candidate)) File.Delete(candidate);
        }
    }
}
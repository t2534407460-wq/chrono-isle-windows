using ChronoIsle.App.Services.Persistence;

namespace ChronoIsle.App.Services.Reporting;

public static class ReportSchema
{
    public static void EnsureCreated(IDbWriteQueue writeQueue)
    {
        ArgumentNullException.ThrowIfNull(writeQueue);
        writeQueue.Execute(unitOfWork =>
        {
            using var command = unitOfWork.Connection.CreateCommand();
            command.Transaction = unitOfWork.Transaction;
            command.CommandText = Schema;
            command.ExecuteNonQuery();
            ActionEventSchemaCompatibility.Ensure(unitOfWork);
        });
    }

    internal const string Schema = """
        CREATE TABLE IF NOT EXISTS report_snapshots(
            id TEXT PRIMARY KEY,
            period_kind TEXT NOT NULL CHECK(period_kind IN ('Daily','Weekly','Monthly')),
            period_start_utc TEXT NOT NULL,
            period_end_utc TEXT NOT NULL,
            query_version TEXT NOT NULL CHECK(length(trim(query_version)) > 0),
            facts_json TEXT NOT NULL CHECK(json_valid(facts_json)),
            created_at_utc TEXT NOT NULL,
            CHECK(julianday(period_start_utc) IS NOT NULL),
            CHECK(julianday(period_end_utc) IS NOT NULL),
            CHECK(julianday(period_end_utc) > julianday(period_start_utc))
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ux_report_snapshot_period_query
            ON report_snapshots(period_kind,period_start_utc,period_end_utc,query_version);

        CREATE TABLE IF NOT EXISTS action_events(
            action_event_id TEXT PRIMARY KEY,
            user_request_id TEXT,
            parse_attempt_id TEXT,
            client_request_id TEXT,
            confirmation_id TEXT,
            event_type TEXT NOT NULL DEFAULT 'AssistantCommand',
            command_name TEXT,
            command_json TEXT,
            source_text TEXT,
            raw_parsed_json TEXT,
            structure_summary_json TEXT NOT NULL DEFAULT '{}'
                CHECK(json_valid(structure_summary_json)),
            status TEXT NOT NULL DEFAULT 'Recorded',
            result_json TEXT,
            result_summary_json TEXT,
            error_code TEXT,
            error_message TEXT,
            error_text TEXT,
            created_at TEXT,
            created_at_utc TEXT,
            redacted_at TEXT,
            sanitized_at_utc TEXT
        );
        """;
}

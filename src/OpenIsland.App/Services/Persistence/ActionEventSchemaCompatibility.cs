using Microsoft.Data.Sqlite;

namespace OpenIsland.App.Services.Persistence;

/// <summary>Keeps reporting and command auditing on one additive, backward-compatible table.</summary>
public static class ActionEventSchemaCompatibility
{
    static readonly (string Name, string Definition)[] Columns =
    [
        ("user_request_id", "TEXT"), ("parse_attempt_id", "TEXT"), ("client_request_id", "TEXT"),
        ("confirmation_id", "TEXT"), ("command_name", "TEXT"), ("command_json", "TEXT"),
        ("source_text", "TEXT"), ("status", "TEXT NOT NULL DEFAULT 'Recorded'"),
        ("result_json", "TEXT"), ("error_code", "TEXT"), ("error_message", "TEXT"),
        ("created_at", "TEXT"), ("redacted_at", "TEXT"),
        ("event_type", "TEXT NOT NULL DEFAULT 'AssistantCommand'"), ("raw_parsed_json", "TEXT"),
        ("structure_summary_json", "TEXT NOT NULL DEFAULT '{}'") , ("result_summary_json", "TEXT"),
        ("error_text", "TEXT"), ("created_at_utc", "TEXT"), ("sanitized_at_utc", "TEXT")
    ];

    public static void Ensure(IUnitOfWork unitOfWork)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var columns = Command(unitOfWork, "PRAGMA table_info(action_events)"))
        using (var reader = columns.ExecuteReader())
            while (reader.Read()) existing.Add(reader.GetString(1));

        foreach (var (name, definition) in Columns.Where(column => !existing.Contains(column.Name)))
            using (var alter = Command(unitOfWork, $"ALTER TABLE action_events ADD COLUMN {name} {definition}"))
                alter.ExecuteNonQuery();

        using var indexes = Command(unitOfWork, """
            CREATE UNIQUE INDEX IF NOT EXISTS ux_action_events_client_request_id
                ON action_events(client_request_id) WHERE client_request_id IS NOT NULL;
            CREATE INDEX IF NOT EXISTS ix_action_events_created_at_utc ON action_events(created_at_utc);
            """);
        indexes.ExecuteNonQuery();
    }

    static SqliteCommand Command(IUnitOfWork unitOfWork, string sql)
    {
        var command = unitOfWork.Connection.CreateCommand();
        command.Transaction = unitOfWork.Transaction;
        command.CommandText = sql;
        return command;
    }
}

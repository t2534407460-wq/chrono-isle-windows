using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenIsland.App.Services.Persistence;

namespace OpenIsland.App.Services.Commanding;

internal sealed record StoredAssistantConfirmation(
    string ConfirmationId,
    string UserRequestId,
    string ParseAttemptId,
    string ClientRequestId,
    string CommandHash,
    string TargetSnapshotHash,
    string TargetSnapshotJson,
    string EnvelopeJson,
    DateTimeOffset ExpiresAtUtc,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ConfirmedAtUtc);

/// <summary>Persistence primitives for the trusted, local command pipeline.</summary>
internal sealed class AssistantCommandStore(IDbWriteQueue writeQueue)
{
    public IDbWriteQueue WriteQueue { get; } = writeQueue ?? throw new ArgumentNullException(nameof(writeQueue));

    public void EnsureCreated() => WriteQueue.Execute(unitOfWork =>
    {
        using var command = unitOfWork.Connection.CreateCommand();
        command.Transaction = unitOfWork.Transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS user_requests(
                id TEXT PRIMARY KEY,
                source_text TEXT,
                status TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                redacted_at TEXT);

            CREATE TABLE IF NOT EXISTS parse_attempts(
                id TEXT PRIMARY KEY,
                user_request_id TEXT NOT NULL REFERENCES user_requests(id),
                envelope_json TEXT,
                schema_version INTEGER,
                status TEXT NOT NULL,
                error_code TEXT,
                created_at TEXT NOT NULL,
                redacted_at TEXT);

            CREATE TABLE IF NOT EXISTS assistant_confirmations(
                confirmation_id TEXT PRIMARY KEY,
                user_request_id TEXT NOT NULL REFERENCES user_requests(id),
                parse_attempt_id TEXT NOT NULL REFERENCES parse_attempts(id),
                client_request_id TEXT NOT NULL UNIQUE,
                command_hash TEXT NOT NULL,
                target_snapshot_hash TEXT NOT NULL,
                target_snapshot_json TEXT NOT NULL,
                envelope_json TEXT NOT NULL,
                display_snapshot TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                status TEXT NOT NULL CHECK(status IN ('AwaitingConfirmation','Executing','Succeeded','Failed','Cancelled','Expired','Stale')),
                created_at TEXT NOT NULL,
                confirmed_at TEXT,
                updated_at TEXT NOT NULL);

            CREATE TABLE IF NOT EXISTS action_events(
                action_event_id TEXT PRIMARY KEY,
                user_request_id TEXT REFERENCES user_requests(id),
                parse_attempt_id TEXT REFERENCES parse_attempts(id),
                client_request_id TEXT,
                confirmation_id TEXT REFERENCES assistant_confirmations(confirmation_id),
                command_name TEXT NOT NULL,
                command_json TEXT,
                source_text TEXT,
                status TEXT NOT NULL,
                result_json TEXT,
                error_code TEXT,
                error_message TEXT,
                created_at TEXT NOT NULL,
                redacted_at TEXT);

            CREATE UNIQUE INDEX IF NOT EXISTS ux_action_events_client_request_id
                ON action_events(client_request_id)
                WHERE client_request_id IS NOT NULL;
            CREATE INDEX IF NOT EXISTS ix_assistant_confirmations_status_expires
                ON assistant_confirmations(status,expires_at);
            """;
        command.ExecuteNonQuery();
        ActionEventSchemaCompatibility.Ensure(unitOfWork);
        using var index = unitOfWork.Connection.CreateCommand();
        index.Transaction = unitOfWork.Transaction;
        index.CommandText = "CREATE INDEX IF NOT EXISTS ix_action_events_created_at ON action_events(created_at)";
        index.ExecuteNonQuery();
    });

    public void InsertRequestAndParse(
        IUnitOfWork unitOfWork,
        string userRequestId,
        string parseAttemptId,
        string sourceText,
        AssistantCommandEnvelope envelope,
        string requestStatus,
        DateTimeOffset now)
    {
        var timestamp = Store(now);
        using (var request = unitOfWork.Connection.CreateCommand())
        {
            request.Transaction = unitOfWork.Transaction;
            request.CommandText = """
                INSERT INTO user_requests(id,source_text,status,created_at,updated_at,redacted_at)
                VALUES($id,$source,$status,$now,$now,NULL)
                """;
            request.Parameters.AddWithValue("$id", userRequestId);
            request.Parameters.AddWithValue("$source", sourceText);
            request.Parameters.AddWithValue("$status", requestStatus);
            request.Parameters.AddWithValue("$now", timestamp);
            request.ExecuteNonQuery();
        }
        using var parse = unitOfWork.Connection.CreateCommand();
        parse.Transaction = unitOfWork.Transaction;
        parse.CommandText = """
            INSERT INTO parse_attempts(id,user_request_id,envelope_json,schema_version,status,error_code,created_at,redacted_at)
            VALUES($id,$request,$json,$version,'Accepted',NULL,$now,NULL)
            """;
        parse.Parameters.AddWithValue("$id", parseAttemptId);
        parse.Parameters.AddWithValue("$request", userRequestId);
        parse.Parameters.AddWithValue("$json", AssistantCommandEnvelopeJson.Serialize(envelope));
        parse.Parameters.AddWithValue("$version", envelope.SchemaVersion);
        parse.Parameters.AddWithValue("$now", timestamp);
        parse.ExecuteNonQuery();
    }

    public void SetRequestStatus(IUnitOfWork unitOfWork, string requestId, string status, DateTimeOffset now)
    {
        using var command = unitOfWork.Connection.CreateCommand();
        command.Transaction = unitOfWork.Transaction;
        command.CommandText = "UPDATE user_requests SET status=$status,updated_at=$now WHERE id=$id";
        command.Parameters.AddWithValue("$id", requestId);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$now", Store(now));
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("User request no longer exists.");
    }

    public void InsertConfirmation(
        IUnitOfWork unitOfWork,
        ConfirmationRecord confirmation,
        string userRequestId,
        string parseAttemptId,
        TargetVersionSnapshot targets,
        AssistantCommandEnvelope envelope,
        DateTimeOffset now)
    {
        using var command = unitOfWork.Connection.CreateCommand();
        command.Transaction = unitOfWork.Transaction;
        command.CommandText = """
            INSERT INTO assistant_confirmations(
                confirmation_id,user_request_id,parse_attempt_id,client_request_id,command_hash,
                target_snapshot_hash,target_snapshot_json,envelope_json,display_snapshot,expires_at,
                status,created_at,confirmed_at,updated_at)
            VALUES($id,$request,$parse,$client,$commandHash,$targetHash,$targets,$envelope,$display,
                $expires,'AwaitingConfirmation',$created,NULL,$now)
            """;
        command.Parameters.AddWithValue("$id", confirmation.ConfirmationId);
        command.Parameters.AddWithValue("$request", userRequestId);
        command.Parameters.AddWithValue("$parse", parseAttemptId);
        command.Parameters.AddWithValue("$client", confirmation.ClientRequestId);
        command.Parameters.AddWithValue("$commandHash", confirmation.CommandHash);
        command.Parameters.AddWithValue("$targetHash", confirmation.TargetSnapshotHash);
        command.Parameters.AddWithValue("$targets", JsonSerializer.Serialize(targets.Targets));
        command.Parameters.AddWithValue("$envelope", AssistantCommandEnvelopeJson.Serialize(envelope));
        command.Parameters.AddWithValue("$display", confirmation.DisplaySnapshot);
        command.Parameters.AddWithValue("$expires", Store(confirmation.ExpiresAtUtc));
        command.Parameters.AddWithValue("$created", Store(confirmation.CreatedAtUtc));
        command.Parameters.AddWithValue("$now", Store(now));
        command.ExecuteNonQuery();
    }

    public StoredAssistantConfirmation? ReadConfirmation(IUnitOfWork unitOfWork, string confirmationId)
    {
        using var command = unitOfWork.Connection.CreateCommand();
        command.Transaction = unitOfWork.Transaction;
        command.CommandText = """
            SELECT confirmation_id,user_request_id,parse_attempt_id,client_request_id,command_hash,
                   target_snapshot_hash,target_snapshot_json,envelope_json,expires_at,status,created_at,confirmed_at
            FROM assistant_confirmations WHERE confirmation_id=$id
            """;
        command.Parameters.AddWithValue("$id", confirmationId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            reader.GetString(5), reader.GetString(6), reader.GetString(7), Parse(reader.GetString(8)), reader.GetString(9),
            Parse(reader.GetString(10)), reader.IsDBNull(11) ? null : Parse(reader.GetString(11)));
    }

    public bool TrySetConfirmationStatus(
        IUnitOfWork unitOfWork,
        string confirmationId,
        string expected,
        string next,
        DateTimeOffset now,
        bool confirmed = false)
    {
        using var command = unitOfWork.Connection.CreateCommand();
        command.Transaction = unitOfWork.Transaction;
        command.CommandText = """
            UPDATE assistant_confirmations
            SET status=$next,updated_at=$now,confirmed_at=CASE WHEN $confirmed=1 THEN $now ELSE confirmed_at END
            WHERE confirmation_id=$id AND status=$expected
            """;
        command.Parameters.AddWithValue("$id", confirmationId);
        command.Parameters.AddWithValue("$expected", expected);
        command.Parameters.AddWithValue("$next", next);
        command.Parameters.AddWithValue("$now", Store(now));
        command.Parameters.AddWithValue("$confirmed", confirmed ? 1 : 0);
        return command.ExecuteNonQuery() == 1;
    }

    public string? FindActionResult(IUnitOfWork unitOfWork, string clientRequestId)
    {
        using var command = unitOfWork.Connection.CreateCommand();
        command.Transaction = unitOfWork.Transaction;
        command.CommandText = "SELECT result_json FROM action_events WHERE client_request_id=$id AND status='Succeeded'";
        command.Parameters.AddWithValue("$id", clientRequestId);
        return command.ExecuteScalar() as string;
    }

    public void InsertActionEvent(
        IUnitOfWork unitOfWork,
        string actionEventId,
        string userRequestId,
        string parseAttemptId,
        string clientRequestId,
        string? confirmationId,
        AssistantCommandEnvelope envelope,
        string sourceText,
        string status,
        string? resultJson,
        string? errorCode,
        string? errorMessage,
        DateTimeOffset now)
    {
        using var command = unitOfWork.Connection.CreateCommand();
        command.Transaction = unitOfWork.Transaction;
        command.CommandText = """
            INSERT INTO action_events(
                action_event_id,user_request_id,parse_attempt_id,client_request_id,confirmation_id,command_name,
                command_json,source_text,status,result_json,error_code,error_message,created_at,created_at_utc,
                event_type,structure_summary_json,redacted_at)
            VALUES($id,$request,$parse,$client,$confirmation,$name,$json,$source,$status,$result,$code,$message,$now,$now,
                'AssistantCommand','{}',NULL)
            """;
        command.Parameters.AddWithValue("$id", actionEventId);
        command.Parameters.AddWithValue("$request", userRequestId);
        command.Parameters.AddWithValue("$parse", parseAttemptId);
        command.Parameters.AddWithValue("$client", clientRequestId);
        command.Parameters.AddWithValue("$confirmation", Db(confirmationId));
        command.Parameters.AddWithValue("$name", AssistantCommandEnvelopeJson.CommandName(envelope.Command));
        command.Parameters.AddWithValue("$json", AssistantCommandEnvelopeJson.Serialize(envelope));
        command.Parameters.AddWithValue("$source", sourceText);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$result", Db(resultJson));
        command.Parameters.AddWithValue("$code", Db(errorCode));
        command.Parameters.AddWithValue("$message", Db(errorMessage));
        command.Parameters.AddWithValue("$now", Store(now));
        command.ExecuteNonQuery();
    }

    public int RedactDetailedAudit(DateTimeOffset olderThanUtc) => WriteQueue.Execute(unitOfWork =>
    {
        var threshold = Store(olderThanUtc);
        var now = Store(DateTimeOffset.UtcNow);
        var changed = 0;
        using (var actions = unitOfWork.Connection.CreateCommand())
        {
            actions.Transaction = unitOfWork.Transaction;
            actions.CommandText = """
                UPDATE action_events SET command_json=NULL,source_text=NULL,result_json=NULL,
                    error_message=NULL,redacted_at=$now
                WHERE created_at < $threshold AND redacted_at IS NULL
                """;
            actions.Parameters.AddWithValue("$threshold", threshold);
            actions.Parameters.AddWithValue("$now", now);
            changed += actions.ExecuteNonQuery();
        }
        using (var parses = unitOfWork.Connection.CreateCommand())
        {
            parses.Transaction = unitOfWork.Transaction;
            parses.CommandText = "UPDATE parse_attempts SET envelope_json=NULL,redacted_at=$now WHERE created_at < $threshold AND redacted_at IS NULL";
            parses.Parameters.AddWithValue("$threshold", threshold);
            parses.Parameters.AddWithValue("$now", now);
            changed += parses.ExecuteNonQuery();
        }
        using (var confirmations = unitOfWork.Connection.CreateCommand())
        {
            confirmations.Transaction = unitOfWork.Transaction;
            confirmations.CommandText = """
                UPDATE assistant_confirmations
                SET envelope_json='{}',target_snapshot_json='[]',display_snapshot='[redacted]'
                WHERE created_at < $threshold
                  AND status NOT IN ('AwaitingConfirmation','Executing')
                  AND display_snapshot <> '[redacted]'
                """;
            confirmations.Parameters.AddWithValue("$threshold", threshold);
            changed += confirmations.ExecuteNonQuery();
        }
        using (var requests = unitOfWork.Connection.CreateCommand())
        {
            requests.Transaction = unitOfWork.Transaction;
            requests.CommandText = "UPDATE user_requests SET source_text=NULL,redacted_at=$now WHERE created_at < $threshold AND redacted_at IS NULL";
            requests.Parameters.AddWithValue("$threshold", threshold);
            requests.Parameters.AddWithValue("$now", now);
            changed += requests.ExecuteNonQuery();
        }
        return changed;
    });

    public int ClearDetailedAudit() => RedactDetailedAudit(DateTimeOffset.MaxValue);

    static object Db(string? value) => value is null ? DBNull.Value : value;
    static string Store(DateTimeOffset value) => value.ToUniversalTime().ToString("O");
    static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind);
}

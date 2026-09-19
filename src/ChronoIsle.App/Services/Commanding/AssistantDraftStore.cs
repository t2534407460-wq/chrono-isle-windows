using ChronoIsle.App.Services.Persistence;

namespace ChronoIsle.App.Services.Commanding;

public sealed class AssistantDraftStore
{
    readonly IDbWriteQueue queue;
    readonly SqliteConnectionFactory connections;

    public AssistantDraftStore(string databasePath)
    {
        var runtime = LifeDataStoreRuntimeRegistry.GetOrCreate(databasePath);
        queue = runtime.WriteQueue;
        connections = runtime.ConnectionFactory;
        queue.Execute(uow =>
        {
            using var c = uow.Connection.CreateCommand();
            c.Transaction = uow.Transaction;
            c.CommandText = """
                CREATE TABLE IF NOT EXISTS assistant_drafts_v3(
                    request_id TEXT PRIMARY KEY, session_id TEXT NOT NULL, revision INTEGER NOT NULL,
                    state TEXT NOT NULL, payload_json TEXT NOT NULL, updated_at TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS ix_assistant_drafts_v3_session
                    ON assistant_drafts_v3(session_id,updated_at);
                """;
            c.ExecuteNonQuery();
        });
    }

    public AssistantDraftTurn? Get(string requestId)
    {
        using var db = connections.OpenConnection();
        using var c = db.CreateCommand();
        c.CommandText = "SELECT payload_json FROM assistant_drafts_v3 WHERE request_id=$id";
        c.Parameters.AddWithValue("$id", requestId);
        return c.ExecuteScalar() is string json ? AssistantDraftJson.Read<AssistantDraftTurn>(json) : null;
    }

    public AssistantDraftTurn? Active(string sessionId)
    {
        using var db = connections.OpenConnection();
        using var c = db.CreateCommand();
        c.CommandText = """
            SELECT payload_json FROM assistant_drafts_v3 WHERE session_id=$id
            AND state NOT IN ('Succeeded','Cancelled','Expired','Superseded')
            ORDER BY updated_at DESC LIMIT 1
            """;
        c.Parameters.AddWithValue("$id", sessionId);
        return c.ExecuteScalar() is string json ? AssistantDraftJson.Read<AssistantDraftTurn>(json) : null;
    }

    public void Save(AssistantDraftTurn turn, int? expectedRevision = null)
    {
        queue.Execute(uow =>
        {
            using var c = uow.Connection.CreateCommand();
            c.Transaction = uow.Transaction;
            c.CommandText = expectedRevision is null ? """
                INSERT INTO assistant_drafts_v3 VALUES($id,$session,$revision,$state,$json,$now)
                """ : """
                UPDATE assistant_drafts_v3 SET revision=$revision,state=$state,payload_json=$json,updated_at=$now
                WHERE request_id=$id AND session_id=$session AND revision=$expected
                """;
            c.Parameters.AddWithValue("$id", turn.RequestId);
            c.Parameters.AddWithValue("$session", turn.SessionId);
            c.Parameters.AddWithValue("$revision", turn.Revision);
            c.Parameters.AddWithValue("$state", turn.State);
            c.Parameters.AddWithValue("$json", AssistantDraftJson.Serialize(turn));
            c.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            if (expectedRevision is { } version) c.Parameters.AddWithValue("$expected", version);
            if (c.ExecuteNonQuery() != 1) throw new InvalidOperationException("任务已更新，请使用最新卡片。");
        });
    }
}

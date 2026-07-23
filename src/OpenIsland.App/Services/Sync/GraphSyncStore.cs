using Microsoft.Data.Sqlite;
using OpenIsland.App.Services.Persistence;

namespace OpenIsland.App.Services.Sync;

public enum SyncOutboxStatus { Pending, InFlight, Succeeded, DeadLetter }

public sealed record SyncOutboxWork(
    string Id, string AccountId, SyncProvider Provider, SyncDirection Direction,
    string Operation, string IdempotencyKey, string Payload, int AttemptCount,
    int MaxAttempts, string ClaimToken, DateTimeOffset ClaimExpiresAtUtc);

public sealed record SyncMapping(string Id, string AccountId, SyncProvider Provider, string LocalItemId,
    string RemoteResourceId, bool IsReadOnly, string? ReadOnlyReason);

public sealed class GraphSyncStore
{
    readonly SqliteConnectionFactory connectionFactory;
    readonly IDbWriteQueue writeQueue;
    readonly Func<DateTimeOffset> utcNow;

    public GraphSyncStore(SqliteConnectionFactory connectionFactory, IDbWriteQueue writeQueue,
        Func<DateTimeOffset>? utcNow = null)
    {
        this.connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        this.writeQueue = writeQueue ?? throw new ArgumentNullException(nameof(writeQueue));
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        writeQueue.Execute(Initialize);
    }

    public void UpsertAccount(string accountId, SyncProvider provider, SyncAccountStatus status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        writeQueue.Execute(uow => Execute(uow, """
            INSERT INTO sync_accounts(account_id,provider,status,updated_at_utc)
            VALUES($id,$provider,$status,$now)
            ON CONFLICT(account_id,provider) DO UPDATE SET
              status=excluded.status, updated_at_utc=excluded.updated_at_utc, last_error=NULL
            """, ("$id", accountId), ("$provider", provider.ToString()),
            ("$status", status.ToString()), ("$now", Text(utcNow()))));
    }

    public void MarkReauthRequiredAfterSecretFailure(string accountId, SyncProvider provider, string errorCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        writeQueue.Execute(uow => Execute(uow, """
            UPDATE sync_accounts
            SET status='ReauthRequired', last_error=$error, updated_at_utc=$now
            WHERE account_id=$id AND provider=$provider
            """, ("$id", accountId), ("$provider", provider.ToString()),
            ("$error", "SecretDecryptionFailed:" + errorCode), ("$now", Text(utcNow()))));
    }

    public SyncAccountStatus? GetAccountStatus(string accountId, SyncProvider provider)
    {
        using var connection = connectionFactory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM sync_accounts WHERE account_id=$id AND provider=$provider";
        command.Parameters.AddWithValue("$id", accountId);
        command.Parameters.AddWithValue("$provider", provider.ToString());
        var value = command.ExecuteScalar() as string;
        return value is null ? null : Enum.Parse<SyncAccountStatus>(value);
    }

    public void SaveCursor(SyncCursor cursor)
    {
        ValidateCursor(cursor);
        writeQueue.Execute(uow => Execute(uow, """
            INSERT INTO sync_cursors(account_id,provider,resource_kind,container_id,window_start_utc,window_end_utc,window_key,next_link,delta_link,last_success_at_utc)
            VALUES($account,$provider,$resource,$container,$start,$end,$window,$next,$delta,$success)
            ON CONFLICT(account_id,provider,resource_kind,container_id,window_key) DO UPDATE SET
              next_link=excluded.next_link, delta_link=excluded.delta_link,
              last_success_at_utc=excluded.last_success_at_utc, window_end_utc=excluded.window_end_utc
            """, ("$account", cursor.AccountId), ("$provider", cursor.Provider.ToString()),
            ("$resource", cursor.ResourceKind.ToString()), ("$container", cursor.ContainerId),
            ("$start", DbText(cursor.WindowStartUtc)), ("$end", DbText(cursor.WindowEndUtc)),
            ("$window", cursor.WindowStartUtc is null ? "-" : Text(cursor.WindowStartUtc.Value)),
            ("$next", cursor.NextLink), ("$delta", cursor.DeltaLink),
            ("$success", DbText(cursor.LastSuccessAtUtc))));
    }

    public SyncCursor? LoadCursor(string accountId, SyncProvider provider, SyncResourceKind resourceKind,
        string containerId, DateTimeOffset? windowStartUtc = null)
    {
        using var connection = connectionFactory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT window_start_utc,window_end_utc,next_link,delta_link,last_success_at_utc
            FROM sync_cursors WHERE account_id=$account AND provider=$provider
              AND resource_kind=$resource AND container_id=$container AND window_key=$window
            """;
        command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$provider", provider.ToString());
        command.Parameters.AddWithValue("$resource", resourceKind.ToString());
        command.Parameters.AddWithValue("$container", containerId);
        command.Parameters.AddWithValue("$window", windowStartUtc is null ? "-" : Text(windowStartUtc.Value));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new(accountId, provider, resourceKind, containerId,
            ReadTime(reader, 0), ReadTime(reader, 1), ReadString(reader, 2), ReadString(reader, 3), ReadTime(reader, 4));
    }

    public string Enqueue(string accountId, SyncProvider provider, SyncDirection direction,
        string operation, string idempotencyKey, string payload, int maxAttempts = 5)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        if (maxAttempts is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        return writeQueue.Execute(uow =>
        {
            using var existing = Command(uow, """
                SELECT id FROM sync_outbox WHERE idempotency_key=$key AND deleted_at_utc IS NULL
                """, ("$key", idempotencyKey));
            var prior = existing.ExecuteScalar() as string;
            if (prior is not null) return prior;
            var id = Guid.NewGuid().ToString("N");
            Execute(uow, """
                INSERT INTO sync_outbox(id,account_id,provider,direction,operation,idempotency_key,payload,status,attempt_count,max_attempts,next_retry_at_utc,created_at_utc)
                VALUES($id,$account,$provider,$direction,$operation,$key,$payload,'Pending',0,$max,$now,$now)
                """, ("$id", id), ("$account", accountId), ("$provider", provider.ToString()),
                ("$direction", direction.ToString()), ("$operation", operation), ("$key", idempotencyKey),
                ("$payload", payload), ("$max", maxAttempts), ("$now", Text(utcNow())));
            return id;
        });
    }

    public SyncOutboxWork? ClaimNext(TimeSpan? lease = null)
    {
        var duration = lease ?? TimeSpan.FromSeconds(60);
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lease));
        return writeQueue.Execute<SyncOutboxWork?>(uow =>
        {
            var now = utcNow();
            using var find = Command(uow, """
                SELECT id,account_id,provider,direction,operation,idempotency_key,payload,attempt_count,max_attempts
                FROM sync_outbox
                WHERE deleted_at_utc IS NULL AND attempt_count < max_attempts
                  AND next_retry_at_utc <= $now
                  AND (status='Pending' OR (status='InFlight' AND claim_expires_at_utc <= $now))
                ORDER BY created_at_utc,id LIMIT 1
                """, ("$now", Text(now)));
            using var reader = find.ExecuteReader();
            if (!reader.Read()) return null;
            var token = Guid.NewGuid().ToString("N");
            var expires = now + duration;
            var values = new object[] { reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetInt32(7), reader.GetInt32(8) };
            reader.Close();
            var changed = Execute(uow, """
                UPDATE sync_outbox SET status='InFlight',claim_token=$token,claim_expires_at_utc=$expires
                WHERE id=$id AND (status='Pending' OR claim_expires_at_utc <= $now)
                """, ("$token", token), ("$expires", Text(expires)), ("$id", values[0]), ("$now", Text(now)));
            if (changed != 1) return null;
            return new((string)values[0], (string)values[1], Enum.Parse<SyncProvider>((string)values[2]),
                Enum.Parse<SyncDirection>((string)values[3]), (string)values[4], (string)values[5], (string)values[6],
                (int)values[7], (int)values[8], token, expires);
        });
    }

    public bool Complete(string id, string claimToken) => writeQueue.Execute(uow => Execute(uow, """
        UPDATE sync_outbox SET status='Succeeded',claim_token=NULL,claim_expires_at_utc=NULL,last_error=NULL
        WHERE id=$id AND status='InFlight' AND claim_token=$token
        """, ("$id", id), ("$token", claimToken)) == 1);

    public SyncOutboxStatus Fail(string id, string claimToken, string error)
    {
        return writeQueue.Execute(uow =>
        {
            using var read = Command(uow, "SELECT attempt_count,max_attempts FROM sync_outbox WHERE id=$id AND status='InFlight' AND claim_token=$token",
                ("$id", id), ("$token", claimToken));
            using var reader = read.ExecuteReader();
            if (!reader.Read()) throw new InvalidOperationException("The outbox lease is no longer owned by this worker.");
            var attempt = reader.GetInt32(0) + 1;
            var maximum = reader.GetInt32(1);
            reader.Close();
            var status = attempt >= maximum ? SyncOutboxStatus.DeadLetter : SyncOutboxStatus.Pending;
            var delaySeconds = Math.Min(3600, 5 * Math.Pow(2, attempt - 1));
            Execute(uow, """
                UPDATE sync_outbox SET status=$status,attempt_count=$attempt,next_retry_at_utc=$retry,
                  claim_token=NULL,claim_expires_at_utc=NULL,last_error=$error
                WHERE id=$id AND claim_token=$token
                """, ("$status", status.ToString()), ("$attempt", attempt),
                ("$retry", Text(utcNow().AddSeconds(delaySeconds))), ("$error", error),
                ("$id", id), ("$token", claimToken));
            return status;
        });
    }

    public SyncMapping? FindActiveMapping(string accountId, SyncProvider provider, string localItemId)
    {
        using var connection = connectionFactory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,account_id,provider,local_item_id,remote_resource_id,is_readonly,readonly_reason
            FROM sync_mappings
            WHERE account_id=$account AND provider=$provider AND local_item_id=$local AND deleted_at_utc IS NULL
            """;
        command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$provider", provider.ToString());
        command.Parameters.AddWithValue("$local", localItemId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), reader.GetString(1), Enum.Parse<SyncProvider>(reader.GetString(2)),
            reader.GetString(3), reader.GetString(4), reader.GetInt32(5) != 0, ReadString(reader, 6)) : null;
    }

    public void AddMapping(string id, string accountId, SyncProvider provider, string localItemId,
        string remoteResourceId, ExternalMappingResult mapping)
    {
        writeQueue.Execute(uow => Execute(uow, """
            INSERT INTO sync_mappings(id,account_id,provider,local_item_id,remote_resource_id,is_readonly,readonly_reason,created_at_utc)
            VALUES($id,$account,$provider,$local,$remote,$readonly,$reason,$now)
            """, ("$id", id), ("$account", accountId), ("$provider", provider.ToString()),
            ("$local", localItemId), ("$remote", remoteResourceId),
            ("$readonly", mapping.Disposition == ExternalMappingDisposition.Editable ? 0 : 1),
            ("$reason", mapping.ReadOnlyReason), ("$now", Text(utcNow()))));
    }

    public bool SoftDeleteMapping(string id) => writeQueue.Execute(uow => Execute(uow,
        "UPDATE sync_mappings SET deleted_at_utc=$now WHERE id=$id AND deleted_at_utc IS NULL",
        ("$now", Text(utcNow())), ("$id", id)) == 1);

    public string RecordConflict(string accountId, SyncProvider provider, string localItemId,
        string remoteResourceId, string localFingerprint, string remoteFingerprint)
    {
        var id = Guid.NewGuid().ToString("N");
        writeQueue.Execute(uow => Execute(uow, """
            INSERT INTO sync_conflicts(id,account_id,provider,local_item_id,remote_resource_id,local_fingerprint,remote_fingerprint,status,created_at_utc)
            VALUES($id,$account,$provider,$local,$remote,$localHash,$remoteHash,'Unresolved',$now)
            """, ("$id", id), ("$account", accountId), ("$provider", provider.ToString()),
            ("$local", localItemId), ("$remote", remoteResourceId),
            ("$localHash", localFingerprint), ("$remoteHash", remoteFingerprint), ("$now", Text(utcNow()))));
        return id;
    }

    void Initialize(IUnitOfWork uow) => Execute(uow, """
        CREATE TABLE IF NOT EXISTS sync_accounts(
          account_id TEXT NOT NULL, provider TEXT NOT NULL, status TEXT NOT NULL,
          last_error TEXT NULL, updated_at_utc TEXT NOT NULL,
          PRIMARY KEY(account_id,provider),
          CHECK(status IN ('Disabled','Connected','ReauthRequired')));
        CREATE TABLE IF NOT EXISTS sync_cursors(
          account_id TEXT NOT NULL, provider TEXT NOT NULL, resource_kind TEXT NOT NULL, container_id TEXT NOT NULL,
          window_start_utc TEXT NULL, window_end_utc TEXT NULL, window_key TEXT NOT NULL,
          next_link TEXT NULL, delta_link TEXT NULL, last_success_at_utc TEXT NULL,
          PRIMARY KEY(account_id,provider,resource_kind,container_id,window_key),
          CHECK((provider='MicrosoftToDo' AND resource_kind IN ('TodoTaskList','TodoTask') AND window_start_utc IS NULL AND window_end_utc IS NULL)
             OR (provider='OutlookCalendar' AND resource_kind='CalendarEvent' AND window_start_utc IS NOT NULL AND window_end_utc IS NOT NULL AND window_end_utc > window_start_utc)));
        CREATE TABLE IF NOT EXISTS sync_outbox(
          id TEXT PRIMARY KEY, account_id TEXT NOT NULL, provider TEXT NOT NULL, direction TEXT NOT NULL,
          operation TEXT NOT NULL, idempotency_key TEXT NOT NULL, payload TEXT NOT NULL,
          status TEXT NOT NULL, attempt_count INTEGER NOT NULL, max_attempts INTEGER NOT NULL,
          next_retry_at_utc TEXT NOT NULL, claim_token TEXT NULL, claim_expires_at_utc TEXT NULL,
          last_error TEXT NULL, created_at_utc TEXT NOT NULL, deleted_at_utc TEXT NULL,
          CHECK(attempt_count >= 0 AND max_attempts > 0 AND attempt_count <= max_attempts),
          CHECK(status IN ('Pending','InFlight','Succeeded','DeadLetter')));
        CREATE UNIQUE INDEX IF NOT EXISTS ux_sync_outbox_active_key ON sync_outbox(idempotency_key) WHERE deleted_at_utc IS NULL;
        CREATE TABLE IF NOT EXISTS sync_mappings(
          id TEXT PRIMARY KEY, account_id TEXT NOT NULL, provider TEXT NOT NULL, local_item_id TEXT NOT NULL,
          remote_resource_id TEXT NOT NULL, is_readonly INTEGER NOT NULL, readonly_reason TEXT NULL,
          created_at_utc TEXT NOT NULL, deleted_at_utc TEXT NULL,
          CHECK(is_readonly IN (0,1)), CHECK(is_readonly=0 OR readonly_reason IS NOT NULL));
        CREATE UNIQUE INDEX IF NOT EXISTS ux_sync_mapping_active_remote
          ON sync_mappings(account_id,provider,remote_resource_id) WHERE deleted_at_utc IS NULL;
        CREATE UNIQUE INDEX IF NOT EXISTS ux_sync_mapping_active_local
          ON sync_mappings(account_id,provider,local_item_id) WHERE deleted_at_utc IS NULL;
        CREATE TABLE IF NOT EXISTS sync_conflicts(
          id TEXT PRIMARY KEY, account_id TEXT NOT NULL, provider TEXT NOT NULL, local_item_id TEXT NOT NULL,
          remote_resource_id TEXT NOT NULL, local_fingerprint TEXT NOT NULL, remote_fingerprint TEXT NOT NULL,
          status TEXT NOT NULL, resolution TEXT NULL, created_at_utc TEXT NOT NULL, resolved_at_utc TEXT NULL,
          CHECK(status IN ('Unresolved','Resolved')));
        CREATE UNIQUE INDEX IF NOT EXISTS ux_sync_conflict_unresolved
          ON sync_conflicts(account_id,provider,local_item_id,remote_resource_id) WHERE status='Unresolved';
        """);

    static void ValidateCursor(SyncCursor cursor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cursor.AccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(cursor.ContainerId);
        if (cursor.Provider == SyncProvider.MicrosoftToDo &&
            (cursor.WindowStartUtc is not null || cursor.WindowEndUtc is not null ||
             cursor.ResourceKind is not (SyncResourceKind.TodoTaskList or SyncResourceKind.TodoTask)))
            throw new ArgumentException("To Do cursors are list-scoped and cannot have a time window.", nameof(cursor));
        if (cursor.Provider == SyncProvider.OutlookCalendar &&
            (cursor.ResourceKind != SyncResourceKind.CalendarEvent || cursor.WindowStartUtc is null ||
             cursor.WindowEndUtc is null || cursor.WindowEndUtc <= cursor.WindowStartUtc))
            throw new ArgumentException("Calendar cursors require a valid calendar-view window.", nameof(cursor));
    }

    static SqliteCommand Command(IUnitOfWork uow, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = uow.Connection.CreateCommand();
        command.Transaction = uow.Transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return command;
    }

    static int Execute(IUnitOfWork uow, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(uow, sql, parameters);
        return command.ExecuteNonQuery();
    }

    static string Text(DateTimeOffset value) => value.ToUniversalTime().ToString("O");
    static object DbText(DateTimeOffset? value) => value is null ? DBNull.Value : Text(value.Value);
    static string? ReadString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    static DateTimeOffset? ReadTime(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal));
}

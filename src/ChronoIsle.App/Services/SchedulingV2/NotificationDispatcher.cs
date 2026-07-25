using System.Globalization;
using Microsoft.Data.Sqlite;
using ChronoIsle.App.Services.Persistence;

namespace ChronoIsle.App.Services.Scheduling;

public sealed record NotificationDeliveryRequest(
    long OutboxId,
    string IdempotencyKey,
    string ItemId,
    string OccurrenceKey,
    string ActionType,
    DateTimeOffset TargetDeliveryAtUtc,
    string ClaimToken,
    int AttemptCount);

public enum NotificationDispatchOutcome
{
    NothingDue,
    Delivered,
    RetryScheduled,
    DeadLettered,
    LostClaim
}

public sealed record NotificationDispatcherHealth(
    bool IsDegraded,
    int ConsecutiveFailures,
    DateTimeOffset? LastSuccessAtUtc,
    DateTimeOffset? LastFailureAtUtc,
    string? LastError);

public interface INotificationTransport
{
    Task DeliverAsync(NotificationDeliveryRequest request, CancellationToken cancellationToken);
}

public interface INotificationDispatcher
{
    NotificationDispatcherHealth Health { get; }
    Task<NotificationDispatchOutcome> DispatchNextAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
}

public sealed class NotificationDispatcher : INotificationDispatcher
{
    public static readonly TimeSpan ClaimLease = TimeSpan.FromSeconds(60);

    readonly NotificationOutboxStore store;
    readonly INotificationTransport transport;
    readonly int maximumAttempts;
    readonly object healthLock = new();
    NotificationDispatcherHealth health = new(false, 0, null, null, null);

    public NotificationDispatcher(
        NotificationOutboxStore store,
        INotificationTransport transport,
        int maximumAttempts = 5)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.maximumAttempts = maximumAttempts >= 1
            ? maximumAttempts
            : throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
    }

    public event EventHandler<NotificationDispatcherHealth>? HealthChanged;

    public NotificationDispatcherHealth Health
    {
        get { lock (healthLock) return health; }
    }

    public async Task<NotificationDispatchOutcome> DispatchNextAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        var claimToken = Guid.NewGuid().ToString("N");
        var request = store.TryClaim(nowUtc, claimToken, ClaimLease);
        if (request is null) return NotificationDispatchOutcome.NothingDue;

        try
        {
            // Toast, network and other external work must never hold the SQLite write queue.
            await transport.DeliverAsync(request, cancellationToken).ConfigureAwait(false);
            if (!store.MarkDelivered(request, nowUtc))
                return NotificationDispatchOutcome.LostClaim;

            UpdateHealth(new(false, 0, nowUtc, Health.LastFailureAtUtc, null));
            return NotificationDispatchOutcome.Delivered;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            store.ReleaseClaim(request);
            throw;
        }
        catch (Exception exception)
        {
            var outcome = store.MarkFailed(request, nowUtc, exception.Message, maximumAttempts);
            var previous = Health;
            UpdateHealth(new(
                outcome == NotificationDispatchOutcome.DeadLettered,
                previous.ConsecutiveFailures + 1,
                previous.LastSuccessAtUtc,
                nowUtc,
                exception.Message));
            return outcome;
        }
    }

    void UpdateHealth(NotificationDispatcherHealth value)
    {
        lock (healthLock) health = value;
        try { HealthChanged?.Invoke(this, value); }
        catch { /* Observers cannot terminate the dispatcher. */ }
    }
}

public sealed class NotificationOutboxStore
{
    readonly IDbWriteQueue queue;

    public NotificationOutboxStore(IDbWriteQueue queue)
    {
        this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
        queue.Execute(uow =>
        {
            using var create = Command(uow, Schema);
            create.ExecuteNonQuery();
            EnsureColumn(uow, "claimed_at_utc", "TEXT NULL");
            EnsureColumn(uow, "claim_token", "TEXT NULL");
            EnsureColumn(uow, "claim_expires_at_utc", "TEXT NULL");
        });
    }

    public NotificationDeliveryRequest? TryClaim(DateTimeOffset nowUtc, string claimToken, TimeSpan lease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimToken);
        if (lease <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lease));
        var expiresAt = nowUtc.ToUniversalTime().Add(lease);

        return queue.Execute<NotificationDeliveryRequest?>(uow =>
        {
            using var command = Command(uow, """
                UPDATE notification_outbox
                SET status='Claimed', claimed_at_utc=$now, claim_token=$token,
                    claim_expires_at_utc=$expires, attempt_count=attempt_count+1, last_error=NULL
                WHERE id=(
                    SELECT id FROM notification_outbox
                    WHERE deleted_at_utc IS NULL AND target_delivery_at_utc <= $now AND
                      ((status IN ('Pending','RetryWait') AND (next_retry_at_utc IS NULL OR next_retry_at_utc <= $now)) OR
                       (status='Claimed' AND claim_expires_at_utc IS NOT NULL AND claim_expires_at_utc <= $now))
                    ORDER BY target_delivery_at_utc,id LIMIT 1)
                  AND deleted_at_utc IS NULL AND
                      ((status IN ('Pending','RetryWait') AND (next_retry_at_utc IS NULL OR next_retry_at_utc <= $now)) OR
                       (status='Claimed' AND claim_expires_at_utc IS NOT NULL AND claim_expires_at_utc <= $now))
                RETURNING id,notification_idempotency_key,item_id,occurrence_key,action_type,
                          target_delivery_at_utc,attempt_count
                """);
            command.Parameters.AddWithValue("$now", Format(nowUtc));
            command.Parameters.AddWithValue("$token", claimToken);
            command.Parameters.AddWithValue("$expires", Format(expiresAt));
            using var reader = command.ExecuteReader();
            return reader.Read()
                ? new NotificationDeliveryRequest(
                    reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), Parse(reader.GetString(5)), claimToken, reader.GetInt32(6))
                : null;
        });
    }

    public bool MarkDelivered(NotificationDeliveryRequest request, DateTimeOffset deliveredAtUtc) =>
        queue.Execute(uow =>
        {
            var changed = UpdateOwned(uow, request, """
                UPDATE notification_outbox
                SET status='Delivered',claim_token=NULL,claim_expires_at_utc=NULL,next_retry_at_utc=NULL
                WHERE id=$id AND status='Claimed' AND claim_token=$token AND deleted_at_utc IS NULL
                """);
            if (!changed) return false;
            InsertDelivery(uow, request.OutboxId, "ToastAndIsland", "Delivered", deliveredAtUtc, null);
            return true;
        });

    public NotificationDispatchOutcome MarkFailed(
        NotificationDeliveryRequest request,
        DateTimeOffset failedAtUtc,
        string error,
        int maximumAttempts)
    {
        var deadLetter = request.AttemptCount >= maximumAttempts;
        var delaySeconds = Math.Min(900, 5 * Math.Pow(2, Math.Clamp(request.AttemptCount - 1, 0, 8)));
        var nextRetry = failedAtUtc.ToUniversalTime().AddSeconds(delaySeconds);
        var sanitized = string.IsNullOrWhiteSpace(error) ? "Unknown notification failure" : error;
        sanitized = sanitized[..Math.Min(2000, sanitized.Length)];

        return queue.Execute(uow =>
        {
            using var command = Command(uow, """
                UPDATE notification_outbox
                SET status=$status,next_retry_at_utc=$retry,last_error=$error,
                    claim_token=NULL,claim_expires_at_utc=NULL
                WHERE id=$id AND status='Claimed' AND claim_token=$token AND deleted_at_utc IS NULL
                """);
            command.Parameters.AddWithValue("$status", deadLetter ? "DeadLetter" : "RetryWait");
            command.Parameters.AddWithValue("$retry", deadLetter ? DBNull.Value : Format(nextRetry));
            command.Parameters.AddWithValue("$error", sanitized);
            command.Parameters.AddWithValue("$id", request.OutboxId);
            command.Parameters.AddWithValue("$token", request.ClaimToken);
            if (command.ExecuteNonQuery() != 1) return NotificationDispatchOutcome.LostClaim;
            InsertDelivery(uow, request.OutboxId, "ToastAndIsland", "Failed", failedAtUtc, sanitized);
            return deadLetter ? NotificationDispatchOutcome.DeadLettered : NotificationDispatchOutcome.RetryScheduled;
        });
    }

    public bool ReleaseClaim(NotificationDeliveryRequest request) => queue.Execute(uow =>
        UpdateOwned(uow, request, """
            UPDATE notification_outbox
            SET status='Pending',claim_token=NULL,claim_expires_at_utc=NULL
            WHERE id=$id AND status='Claimed' AND claim_token=$token AND deleted_at_utc IS NULL
            """));

    static bool UpdateOwned(IUnitOfWork uow, NotificationDeliveryRequest request, string sql)
    {
        using var command = Command(uow, sql);
        command.Parameters.AddWithValue("$id", request.OutboxId);
        command.Parameters.AddWithValue("$token", request.ClaimToken);
        return command.ExecuteNonQuery() == 1;
    }

    static void InsertDelivery(
        IUnitOfWork uow,
        long outboxId,
        string target,
        string status,
        DateTimeOffset atUtc,
        string? error)
    {
        using var command = Command(uow, """
            INSERT INTO notification_deliveries(outbox_id,delivery_target,delivered_at_utc,status,error)
            VALUES($outbox,$target,$at,$status,$error)
            """);
        command.Parameters.AddWithValue("$outbox", outboxId);
        command.Parameters.AddWithValue("$target", target);
        command.Parameters.AddWithValue("$at", Format(atUtc));
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    static void EnsureColumn(IUnitOfWork uow, string name, string definition)
    {
        using var info = Command(uow, "PRAGMA table_info(notification_outbox)");
        using var reader = info.ExecuteReader();
        while (reader.Read())
            if (string.Equals(reader.GetString(1), name, StringComparison.OrdinalIgnoreCase)) return;
        reader.Close();
        using var alter = Command(uow, $"ALTER TABLE notification_outbox ADD COLUMN {name} {definition}");
        alter.ExecuteNonQuery();
    }

    static SqliteCommand Command(IUnitOfWork uow, string sql)
    {
        var command = uow.Connection.CreateCommand();
        command.Transaction = uow.Transaction;
        command.CommandText = sql;
        return command;
    }

    static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    static DateTimeOffset Parse(string value) =>
        DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    const string Schema = """
        CREATE TABLE IF NOT EXISTS notification_outbox(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            notification_idempotency_key TEXT NOT NULL,
            item_id TEXT NOT NULL,
            occurrence_key TEXT NOT NULL,
            action_type TEXT NOT NULL,
            target_delivery_at_utc TEXT NOT NULL,
            status TEXT NOT NULL CHECK(status IN('Pending','Claimed','Delivered','RetryWait','DeadLetter','Cancelled')),
            attempt_count INTEGER NOT NULL DEFAULT 0 CHECK(attempt_count>=0),
            next_retry_at_utc TEXT NULL,
            last_error TEXT NULL,
            created_at_utc TEXT NOT NULL,
            deleted_at_utc TEXT NULL,
            claimed_at_utc TEXT NULL,
            claim_token TEXT NULL,
            claim_expires_at_utc TEXT NULL);
        CREATE UNIQUE INDEX IF NOT EXISTS ux_notification_outbox_active_key
            ON notification_outbox(notification_idempotency_key) WHERE deleted_at_utc IS NULL;
        CREATE TABLE IF NOT EXISTS notification_deliveries(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            outbox_id INTEGER NOT NULL,
            delivery_target TEXT NOT NULL,
            delivered_at_utc TEXT NULL,
            status TEXT NOT NULL,
            error TEXT NULL,
            FOREIGN KEY(outbox_id) REFERENCES notification_outbox(id));
        """;
}

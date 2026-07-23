using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using OpenIsland.App.Services.Persistence;

namespace OpenIsland.App.Services.Scheduling;

public sealed record ReminderOccurrenceSeed(string OccurrenceKey, string ItemId, DateTimeOffset DueAtUtc, int RuleRevision = 1);
public sealed record ReminderOccurrenceLease(string OccurrenceKey, string ItemId, DateTimeOffset DueAtUtc, int RuleRevision, string ClaimToken, DateTimeOffset ClaimExpiresAtUtc, int AttemptCount);
public enum ReminderFailureOutcome { NotOwned, RetryScheduled, DeadLettered }
public interface IReminderDueDetector
{
    bool Register(ReminderOccurrenceSeed value);
    ReminderOccurrenceLease? TryClaimDue(DateTimeOffset nowUtc, string claimToken);
    bool MarkDelivered(ReminderOccurrenceLease lease, DateTimeOffset atUtc);
    ReminderFailureOutcome MarkFailed(ReminderOccurrenceLease lease, DateTimeOffset atUtc, string error);
}
public sealed class ReminderDueDetector : IReminderDueDetector
{
    public static readonly TimeSpan ClaimLease = TimeSpan.FromSeconds(60);
    readonly ReminderDeliveryStore store; readonly int maxAttempts;
    public ReminderDueDetector(ReminderDeliveryStore store, int maximumAttempts = 5)
    { this.store = store; maxAttempts = maximumAttempts >= 1 ? maximumAttempts : throw new ArgumentOutOfRangeException(nameof(maximumAttempts)); }
    public bool Register(ReminderOccurrenceSeed value) => store.RegisterOccurrence(value);
    public ReminderOccurrenceLease? TryClaimDue(DateTimeOffset nowUtc, string token) => store.TryClaimDue(nowUtc, token, ClaimLease);
    public bool MarkDelivered(ReminderOccurrenceLease lease, DateTimeOffset atUtc) => store.MarkDelivered(lease, atUtc);
    public ReminderFailureOutcome MarkFailed(ReminderOccurrenceLease lease, DateTimeOffset atUtc, string error) => store.MarkFailed(lease, atUtc, error, maxAttempts);
}

public sealed record NotificationOutboxEntry(string IdempotencyKey, string ItemId, string OccurrenceKey, string ActionType, DateTimeOffset TargetDeliveryAtUtc);
public static class NotificationIdempotencyKey
{
    public static string Create(string item, string occurrence, string action, int revision, DateTimeOffset target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(item); ArgumentException.ThrowIfNullOrWhiteSpace(occurrence); ArgumentException.ThrowIfNullOrWhiteSpace(action);
        var value = string.Join('\u001f', item, occurrence, action, revision.ToString(CultureInfo.InvariantCulture), target.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}

public enum ReminderPriority { Low, Normal, High, Urgent }
public sealed record ReminderPresentationContext(bool IsFullScreen, bool IsFocusMode, bool IsDoNotDisturb)
{ public static ReminderPresentationContext Normal { get; } = new(false, false, false); }
public enum ReminderPolicyDecisionKind { DeliverImmediately, IslandBannerOnly, Defer }
public sealed record ReminderPolicyInput(string ItemId, string OccurrenceKey, DateTimeOffset OriginalDueAtUtc, ReminderPriority Priority, ReminderPresentationContext Context);
public sealed record ReminderPolicyDecision(ReminderPolicyDecisionKind Kind, string Reason);
public sealed record DeferredNotificationSummary(int Total, int Overdue, int Low, int Normal, int High, int Urgent, string? SummaryBatchId);
public sealed class ReminderPolicyService
{
    readonly ReminderDeliveryStore store;
    public ReminderPolicyService(ReminderDeliveryStore store) => this.store = store;
    public ReminderPolicyDecision Apply(ReminderPolicyInput input, DateTimeOffset now)
    { var result = Evaluate(input); if (result.Kind == ReminderPolicyDecisionKind.Defer) store.Defer(input, result.Reason, now); return result; }
    public static ReminderPolicyDecision Evaluate(ReminderPolicyInput input)
    {
        if (input.Context.IsDoNotDisturb && input.Priority != ReminderPriority.Urgent) return new(ReminderPolicyDecisionKind.Defer, "DoNotDisturb");
        if (input.Context.IsFocusMode && input.Priority == ReminderPriority.Low) return new(ReminderPolicyDecisionKind.Defer, "FocusLowPriority");
        if (input.Context.IsFullScreen) return new(ReminderPolicyDecisionKind.IslandBannerOnly, "FullScreenSilent");
        return new(ReminderPolicyDecisionKind.DeliverImmediately, "Normal");
    }
    public DeferredNotificationSummary ReleaseDeferredSummary(DateTimeOffset now, string batchId) => store.ReleaseDeferredSummary(now, batchId);
}

public sealed class ReminderDeliveryStore
{
    readonly IDbWriteQueue queue;
    public ReminderDeliveryStore(IDbWriteQueue queue)
    { this.queue = queue ?? throw new ArgumentNullException(nameof(queue)); queue.Execute(u => { using var c = Cmd(u, Schema); c.ExecuteNonQuery(); }); }

    public bool RegisterOccurrence(ReminderOccurrenceSeed value) => queue.Execute(u =>
    {
        using var c = Cmd(u, "INSERT INTO reminder_occurrences(occurrence_key,item_id,rule_revision,due_at_utc,delivery_status,attempt_count,created_at_utc) VALUES($k,$i,$r,$d,'Pending',0,$c) ON CONFLICT(occurrence_key) DO NOTHING");
        c.Parameters.AddWithValue("$k", value.OccurrenceKey); c.Parameters.AddWithValue("$i", value.ItemId); c.Parameters.AddWithValue("$r", value.RuleRevision);
        c.Parameters.AddWithValue("$d", F(value.DueAtUtc)); c.Parameters.AddWithValue("$c", F(DateTimeOffset.UtcNow)); return c.ExecuteNonQuery() == 1;
    });

    public ReminderOccurrenceLease? TryClaimDue(DateTimeOffset now, string token, TimeSpan lease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token); var expires = now.ToUniversalTime().Add(lease);
        return queue.Execute<ReminderOccurrenceLease?>(u =>
        {
            using var c = Cmd(u, """
                UPDATE reminder_occurrences SET claimed_at_utc=$now,claim_token=$token,claim_expires_at_utc=$expires,delivery_status='Claimed',attempt_count=attempt_count+1,last_error=NULL
                WHERE occurrence_key=(SELECT occurrence_key FROM reminder_occurrences WHERE due_at_utc<=$now AND
                  ((delivery_status IN ('Pending','RetryWait') AND (next_retry_at_utc IS NULL OR next_retry_at_utc<=$now)) OR
                   (delivery_status='Claimed' AND claim_expires_at_utc IS NOT NULL AND claim_expires_at_utc<=$now)) ORDER BY due_at_utc,occurrence_key LIMIT 1)
                AND ((delivery_status IN ('Pending','RetryWait') AND (next_retry_at_utc IS NULL OR next_retry_at_utc<=$now)) OR
                     (delivery_status='Claimed' AND claim_expires_at_utc IS NOT NULL AND claim_expires_at_utc<=$now))
                RETURNING occurrence_key,item_id,due_at_utc,rule_revision,attempt_count
                """);
            c.Parameters.AddWithValue("$now", F(now)); c.Parameters.AddWithValue("$token", token); c.Parameters.AddWithValue("$expires", F(expires));
            using var r = c.ExecuteReader(); return r.Read() ? new(r.GetString(0), r.GetString(1), P(r.GetString(2)), r.GetInt32(3), token, expires, r.GetInt32(4)) : null;
        });
    }

    public bool MarkDelivered(ReminderOccurrenceLease lease, DateTimeOffset at) => queue.Execute(u =>
    {
        using var c = Cmd(u, "UPDATE reminder_occurrences SET delivery_status='Delivered',completed_at_utc=$at,claim_token=NULL,claim_expires_at_utc=NULL,next_retry_at_utc=NULL WHERE occurrence_key=$key AND delivery_status='Claimed' AND claim_token=$token");
        c.Parameters.AddWithValue("$at", F(at)); c.Parameters.AddWithValue("$key", lease.OccurrenceKey); c.Parameters.AddWithValue("$token", lease.ClaimToken); return c.ExecuteNonQuery() == 1;
    });

    public ReminderFailureOutcome MarkFailed(ReminderOccurrenceLease lease, DateTimeOffset at, string error, int max)
    {
        var dead = lease.AttemptCount >= max; var retry = at.ToUniversalTime().AddSeconds(Math.Min(900, 5 * Math.Pow(2, Math.Clamp(lease.AttemptCount - 1, 0, 8))));
        return queue.Execute(u =>
        {
            using var c = Cmd(u, "UPDATE reminder_occurrences SET delivery_status=$status,next_retry_at_utc=$retry,last_error=$error,claim_token=NULL,claim_expires_at_utc=NULL WHERE occurrence_key=$key AND delivery_status='Claimed' AND claim_token=$token");
            c.Parameters.AddWithValue("$status", dead ? "DeadLetter" : "RetryWait"); c.Parameters.AddWithValue("$retry", dead ? DBNull.Value : F(retry));
            var message = error ?? "Unknown failure"; c.Parameters.AddWithValue("$error", message[..Math.Min(2000, message.Length)]);
            c.Parameters.AddWithValue("$key", lease.OccurrenceKey); c.Parameters.AddWithValue("$token", lease.ClaimToken);
            return c.ExecuteNonQuery() != 1 ? ReminderFailureOutcome.NotOwned : dead ? ReminderFailureOutcome.DeadLettered : ReminderFailureOutcome.RetryScheduled;
        });
    }

    public bool EnqueueNotification(NotificationOutboxEntry e) => queue.Execute(u =>
    {
        using var c = Cmd(u, "INSERT INTO notification_outbox(notification_idempotency_key,item_id,occurrence_key,action_type,target_delivery_at_utc,status,attempt_count,created_at_utc) VALUES($k,$i,$o,$a,$t,'Pending',0,$c) ON CONFLICT(notification_idempotency_key) WHERE deleted_at_utc IS NULL DO NOTHING");
        c.Parameters.AddWithValue("$k", e.IdempotencyKey); c.Parameters.AddWithValue("$i", e.ItemId); c.Parameters.AddWithValue("$o", e.OccurrenceKey); c.Parameters.AddWithValue("$a", e.ActionType);
        c.Parameters.AddWithValue("$t", F(e.TargetDeliveryAtUtc)); c.Parameters.AddWithValue("$c", F(DateTimeOffset.UtcNow)); return c.ExecuteNonQuery() == 1;
    });

    internal void Defer(ReminderPolicyInput i, string reason, DateTimeOffset now) => queue.Execute(u =>
    {
        using var c = Cmd(u, "INSERT INTO deferred_notifications(item_id,occurrence_key,original_due_at_utc,deferred_reason,priority,was_toast_suppressed,created_at_utc) VALUES($i,$o,$d,$r,$p,1,$c) ON CONFLICT(occurrence_key) WHERE released_at_utc IS NULL DO NOTHING");
        c.Parameters.AddWithValue("$i", i.ItemId); c.Parameters.AddWithValue("$o", i.OccurrenceKey); c.Parameters.AddWithValue("$d", F(i.OriginalDueAtUtc));
        c.Parameters.AddWithValue("$r", reason); c.Parameters.AddWithValue("$p", i.Priority.ToString()); c.Parameters.AddWithValue("$c", F(now)); c.ExecuteNonQuery();
    });

    internal DeferredNotificationSummary ReleaseDeferredSummary(DateTimeOffset now, string batch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batch);
        return queue.Execute<DeferredNotificationSummary>(u =>
        {
            var total = 0; var overdue = 0; var byPriority = new int[4];
            using (var c = Cmd(u, "SELECT priority,original_due_at_utc FROM deferred_notifications WHERE released_at_utc IS NULL"))
            using (var r = c.ExecuteReader()) while (r.Read()) { total++; byPriority[(int)Enum.Parse<ReminderPriority>(r.GetString(0))]++; if (P(r.GetString(1)) < now.ToUniversalTime()) overdue++; }
            using (var c = Cmd(u, "UPDATE deferred_notifications SET released_at_utc=$at,summary_batch_id=$b WHERE released_at_utc IS NULL"))
            { c.Parameters.AddWithValue("$at", F(now)); c.Parameters.AddWithValue("$b", batch); c.ExecuteNonQuery(); }
            return new(total, overdue, byPriority[0], byPriority[1], byPriority[2], byPriority[3], total == 0 ? null : batch);
        });
    }

    static SqliteCommand Cmd(IUnitOfWork u, string sql) { var c = u.Connection.CreateCommand(); c.Transaction = u.Transaction; c.CommandText = sql; return c; }
    static string F(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    static DateTimeOffset P(string value) => DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    const string Schema = """
      CREATE TABLE IF NOT EXISTS reminder_occurrences(occurrence_key TEXT PRIMARY KEY,item_id TEXT NOT NULL,rule_revision INTEGER NOT NULL CHECK(rule_revision>=1),due_at_utc TEXT NOT NULL,claimed_at_utc TEXT NULL,claim_token TEXT NULL,claim_expires_at_utc TEXT NULL,delivery_status TEXT NOT NULL CHECK(delivery_status IN('Pending','Claimed','RetryWait','Delivered','DeadLetter')),attempt_count INTEGER NOT NULL DEFAULT 0 CHECK(attempt_count>=0),next_retry_at_utc TEXT NULL,last_error TEXT NULL,created_at_utc TEXT NOT NULL,completed_at_utc TEXT NULL,CHECK(delivery_status!='Claimed' OR(claim_token IS NOT NULL AND claim_expires_at_utc IS NOT NULL)));
      CREATE TABLE IF NOT EXISTS notification_outbox(id INTEGER PRIMARY KEY AUTOINCREMENT,notification_idempotency_key TEXT NOT NULL,item_id TEXT NOT NULL,occurrence_key TEXT NOT NULL,action_type TEXT NOT NULL,target_delivery_at_utc TEXT NOT NULL,status TEXT NOT NULL CHECK(status IN('Pending','Claimed','Delivered','RetryWait','DeadLetter','Cancelled')),attempt_count INTEGER NOT NULL DEFAULT 0 CHECK(attempt_count>=0),next_retry_at_utc TEXT NULL,last_error TEXT NULL,created_at_utc TEXT NOT NULL,deleted_at_utc TEXT NULL);
      CREATE UNIQUE INDEX IF NOT EXISTS ux_notification_outbox_active_key ON notification_outbox(notification_idempotency_key) WHERE deleted_at_utc IS NULL;
      CREATE TABLE IF NOT EXISTS notification_deliveries(id INTEGER PRIMARY KEY AUTOINCREMENT,outbox_id INTEGER NOT NULL,delivery_target TEXT NOT NULL,delivered_at_utc TEXT NULL,status TEXT NOT NULL,error TEXT NULL,FOREIGN KEY(outbox_id) REFERENCES notification_outbox(id));
      CREATE TABLE IF NOT EXISTS occurrence_overrides(id INTEGER PRIMARY KEY AUTOINCREMENT,series_item_id TEXT NOT NULL,rule_version INTEGER NOT NULL CHECK(rule_version>=1),original_start_local_datetime TEXT NOT NULL,original_start_utc TEXT NOT NULL,iana_time_zone_id TEXT NOT NULL,override_type TEXT NOT NULL,new_remind_at_utc TEXT NULL,new_start_at_utc TEXT NULL,created_at_utc TEXT NOT NULL,deleted_at_utc TEXT NULL);
      CREATE UNIQUE INDEX IF NOT EXISTS ux_occurrence_override_active_key ON occurrence_overrides(series_item_id,rule_version,original_start_local_datetime,original_start_utc,iana_time_zone_id) WHERE deleted_at_utc IS NULL;
      CREATE TABLE IF NOT EXISTS deferred_notifications(id INTEGER PRIMARY KEY AUTOINCREMENT,item_id TEXT NOT NULL,occurrence_key TEXT NOT NULL,original_due_at_utc TEXT NOT NULL,deferred_reason TEXT NOT NULL,priority TEXT NOT NULL CHECK(priority IN('Low','Normal','High','Urgent')),deferred_until_utc TEXT NULL,was_toast_suppressed INTEGER NOT NULL CHECK(was_toast_suppressed IN(0,1)),summary_batch_id TEXT NULL,created_at_utc TEXT NOT NULL,released_at_utc TEXT NULL);
      CREATE UNIQUE INDEX IF NOT EXISTS ux_deferred_notification_active_occurrence ON deferred_notifications(occurrence_key) WHERE released_at_utc IS NULL;
      """;
}

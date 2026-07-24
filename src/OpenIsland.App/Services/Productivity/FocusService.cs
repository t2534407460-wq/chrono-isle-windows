using System.Globalization;
using OpenIsland.App.Services.Persistence;

namespace OpenIsland.App.Services.Productivity;

public sealed record FocusSession(string Id, string ItemId, DateTimeOffset StartedAtUtc, int IntendedMinutes,
    DateTimeOffset? EndedAtUtc = null, int? ActualMinutes = null,
    DateTimeOffset? PausedAtUtc = null, int AccumulatedPausedSeconds = 0)
{
    public bool IsPaused => PausedAtUtc is not null;
}
public sealed record FocusCompletion(FocusSession Session, bool ShouldAskToCompleteItem);

public interface IFocusService
{
    FocusSession Start(string todoId, int intendedMinutes);
    FocusSession? RestoreActive();
    FocusSession Pause(string sessionId);
    FocusSession Resume(string sessionId);
    FocusCompletion End(string sessionId);
}

public sealed class FocusService : IFocusService
{
    readonly IDbWriteQueue writeQueue;
    readonly SqliteConnectionFactory connections;
    readonly Func<DateTimeOffset> clock;

    public FocusService(IDbWriteQueue writeQueue, SqliteConnectionFactory connections, Func<DateTimeOffset>? clock = null)
    {
        this.writeQueue = writeQueue ?? throw new ArgumentNullException(nameof(writeQueue));
        this.connections = connections ?? throw new ArgumentNullException(nameof(connections));
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public FocusSession Start(string todoId, int intendedMinutes)
    {
        if (string.IsNullOrWhiteSpace(todoId)) throw new ArgumentException("A todo id is required.", nameof(todoId));
        if (intendedMinutes <= 0) throw new ArgumentOutOfRangeException(nameof(intendedMinutes));
        var session = new FocusSession(Guid.NewGuid().ToString("N"), todoId, clock(), intendedMinutes);
        return writeQueue.Execute(uow =>
        {
            using var active = uow.Connection.CreateCommand();
            active.Transaction = uow.Transaction;
            active.CommandText = "SELECT id,item_id,started_at_utc,intended_minutes,paused_at_utc,accumulated_paused_seconds FROM focus_sessions WHERE ended_at_utc IS NULL LIMIT 1";
            using var reader = active.ExecuteReader();
            if (reader.Read())
            {
                var existing = new FocusSession(reader.GetString(0), reader.GetString(1), Parse(reader.GetString(2)), reader.GetInt32(3),
                    null, null, reader.IsDBNull(4) ? null : Parse(reader.GetString(4)), reader.GetInt32(5));
                if (existing.ItemId == todoId) return existing;
                throw new InvalidOperationException("已有进行中的专注，请先结束当前专注后再开始新的专注。");
            }
            reader.Close();
            using var validate = uow.Connection.CreateCommand();
            validate.Transaction = uow.Transaction;
            validate.CommandText = "SELECT COUNT(*) FROM life_items WHERE id=$id AND kind='Todo' AND is_readonly=0 AND deleted_at IS NULL AND status NOT IN ('Completed','Cancelled')";
            validate.Parameters.AddWithValue("$id", todoId);
            if (Convert.ToInt64(validate.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                throw new InvalidOperationException("Focus can only bind to an editable, unfinished todo.");
            using var insert = uow.Connection.CreateCommand();
            insert.Transaction = uow.Transaction;
            insert.CommandText = "INSERT INTO focus_sessions(id,item_id,started_at_utc,intended_minutes) VALUES($id,$item,$started,$minutes)";
            insert.Parameters.AddWithValue("$id", session.Id); insert.Parameters.AddWithValue("$item", todoId);
            insert.Parameters.AddWithValue("$started", Iso(session.StartedAtUtc)); insert.Parameters.AddWithValue("$minutes", intendedMinutes);
            insert.ExecuteNonQuery();
            return session;
        });
    }

    public FocusSession? RestoreActive()
    {
        using var db = connections.OpenConnection();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT id,item_id,started_at_utc,intended_minutes,paused_at_utc,accumulated_paused_seconds FROM focus_sessions WHERE ended_at_utc IS NULL ORDER BY started_at_utc LIMIT 1";
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new(reader.GetString(0), reader.GetString(1), Parse(reader.GetString(2)), reader.GetInt32(3),
                null, null, reader.IsDBNull(4) ? null : Parse(reader.GetString(4)), reader.GetInt32(5))
            : null;
    }

    public FocusSession Pause(string sessionId)
    {
        var now = clock();
        return writeQueue.Execute(uow =>
        {
            var session = ReadActive(uow, sessionId);
            if (session.PausedAtUtc is not null)
                throw new InvalidOperationException("Focus session is already paused.");
            using var update = uow.Connection.CreateCommand();
            update.Transaction = uow.Transaction;
            update.CommandText = "UPDATE focus_sessions SET paused_at_utc=$paused WHERE id=$id AND ended_at_utc IS NULL AND paused_at_utc IS NULL";
            update.Parameters.AddWithValue("$paused", Iso(now));
            update.Parameters.AddWithValue("$id", sessionId);
            if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("Focus session changed concurrently.");
            return session with { PausedAtUtc = now };
        });
    }

    public FocusSession Resume(string sessionId)
    {
        var now = clock();
        return writeQueue.Execute(uow =>
        {
            var session = ReadActive(uow, sessionId);
            if (session.PausedAtUtc is null)
                throw new InvalidOperationException("Focus session is not paused.");
            var pausedSeconds = Math.Max(0, (int)Math.Floor((now - session.PausedAtUtc.Value).TotalSeconds));
            using var update = uow.Connection.CreateCommand();
            update.Transaction = uow.Transaction;
            update.CommandText = "UPDATE focus_sessions SET paused_at_utc=NULL,accumulated_paused_seconds=accumulated_paused_seconds+$seconds WHERE id=$id AND ended_at_utc IS NULL AND paused_at_utc IS NOT NULL";
            update.Parameters.AddWithValue("$seconds", pausedSeconds);
            update.Parameters.AddWithValue("$id", sessionId);
            if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("Focus session changed concurrently.");
            return session with { PausedAtUtc = null, AccumulatedPausedSeconds = session.AccumulatedPausedSeconds + pausedSeconds };
        });
    }

    public FocusCompletion End(string sessionId)
    {
        var now = clock();
        return writeQueue.Execute(uow =>
        {
            using var read = uow.Connection.CreateCommand();
            read.Transaction = uow.Transaction;
            read.CommandText = "SELECT item_id,started_at_utc,intended_minutes,paused_at_utc,accumulated_paused_seconds FROM focus_sessions WHERE id=$id AND ended_at_utc IS NULL";
            read.Parameters.AddWithValue("$id", sessionId);
            using var reader = read.ExecuteReader();
            if (!reader.Read()) throw new InvalidOperationException("Active focus session not found.");
            var itemId = reader.GetString(0); var started = Parse(reader.GetString(1)); var intended = reader.GetInt32(2);
            var paused = reader.IsDBNull(3) ? (DateTimeOffset?)null : Parse(reader.GetString(3)); var accumulated = reader.GetInt32(4); reader.Close();
            if (now < started) throw new InvalidOperationException("Focus end cannot precede its start.");
            var stoppedAt = paused ?? now;
            var actual = Math.Max(0, (int)Math.Ceiling((stoppedAt - started).TotalMinutes - accumulated / 60d));
            using var update = uow.Connection.CreateCommand();
            update.Transaction = uow.Transaction;
            update.CommandText = "UPDATE focus_sessions SET ended_at_utc=$ended,actual_minutes=$actual,paused_at_utc=NULL WHERE id=$id AND ended_at_utc IS NULL";
            update.Parameters.AddWithValue("$ended", Iso(now)); update.Parameters.AddWithValue("$actual", actual); update.Parameters.AddWithValue("$id", sessionId);
            if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("Focus session changed concurrently.");
            using var item = uow.Connection.CreateCommand();
            item.Transaction = uow.Transaction;
            item.CommandText = "SELECT status FROM life_items WHERE id=$id AND deleted_at IS NULL";
            item.Parameters.AddWithValue("$id", itemId);
            var status = Convert.ToString(item.ExecuteScalar(), CultureInfo.InvariantCulture);
            return new FocusCompletion(new(sessionId, itemId, started, intended, now, actual, null, accumulated), status is "Pending" or "InProgress" or "Deferred");
        });
    }

    FocusSession ReadActive(IUnitOfWork uow, string sessionId)
    {
        using var command = uow.Connection.CreateCommand();
        command.Transaction = uow.Transaction;
        command.CommandText = "SELECT item_id,started_at_utc,intended_minutes,paused_at_utc,accumulated_paused_seconds FROM focus_sessions WHERE id=$id AND ended_at_utc IS NULL";
        command.Parameters.AddWithValue("$id", sessionId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("Active focus session not found.");
        return new FocusSession(sessionId, reader.GetString(0), Parse(reader.GetString(1)), reader.GetInt32(2),
            null, null, reader.IsDBNull(3) ? null : Parse(reader.GetString(3)), reader.GetInt32(4));
    }

    static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

using System.Globalization;
using Microsoft.Data.Sqlite;
using OpenIsland.App;
using OpenIsland.App.Services;
using OpenIsland.App.Services.Domain;
using OpenIsland.App.Services.Persistence;
using OpenIsland.App.Services.Productivity;

namespace OpenIsland.App.Services.State;

public sealed record TodayDashboardItem(
    string Id,
    LifeItemKind Kind,
    string Title,
    long RowVersion,
    DateTimeOffset? ScheduledAtUtc,
    LifePriority Priority,
    bool IsReadOnly);

public sealed record TodayDashboardSnapshot(
    DateTimeOffset GeneratedAtUtc,
    TodayDashboardItem? NextAction,
    IReadOnlyList<TodayDashboardItem> Today,
    IReadOnlyList<TodayDashboardItem> Overdue,
    IReadOnlyList<TodayDashboardItem> Inbox,
    IReadOnlyList<string> SuggestedItemIds);

public enum InboxMutationResult
{
    Succeeded,
    NotFound,
    ConcurrentConflict,
    NotInboxItem,
    ReadOnly
}

public sealed class TodayDashboardService
{
    readonly SqliteConnectionFactory connections;
    readonly IDbWriteQueue writeQueue;
    readonly Func<DateTime, AgendaItem?>? nextLongTermReminder;

    public TodayDashboardService(SqliteConnectionFactory connections, IDbWriteQueue writeQueue)
    {
        this.connections = connections ?? throw new ArgumentNullException(nameof(connections));
        this.writeQueue = writeQueue ?? throw new ArgumentNullException(nameof(writeQueue));
    }

    public TodayDashboardService(LifeDataService data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var runtime = LifeDataStoreRuntimeRegistry.GetOrCreate(data.DatabasePath);
        connections = runtime.ConnectionFactory;
        writeQueue = runtime.WriteQueue;
        nextLongTermReminder = data.NextLongTermReminder;
    }

    public TodayDashboardService(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var runtime = LifeDataStoreRuntimeRegistry.GetOrCreate(databasePath);
        connections = runtime.ConnectionFactory;
        writeQueue = runtime.WriteQueue;
    }

    public TodayDashboardSnapshot GetSnapshot(DateTimeOffset now)
    {
        var nowUtc = now.ToUniversalTime();
        var localDate = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local).Date;
        var active = ReadActiveItems();
        var today = active.Where(item => item.ScheduledAtUtc is { } at &&
                TimeZoneInfo.ConvertTime(at, TimeZoneInfo.Local).Date == localDate)
            .OrderBy(item => item.ScheduledAtUtc).ThenBy(item => item.Title, StringComparer.CurrentCulture)
            .ToArray();
        var overdue = active.Where(item => item.Kind is LifeItemKind.Todo or LifeItemKind.Reminder &&
                                           item.ScheduledAtUtc < nowUtc.Subtract(TimeSpan.FromMinutes(1)))
            .OrderBy(item => item.ScheduledAtUtc).ThenByDescending(item => item.Priority)
            .ToArray();
        var inbox = active.Where(item => item.Kind == LifeItemKind.Todo && item.ScheduledAtUtc is null)
            .OrderByDescending(item => item.Priority).ThenBy(item => item.Title, StringComparer.CurrentCulture)
            .ToArray();
        var nextCandidates = active.Where(item => item.ScheduledAtUtc >= nowUtc);
        var localNow = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local).DateTime;
        if (nextLongTermReminder?.Invoke(localNow) is { } recurring)
            nextCandidates = nextCandidates.Append(FromRecurringOccurrence(recurring, active));
        var next = nextCandidates
            .OrderBy(item => item.ScheduledAtUtc).ThenByDescending(item => item.Priority).FirstOrDefault();
        var ranked = LocalTaskRanker.Rank(
                active.Where(item => item.Kind == LifeItemKind.Todo)
                    .Select(item => new TaskRankCandidate(item.Id, item.ScheduledAtUtc, item.Priority.ToString(), null)),
                nowUtc)
            .Take(3).Select(item => item.Id).ToArray();
        return new(nowUtc, next, today, overdue, inbox, ranked);
    }

    public InboxMutationResult ScheduleInbox(string id, long expectedRowVersion, TemporalValue due)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ValidateTemporal(due);
        return writeQueue.Execute(uow =>
        {
            var state = ReadMutationState(uow, id);
            if (state is null) return InboxMutationResult.NotFound;
            if (state.Value.IsReadOnly) return InboxMutationResult.ReadOnly;
            if (state.Value.RowVersion != expectedRowVersion) return InboxMutationResult.ConcurrentConflict;
            if (!state.Value.IsInbox) return InboxMutationResult.NotInboxItem;

            using var update = Command(uow, """
                UPDATE life_items SET
                  due_local_datetime=$local,due_utc_instant=$utc,
                  due_iana_time_zone_id=$iana,due_windows_time_zone_id_cache=$windows,
                  due_time_semantics=$semantics,row_version=row_version+1,updated_at=$updated
                WHERE id=$id AND row_version=$expected AND deleted_at IS NULL
                """);
            update.Parameters.AddWithValue("$local", Db(due.LocalDateTime?.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture)));
            update.Parameters.AddWithValue("$utc", Db(due.UtcInstant?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)));
            update.Parameters.AddWithValue("$iana", Db(due.IanaTimeZoneId));
            update.Parameters.AddWithValue("$windows", Db(due.WindowsTimeZoneIdCache));
            update.Parameters.AddWithValue("$semantics", due.Semantics.ToString());
            update.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$expected", expectedRowVersion);
            return update.ExecuteNonQuery() == 1
                ? InboxMutationResult.Succeeded
                : InboxMutationResult.ConcurrentConflict;
        });
    }

    public InboxMutationResult RestoreDeletedItem(string id, long expectedRowVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return writeQueue.Execute(uow =>
        {
            using var update = Command(uow, """
                UPDATE life_items SET
                  deleted_at=NULL,
                  status=CASE WHEN status IN ('Cancelled','Ignored') THEN 'Pending' ELSE status END,
                  row_version=row_version+1,
                  updated_at=$now
                WHERE id=$id AND row_version=$expected AND deleted_at IS NOT NULL
                """);
            update.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$expected", expectedRowVersion);
            return update.ExecuteNonQuery() == 1
                ? InboxMutationResult.Succeeded
                : InboxMutationResult.ConcurrentConflict;
        });
    }
    public InboxMutationResult Ignore(string id, long expectedRowVersion) =>
        MutateInbox(id, expectedRowVersion, "status='Ignored'");

    public InboxMutationResult DeleteLocalCopy(string id, long expectedRowVersion) =>
        MutateInbox(id, expectedRowVersion, "deleted_at=$now");

    InboxMutationResult MutateInbox(string id, long expectedRowVersion, string assignment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return writeQueue.Execute(uow =>
        {
            var state = ReadMutationState(uow, id);
            if (state is null) return InboxMutationResult.NotFound;
            if (state.Value.RowVersion != expectedRowVersion) return InboxMutationResult.ConcurrentConflict;
            if (assignment.StartsWith("status", StringComparison.Ordinal) && state.Value.IsReadOnly)
                return InboxMutationResult.ReadOnly;
            if (!state.Value.IsInbox) return InboxMutationResult.NotInboxItem;
            using var update = Command(uow, $"""
                UPDATE life_items SET {assignment},row_version=row_version+1,updated_at=$now
                WHERE id=$id AND row_version=$expected AND deleted_at IS NULL
                """);
            update.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$expected", expectedRowVersion);
            return update.ExecuteNonQuery() == 1
                ? InboxMutationResult.Succeeded
                : InboxMutationResult.ConcurrentConflict;
        });
    }

    IReadOnlyList<TodayDashboardItem> ReadActiveItems()
    {
        using var connection = connections.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,kind,title,row_version,due_utc_instant,remind_utc_instant,start_utc_instant,
                   COALESCE(priority,'Normal'),is_readonly
            FROM life_items
            WHERE deleted_at IS NULL AND status NOT IN ('Completed','Cancelled','Ignored')
            """;
        using var reader = command.ExecuteReader();
        var items = new List<TodayDashboardItem>();
        while (reader.Read())
        {
            var kind = Enum.Parse<LifeItemKind>(reader.GetString(1));
            var scheduled = kind switch
            {
                LifeItemKind.Event => Parse(reader, 6),
                LifeItemKind.Reminder => Parse(reader, 5),
                _ => Parse(reader, 4) ?? Parse(reader, 5)
            };
            items.Add(new(reader.GetString(0), kind, reader.GetString(2), reader.GetInt64(3), scheduled,
                Enum.TryParse<LifePriority>(reader.GetString(7), out var priority) ? priority : LifePriority.Normal,
                reader.GetInt64(8) != 0));
        }
        return items;
    }

    static TodayDashboardItem FromRecurringOccurrence(AgendaItem item, IReadOnlyList<TodayDashboardItem> active)
    {
        var stored = active.FirstOrDefault(candidate => candidate.Id == item.Id);
        var scheduled = new DateTimeOffset(DateTime.SpecifyKind(item.StartsAt, DateTimeKind.Local)).ToUniversalTime();
        return new(item.Id, LifeItemKind.Reminder, item.Title, stored?.RowVersion ?? 0, scheduled,
            stored?.Priority ?? LifePriority.Normal, stored?.IsReadOnly ?? false);
    }

    static (bool IsInbox, bool IsReadOnly, long RowVersion)? ReadMutationState(IUnitOfWork uow, string id)
    {
        using var command = Command(uow, """
            SELECT kind,due_utc_instant,remind_utc_instant,start_utc_instant,end_utc_instant,is_readonly,row_version
            FROM life_items WHERE id=$id AND deleted_at IS NULL
            """);
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var inbox = reader.GetString(0) == "Todo" && reader.IsDBNull(1) && reader.IsDBNull(2) &&
                    reader.IsDBNull(3) && reader.IsDBNull(4);
        return (inbox, reader.GetInt64(5) != 0, reader.GetInt64(6));
    }

    static SqliteCommand Command(IUnitOfWork uow, string sql)
    {
        var command = uow.Connection.CreateCommand();
        command.Transaction = uow.Transaction;
        command.CommandText = sql;
        return command;
    }

    static void ValidateTemporal(TemporalValue value)
    {
        var valid = value.Semantics switch
        {
            TimeSemantics.AbsoluteInstant => value.UtcInstant is not null && value.LocalDateTime is null &&
                value.IanaTimeZoneId is null && value.WindowsTimeZoneIdCache is null,
            TimeSemantics.ZonedWallClock => value.LocalDateTime is not null && value.UtcInstant is not null &&
                !string.IsNullOrWhiteSpace(value.IanaTimeZoneId),
            TimeSemantics.DeviceLocalFloatingWallClock => value.LocalDateTime is not null && value.UtcInstant is not null &&
                value.IanaTimeZoneId is null && value.WindowsTimeZoneIdCache is null,
            _ => false
        };
        if (!valid) throw new ArgumentException("The due TemporalValue is unresolved or internally inconsistent.", nameof(value));
        if (value.LocalDateTime?.Kind == DateTimeKind.Utc)
            throw new ArgumentException("Wall-clock local time must not be DateTimeKind.Utc.", nameof(value));
    }


    static DateTimeOffset? Parse(SqliteDataReader reader, int index) => reader.IsDBNull(index)
        ? null
        : DateTimeOffset.Parse(reader.GetString(index), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    static object Db(object? value) => value ?? DBNull.Value;
}

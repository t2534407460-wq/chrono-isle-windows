using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenIsland.App.Services.Persistence;

namespace OpenIsland.App.Services.Reporting;

public enum ReportPeriodKind { Daily, Weekly, Monthly }

public sealed record ReportPeriod(ReportPeriodKind Kind, DateTimeOffset StartUtc, DateTimeOffset EndUtc)
{
    public ReportPeriod Normalize()
    {
        var start = StartUtc.ToUniversalTime();
        var end = EndUtc.ToUniversalTime();
        if (end <= start) throw new ArgumentException("Report period end must be later than its start.");
        return this with { StartUtc = start, EndUtc = end };
    }
}

public sealed record ReportFacts(
    int CreatedCount, int CompletedCount, int OverdueCount, int DeferredCount,
    int HighPriorityCount, string? BusiestDay, int BusiestDayItemCount,
    int SuppressedNotificationCount);

public sealed record ReportSnapshot(
    string Id, ReportPeriod Period, string QueryVersion, ReportFacts Facts,
    DateTimeOffset CreatedAtUtc);

/// <summary>No titles, notes, item IDs or raw audit content may be added to this DTO.</summary>
public sealed record MinimalReportStatisticsDto(
    string PeriodKind, DateTimeOffset PeriodStartUtc, DateTimeOffset PeriodEndUtc,
    int CreatedCount, int CompletedCount, int OverdueCount, int DeferredCount,
    int HighPriorityCount, string? BusiestDay, int BusiestDayItemCount,
    int SuppressedNotificationCount);

public interface IReportService
{
    ReportSnapshot Generate(ReportPeriod period, string queryVersion);
    MinimalReportStatisticsDto? ExportMinimalStatisticsForAi(ReportSnapshot snapshot, bool aiReportsEnabled = false);
    IReadOnlyList<ReportSnapshot> ListSnapshots(ReportPeriodKind kind, int maximum = 12);
}

public sealed class ReportService : IReportService
{
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    readonly IDbWriteQueue writeQueue;
    readonly Func<DateTimeOffset> utcNow;

    public ReportService(IDbWriteQueue writeQueue, Func<DateTimeOffset>? utcNow = null)
    {
        this.writeQueue = writeQueue ?? throw new ArgumentNullException(nameof(writeQueue));
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        ReportSchema.EnsureCreated(writeQueue);
    }

    public ReportSnapshot Generate(ReportPeriod period, string queryVersion)
    {
        period = period.Normalize();
        ArgumentException.ThrowIfNullOrWhiteSpace(queryVersion);
        if (queryVersion.Length > 64) throw new ArgumentOutOfRangeException(nameof(queryVersion));

        return writeQueue.Execute(unitOfWork =>
        {
            var existing = ReadExisting(unitOfWork, period, queryVersion);
            if (existing is not null) return existing;
            var snapshot = new ReportSnapshot(Guid.NewGuid().ToString("N"), period, queryVersion,
                BuildFacts(unitOfWork, period), utcNow().ToUniversalTime());
            Insert(unitOfWork, snapshot);
            return snapshot;
        });
    }

    public MinimalReportStatisticsDto? ExportMinimalStatisticsForAi(ReportSnapshot snapshot, bool aiReportsEnabled = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!aiReportsEnabled) return null;
        var f = snapshot.Facts;
        return new(snapshot.Period.Kind.ToString(), snapshot.Period.StartUtc, snapshot.Period.EndUtc,
            f.CreatedCount, f.CompletedCount, f.OverdueCount, f.DeferredCount,
            f.HighPriorityCount, f.BusiestDay, f.BusiestDayItemCount, f.SuppressedNotificationCount);
    }
    public IReadOnlyList<ReportSnapshot> ListSnapshots(ReportPeriodKind kind, int maximum = 12)
    {
        if (maximum is < 1 or > 120) throw new ArgumentOutOfRangeException(nameof(maximum));
        return writeQueue.Execute<IReadOnlyList<ReportSnapshot>>(unitOfWork =>
        {
            using var command = Command(unitOfWork, """
                SELECT id,period_start_utc,period_end_utc,query_version,facts_json,created_at_utc
                FROM report_snapshots WHERE period_kind=$kind
                ORDER BY period_end_utc DESC,created_at_utc DESC LIMIT $maximum
                """);
            command.Parameters.AddWithValue("$kind", kind.ToString());
            command.Parameters.AddWithValue("$maximum", maximum);
            using var reader = command.ExecuteReader();
            var snapshots = new List<ReportSnapshot>();
            while (reader.Read())
            {
                var period = new ReportPeriod(kind, Parse(reader.GetString(1)), Parse(reader.GetString(2)));
                var facts = JsonSerializer.Deserialize<ReportFacts>(reader.GetString(4), JsonOptions)
                    ?? throw new InvalidDataException("Stored report facts are invalid.");
                snapshots.Add(new ReportSnapshot(reader.GetString(0), period, reader.GetString(3), facts, Parse(reader.GetString(5))));
            }
            return snapshots;
        });
    }


    static ReportSnapshot? ReadExisting(IUnitOfWork u, ReportPeriod period, string version)
    {
        using var command = Command(u, """
            SELECT id,facts_json,created_at_utc FROM report_snapshots
            WHERE period_kind=$kind AND period_start_utc=$start AND period_end_utc=$end AND query_version=$version
            """);
        AddPeriod(command, period, version);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var facts = JsonSerializer.Deserialize<ReportFacts>(reader.GetString(1), JsonOptions)
            ?? throw new InvalidDataException("Stored report facts are invalid.");
        return new(reader.GetString(0), period, version, facts, Parse(reader.GetString(2)));
    }

    static ReportFacts BuildFacts(IUnitOfWork u, ReportPeriod period)
    {
        if (!TableExists(u, "life_items"))
            return new(0, 0, 0, 0, 0, null, 0, CountSuppressed(u, period));

        var columns = Columns(u, "life_items");
        var completedAt = columns.Contains("completed_at_utc") ? "completed_at_utc" : "updated_at";
        var priority = columns.Contains("priority") ? "priority" : "NULL";
        var overdueGrace = columns.Contains("overdue_grace_minutes") ? "COALESCE(overdue_grace_minutes,5)" : "5";
        var deferredExpression = columns.Contains("postponement_count")
            ? "CASE WHEN status='Deferred' OR postponement_count>0 THEN 1 ELSE 0 END"
            : "CASE WHEN status='Deferred' THEN 1 ELSE 0 END";
        var activityAt = columns.Contains("start_utc_instant")
            ? "COALESCE(start_utc_instant,due_utc_instant,remind_utc_instant,created_at)" : "created_at";

        var created = Scalar(u, "SELECT COUNT(*) FROM life_items WHERE deleted_at IS NULL AND created_at >= $start AND created_at < $end", period);
        var completed = Scalar(u, $"SELECT COUNT(*) FROM life_items WHERE deleted_at IS NULL AND status='Completed' AND {completedAt} >= $start AND {completedAt} < $end", period);
        var overdue = Scalar(u, $"SELECT COUNT(*) FROM life_items WHERE deleted_at IS NULL AND status NOT IN ('Completed','Cancelled','Ignored') AND due_utc_instant IS NOT NULL AND julianday(due_utc_instant) + {overdueGrace} / 1440.0 < julianday($end)", period);
        var deferred = Scalar(u, $"SELECT COALESCE(SUM({deferredExpression}),0) FROM life_items WHERE deleted_at IS NULL AND updated_at >= $start AND updated_at < $end", period);
        var high = Scalar(u, $"SELECT COUNT(*) FROM life_items WHERE deleted_at IS NULL AND {priority} IN ('High','Urgent') AND updated_at >= $start AND updated_at < $end", period);

        string? busiestDay = null;
        var busiestCount = 0;
        using (var command = Command(u, $"""
            SELECT substr({activityAt},1,10),COUNT(*) FROM life_items
            WHERE deleted_at IS NULL AND {activityAt} >= $start AND {activityAt} < $end
            GROUP BY substr({activityAt},1,10) ORDER BY COUNT(*) DESC,substr({activityAt},1,10) ASC LIMIT 1
            """))
        {
            AddBounds(command, period);
            using var reader = command.ExecuteReader();
            if (reader.Read()) { busiestDay = reader.IsDBNull(0) ? null : reader.GetString(0); busiestCount = reader.GetInt32(1); }
        }
        return new(created, completed, overdue, deferred, high, busiestDay, busiestCount, CountSuppressed(u, period));
    }

    static int CountSuppressed(IUnitOfWork u, ReportPeriod period) =>
        !TableExists(u, "deferred_notifications") ? 0 :
        Scalar(u, "SELECT COUNT(*) FROM deferred_notifications WHERE created_at_utc >= $start AND created_at_utc < $end", period);

    static void Insert(IUnitOfWork u, ReportSnapshot snapshot)
    {
        using var command = Command(u, "INSERT INTO report_snapshots(id,period_kind,period_start_utc,period_end_utc,query_version,facts_json,created_at_utc) VALUES($id,$kind,$start,$end,$version,$facts,$created)");
        command.Parameters.AddWithValue("$id", snapshot.Id); AddPeriod(command, snapshot.Period, snapshot.QueryVersion);
        command.Parameters.AddWithValue("$facts", JsonSerializer.Serialize(snapshot.Facts, JsonOptions));
        command.Parameters.AddWithValue("$created", Format(snapshot.CreatedAtUtc)); command.ExecuteNonQuery();
    }

    static int Scalar(IUnitOfWork u, string sql, ReportPeriod period)
    { using var command = Command(u, sql); AddBounds(command, period); return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture); }
    static bool TableExists(IUnitOfWork u, string table)
    { using var command = Command(u, "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name)"); command.Parameters.AddWithValue("$name", table); return Convert.ToInt64(command.ExecuteScalar()) == 1; }
    static HashSet<string> Columns(IUnitOfWork u, string table)
    { using var command = Command(u, $"PRAGMA table_info({table})"); using var reader = command.ExecuteReader(); var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase); while (reader.Read()) result.Add(reader.GetString(1)); return result; }
    static SqliteCommand Command(IUnitOfWork u, string sql)
    { var command = u.Connection.CreateCommand(); command.Transaction = u.Transaction; command.CommandText = sql; return command; }
    static void AddBounds(SqliteCommand command, ReportPeriod period)
    { command.Parameters.AddWithValue("$start", Format(period.StartUtc)); command.Parameters.AddWithValue("$end", Format(period.EndUtc)); }
    static void AddPeriod(SqliteCommand command, ReportPeriod period, string version)
    { command.Parameters.AddWithValue("$kind", period.Kind.ToString()); AddBounds(command, period); command.Parameters.AddWithValue("$version", version); }
    static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    static DateTimeOffset Parse(string value) => DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

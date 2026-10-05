using System.Globalization;
using System.Text.Json;
using ChronoIsle.App.Services.Persistence;

namespace ChronoIsle.App.Services.Reporting;

public sealed record HistoryRecord(string Id, string Title, string Kind, string Status, string Category,
    string Source, DateTimeOffset? CreatedAt, DateTimeOffset? CompletedAt, DateTimeOffset? ScheduledAt,
    bool Archived, long Revision)
{
    public string KindLabel => Kind switch { "Todo" or "todo" => "待办", "Event" or "event" => "日程", "LongTerm" => "长期事项", _ => "提醒" };
    public string StatusLabel => Status switch { "Completed" => "已完成", "Pending" => "待处理", "InProgress" => "进行中", "Deferred" => "已延期", "Cancelled" => "已取消", "Ignored" => "已忽略", _ => Status };
    public string CreatedText => CreatedAt?.ToString("yyyy-MM-dd HH:mm") ?? "未记录";
    public string CompletedText => CompletedAt?.ToString("yyyy-MM-dd HH:mm") ?? "未记录";
    public string ScheduledText => ScheduledAt?.ToString("yyyy-MM-dd HH:mm") ?? "未安排";
}

public sealed record HistoryDay(string Date, int Created, int Completed);
public sealed record HistoryReport(string QueryVersion, DateTimeOffset GeneratedAt, string TimeZoneId,
    string? StartDate, string? EndDate, long Watermark, IReadOnlyList<HistoryRecord> Records,
    IReadOnlyList<HistoryDay> Days)
{
    public int Completed => Records.Count(r => r.Status == "Completed");
    public int Unfinished => Records.Count(r => r.Status is "Pending" or "InProgress" or "Deferred");
}

/// <summary>Retains reporting facts independently of the short-lived archive. Never infers completion from a notification.</summary>
public sealed class HistoryReportService
{
    readonly IDbWriteQueue queue;
    public HistoryReportService(IDbWriteQueue queue) { this.queue = queue; EnsureCreated(queue); }

    public static void EnsureCreated(IDbWriteQueue queue) => queue.Execute(u =>
    {
        using var c = u.Connection.CreateCommand(); c.Transaction = u.Transaction;
        c.CommandText = """
            CREATE TABLE IF NOT EXISTS history_item_events(
                sequence INTEGER PRIMARY KEY AUTOINCREMENT, item_id TEXT NOT NULL,
                title TEXT NOT NULL, kind TEXT NOT NULL, status TEXT NOT NULL, category TEXT,
                source TEXT NOT NULL, created_at TEXT, completed_at TEXT, scheduled_at TEXT,
                archived INTEGER NOT NULL, revision INTEGER NOT NULL, captured_at TEXT NOT NULL,
                capture_kind TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_history_item_events_item ON history_item_events(item_id,sequence);
            CREATE TABLE IF NOT EXISTS history_report_snapshots(
                id TEXT PRIMARY KEY,created_at TEXT NOT NULL,payload TEXT NOT NULL CHECK(json_valid(payload)));
            """;
        c.ExecuteNonQuery();
        const string fields = "item_id,title,kind,status,category,source,created_at,completed_at,scheduled_at,archived,revision,captured_at,capture_kind";
        static string Values(string p, string kind) => $"{p}.id,{p}.title,COALESCE({p}.item_type,{p}.kind),{p}.status,{p}.category,{p}.origin_type,{p}.created_at,{p}.completed_at_utc,COALESCE({p}.start_utc_instant,{p}.due_utc_instant,{p}.remind_utc_instant),({p}.deleted_at IS NOT NULL),{p}.row_version,strftime('%Y-%m-%dT%H:%M:%fZ','now'),'{kind}'";
        c.CommandText = $"INSERT INTO history_item_events({fields}) SELECT {Values("i", "Baseline")} FROM life_items i WHERE NOT EXISTS(SELECT 1 FROM history_item_events h WHERE h.item_id=i.id);";
        c.ExecuteNonQuery();
        foreach (var (operation, prefix) in new[] { ("INSERT", "NEW"), ("UPDATE", "NEW"), ("DELETE", "OLD") })
        {
            c.CommandText = $"CREATE TRIGGER IF NOT EXISTS history_life_{operation.ToLowerInvariant()} AFTER {operation} ON life_items BEGIN INSERT INTO history_item_events({fields}) VALUES({Values(prefix, operation)}); END;";
            c.ExecuteNonQuery();
        }
        // Older archives can outlive their canonical row. Their archive date is not a completion date.
        c.CommandText = $"""
            INSERT INTO history_item_events({fields})
            SELECT a.id,a.title,a.kind,CASE WHEN a.reason='已完成' THEN 'Completed' ELSE a.reason END,
                NULL,'Local',NULL,NULL,a.due_at,1,1,strftime('%Y-%m-%dT%H:%M:%fZ','now'),'LegacyArchive'
            FROM archived_todos a WHERE NOT EXISTS(SELECT 1 FROM history_item_events h WHERE h.item_id=a.id);
            """;
        c.ExecuteNonQuery();
    });

    public HistoryReport Read(DateOnly? start = null, DateOnly? end = null, string? category = null, string? status = null,
        TimeZoneInfo? zone = null)
    {
        if (start > end) throw new ArgumentException("开始日期不能晚于结束日期。");
        zone ??= TimeZoneInfo.Local;
        return queue.Execute(u =>
        {
            using var c = u.Connection.CreateCommand(); c.Transaction = u.Transaction;
            c.CommandText = "SELECT COALESCE(MAX(sequence),0) FROM history_item_events";
            var watermark = Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture);
            c.CommandText = """
                SELECT h.item_id,h.title,h.kind,h.status,h.category,h.source,h.created_at,
                    (SELECT fact.completed_at FROM history_item_events fact WHERE fact.item_id=h.item_id
                     AND fact.status='Completed' AND fact.completed_at IS NOT NULL ORDER BY fact.sequence LIMIT 1),
                    h.scheduled_at,h.archived,h.revision,h.capture_kind
                FROM history_item_events h
                JOIN (SELECT item_id,MAX(sequence) AS last FROM history_item_events GROUP BY item_id) latest
                ON h.sequence=latest.last ORDER BY h.sequence DESC
                """;
            var records = new List<HistoryRecord>();
            using var r = c.ExecuteReader();
            DateTimeOffset? Date(int n) => r.IsDBNull(n) ? null : DateTimeOffset.TryParse(r.GetString(n), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var value) ? TimeZoneInfo.ConvertTime(value, zone) : null;
            while (r.Read())
            {
                var row = new HistoryRecord(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                    r.IsDBNull(4) ? "未分类" : r.GetString(4), r.GetString(5) == "Local" ? "Open Island" : r.GetString(5),
                    Date(6), Date(7), Date(8), r.GetBoolean(9) || r.GetString(11) == "DELETE", r.GetInt64(10));
                var dates = new[] { row.CreatedAt, row.CompletedAt, row.ScheduledAt }.Where(d => d.HasValue)
                    .Select(d => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(d!.Value, zone).DateTime));
                if ((start is not null || end is not null) && !dates.Any(d => (start is null || d >= start) && (end is null || d <= end))) continue;
                if (category is not null && row.Category != category || status is not null && row.Status != status) continue;
                records.Add(row);
            }
            var activities = records.SelectMany(row => new[] { (At: row.CreatedAt, Completed: false), (At: row.CompletedAt, Completed: true) })
                .Where(x => x.At.HasValue).Select(x => (Day: DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(x.At!.Value, zone).DateTime), x.Completed))
                .Where(x => (start is null || x.Day >= start) && (end is null || x.Day <= end));
            var days = activities.GroupBy(x => x.Day).OrderBy(g => g.Key)
                .Select(g => new HistoryDay(g.Key.ToString("yyyy-MM-dd"), g.Count(x => !x.Completed), g.Count(x => x.Completed))).ToArray();
            return new HistoryReport("island-history-v1", DateTimeOffset.UtcNow, zone.Id, start?.ToString("yyyy-MM-dd"), end?.ToString("yyyy-MM-dd"), watermark, records, days);
        });
    }

    public string SaveSnapshot(HistoryReport report) => queue.Execute(u =>
    {
        var id = Guid.NewGuid().ToString("N");
        using var c = u.Connection.CreateCommand(); c.Transaction = u.Transaction;
        c.CommandText = "INSERT INTO history_report_snapshots(id,created_at,payload) VALUES($id,$at,$payload)";
        c.Parameters.AddWithValue("$id", id); c.Parameters.AddWithValue("$at", report.GeneratedAt.ToString("O"));
        c.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(report)); c.ExecuteNonQuery(); return id;
    });

    public IReadOnlyList<HistoryReport> Snapshots() => queue.Execute<IReadOnlyList<HistoryReport>>(u =>
    {
        using var c = u.Connection.CreateCommand(); c.Transaction = u.Transaction;
        c.CommandText = "SELECT payload FROM history_report_snapshots ORDER BY created_at DESC";
        using var r = c.ExecuteReader(); var result = new List<HistoryReport>();
        while (r.Read()) result.Add(JsonSerializer.Deserialize<HistoryReport>(r.GetString(0)) ?? throw new InvalidDataException("报表快照损坏"));
        return result;
    });
}

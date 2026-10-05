using System.IO.Compression;
using System.Xml.Linq;
using ChronoIsle.App.Services;
using ChronoIsle.App.Services.Persistence;
using ChronoIsle.App.Services.Reporting;
using Microsoft.Data.Sqlite;

namespace ChronoIsle.Tests;

public sealed class HistoryReportTests
{
    [Fact]
    public void DeletingAndPurgingAnItemRetainsItsHistoryWithoutCountingRepeatedUpdates()
    {
        using var s = new Scope();
        s.Seed("one", "Completed", "2026-10-01T16:30:00Z");
        s.Sql("UPDATE life_items SET title='改名',row_version=row_version+1 WHERE id='one'");
        s.Sql("DELETE FROM life_items WHERE id='one'");
        var report = s.Reports.Read(zone: TimeZoneInfo.Utc);
        Assert.Single(report.Records); Assert.Equal(1, report.Completed); Assert.True(report.Records[0].Archived);
        Assert.Equal(1, Assert.Single(report.Days).Completed);
        Assert.Equal("改名", report.Records[0].Title);
    }

    [Fact]
    public void MissingCompletionTimestampIsNotInventedFromUpdateOrArchiveTime()
    {
        using var s = new Scope(); s.Seed("one", "Completed", null);
        var report = s.Reports.Read(); Assert.Equal(1, report.Completed);
        Assert.Null(report.Records[0].CompletedAt); Assert.All(report.Days, d => Assert.Equal(0, d.Completed));
    }

    [Fact]
    public void DateBoundariesUseRequestedTimeZoneAndIncludeEndDate()
    {
        using var s = new Scope(); s.Seed("one", "Completed", "2026-10-01T16:30:00Z");
        var zone = TimeZoneInfo.CreateCustomTimeZone("UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
        var report = s.Reports.Read(new(2026, 10, 2), new(2026, 10, 2), zone: zone);
        Assert.Single(report.Records); Assert.Equal("2026-10-02", Assert.Single(report.Days).Date);
        Assert.Equal("2026-10-02 00:30", report.Records[0].CompletedText);
        Assert.Empty(s.Reports.Read(new(2026, 10, 3), new(2026, 10, 3), zone: zone).Records);
        Assert.Throws<ArgumentException>(() => s.Reports.Read(new(2026, 10, 3), new(2026, 10, 2)));
    }

    [Fact]
    public void ReopeningAnItemPreservesItsCompletionFactAndCompletingAgainDoesNotDoubleCount()
    {
        using var s = new Scope(); s.Seed("one", "Completed", "2026-10-01T16:30:00Z");
        s.Sql("UPDATE life_items SET status='Pending',completed_at_utc=NULL WHERE id='one'");
        var reopened = s.Reports.Read(zone: TimeZoneInfo.Utc); Assert.Equal(0, reopened.Completed); Assert.Equal(1, reopened.Days.Sum(d => d.Completed));
        s.Sql("UPDATE life_items SET status='Completed',completed_at_utc='2026-10-02T16:30:00Z' WHERE id='one'");
        Assert.Equal(1, s.Reports.Read(zone: TimeZoneInfo.Utc).Days.Sum(d => d.Completed));
    }

    [Fact]
    public void SnapshotRemainsFixedWhenSourceChangesAndHistoryRollsBackWithSource()
    {
        using var s = new Scope(); s.Seed("one", "Pending", null);
        s.Reports.SaveSnapshot(s.Reports.Read());
        s.Sql("UPDATE life_items SET title='later' WHERE id='one'");
        Assert.Equal("测试事项", Assert.Single(s.Reports.Snapshots()).Records[0].Title);
        Assert.Throws<InvalidOperationException>(() => s.Queue.Execute(u =>
        {
            using var c = u.Connection.CreateCommand(); c.Transaction = u.Transaction;
            c.CommandText = "UPDATE life_items SET title='rolled-back' WHERE id='one'"; c.ExecuteNonQuery();
            throw new InvalidOperationException();
        }));
        Assert.Equal("later", s.Reports.Read().Records[0].Title);
    }

    [Fact]
    public void ReinitializationDoesNotDuplicateBaselineAndRetainsLegacyArchiveWithoutInventingDates()
    {
        using var s = new Scope(); s.Seed("one", "Pending", null);
        s.Sql("INSERT INTO archived_todos(id,title,archived_at,reason) VALUES('legacy','旧记录','2026-10-01T00:00:00','已完成')");
        HistoryReportService.EnsureCreated(s.Queue); HistoryReportService.EnsureCreated(s.Queue);
        var report = s.Reports.Read(); Assert.Equal(2, report.Records.Count);
        var legacy = Assert.Single(report.Records, r => r.Id == "legacy"); Assert.Null(legacy.CreatedAt); Assert.Null(legacy.CompletedAt);
    }

    [Fact]
    public void ExportsKeepChineseAndNeutralizeSpreadsheetFormulaInjection()
    {
        using var s = new Scope(); s.Seed("one", "Pending", null);
        s.Sql("""UPDATE life_items SET title='=HYPERLINK("https://invalid.test")' WHERE id='one'""");
        var report = s.Reports.Read(); Assert.Contains("\"'=HYPERLINK", HistoryReportExport.Csv(report));
        using var stream = new MemoryStream(); HistoryReportExport.Xlsx(stream, report); stream.Position = 0;
        using var zip = new ZipArchive(stream); using var worksheet = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        var xml = XDocument.Load(worksheet); XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        Assert.Empty(xml.Descendants(ns + "f")); Assert.Contains(xml.Descendants(ns + "t"), t => t.Value == "事项");
        Assert.Contains(xml.Descendants(ns + "t"), t => t.Value.StartsWith("=HYPERLINK"));
    }

    sealed class Scope : IDisposable
    {
        readonly string path = Path.Combine(Path.GetTempPath(), $"island-history-{Guid.NewGuid():N}.db");
        public IDbWriteQueue Queue { get; }
        public HistoryReportService Reports { get; }
        public Scope() { _ = new LifeDataService(path); Queue = new SqliteDbWriteQueue(new SqliteConnectionFactory(path)); Reports = new(Queue); }
        public void Seed(string id, string status, string? completed) => Queue.Execute(u =>
        {
            using var c = u.Connection.CreateCommand(); c.Transaction = u.Transaction;
            c.CommandText = "INSERT INTO life_items(id,kind,title,status,created_at,updated_at,completed_at_utc) VALUES($id,'Todo','测试事项',$status,'2026-10-01T01:00:00Z','2026-10-01T01:00:00Z',$completed)";
            c.Parameters.AddWithValue("$id", id); c.Parameters.AddWithValue("$status", status); c.Parameters.AddWithValue("$completed", (object?)completed ?? DBNull.Value); c.ExecuteNonQuery();
        });
        public void Sql(string sql) => Queue.Execute(u => { using var c = u.Connection.CreateCommand(); c.Transaction = u.Transaction; c.CommandText = sql; c.ExecuteNonQuery(); });
        public void Dispose() { SqliteConnection.ClearAllPools(); foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix); }
    }
}

using System.Text.Json;
using Microsoft.Data.Sqlite;
using ChronoIsle.App.Services.Persistence;
using ChronoIsle.App.Services.Reporting;

namespace ChronoIsle.Tests;

public sealed class ReportingServicesTests
{
    static readonly DateTimeOffset Now = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Snapshot_IsIdempotentForPeriodAndQueryVersion()
    {
        using var scope = new DatabaseScope();
        var service = new ReportService(scope.Queue, () => Now);
        var period = new ReportPeriod(ReportPeriodKind.Daily, Now.Date, Now.Date.AddDays(1));

        var first = service.Generate(period, "facts-v1");
        scope.Execute("""
            INSERT INTO life_items(id,kind,title,status,row_version,origin_type,is_readonly,created_at,updated_at)
            VALUES('later','Todo','Private title','Pending',1,'Local',0,
                   '2026-07-16T13:00:00.0000000+00:00','2026-07-16T13:00:00.0000000+00:00')
            """);
        var repeated = service.Generate(period, "facts-v1");
        var upgraded = service.Generate(period, "facts-v2");
        Assert.Equal(first.Id, repeated.Id);
        Assert.Equal(first.Facts, repeated.Facts);
        Assert.Equal(0, first.Facts.CreatedCount);
        Assert.Equal(1, upgraded.Facts.CreatedCount);
    }
    [Fact]
    public void WeeklyScheduler_GeneratesOnlyOnSunday_AndListsImmutableHistory()
    {
        using var scope = new DatabaseScope();
        var service = new ReportService(scope.Queue, () => Now);
        var scheduler = new WeeklyReportScheduler(service);
        var saturday = new DateTimeOffset(2026, 7, 18, 20, 0, 0, TimeSpan.FromHours(8));
        var sunday = saturday.AddDays(1);
        Assert.Null(scheduler.GenerateIfDue(saturday));
        var first = Assert.IsType<ReportSnapshot>(scheduler.GenerateIfDue(sunday));
        var repeated = Assert.IsType<ReportSnapshot>(scheduler.GenerateIfDue(sunday.AddHours(2)));
        Assert.Equal(first.Id, repeated.Id);

        var history = service.ListSnapshots(ReportPeriodKind.Weekly);
        var snapshot = Assert.Single(history);
        Assert.Equal(first.Id, snapshot.Id);
        Assert.Equal(["下周保持当前节奏，先安排最重要的一项任务"],
            WeeklyReportScheduler.BuildNextWeekPlan(snapshot.Facts));
    }


    [Fact]
    public void MonthlySnapshot_UsesTheWholeCalendarMonth_AndRemainsImmutable()
    {
        using var scope = new DatabaseScope();
        var service = new ReportService(scope.Queue, () => Now);
        var start = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var period = new ReportPeriod(ReportPeriodKind.Monthly, start, start.AddMonths(1));

        var first = service.Generate(period, "facts-v1");
        scope.Execute("""
            INSERT INTO life_items(id,kind,title,status,row_version,origin_type,is_readonly,created_at,updated_at)
            VALUES('later-month','Todo','Added after snapshot','Pending',1,'Local',0,
                   '2026-07-20T13:00:00.0000000+00:00','2026-07-20T13:00:00.0000000+00:00')
            """);
        var repeated = service.Generate(period, "facts-v1");

        Assert.Equal(ReportPeriodKind.Monthly, first.Period.Kind);
        Assert.Equal(start.AddMonths(1), first.Period.EndUtc);
        Assert.Equal(first.Id, repeated.Id);
        Assert.Equal(first.Facts, repeated.Facts);
    }

    [Fact]
    public void AiStatistics_AreOffByDefaultAndContainNoPrivateFields()
    {
        using var scope = new DatabaseScope();
        var service = new ReportService(scope.Queue, () => Now);
        var snapshot = service.Generate(new(ReportPeriodKind.Weekly, Now.AddDays(-7), Now), "facts-v1");

        Assert.Null(service.ExportMinimalStatisticsForAi(snapshot));
        var json = JsonSerializer.Serialize(service.ExportMinimalStatisticsForAi(snapshot, true));

        Assert.DoesNotContain("title", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("notes", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("itemId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AuditPrivacy_SanitizesExpiredDetailsAndCanClearAudit()
    {
        using var scope = new DatabaseScope();
        var privacy = new AuditPrivacyService(scope.Queue);
        scope.Execute("""
            INSERT INTO action_events(action_event_id,event_type,source_text,raw_parsed_json,structure_summary_json,error_text,created_at_utc)
            VALUES('old','Executed','secret source','{"secret":true}','{"command":"create_todo"}','private error','2026-06-01T00:00:00.0000000+00:00'),
                  ('new','Executed','recent source','{"recent":true}','{"command":"create_todo"}',NULL,'2026-07-15T00:00:00.0000000+00:00')
            """);

        var recent = privacy.GetRecent();
        Assert.Equal(2, recent.Count);
        Assert.Equal("Executed", recent[0].Command);
        Assert.Equal("Recorded", recent[0].Status);
        Assert.Equal(new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero), recent[0].CreatedAtUtc);

        Assert.Equal(1, privacy.SanitizeExpired(Now));
        using (var db = scope.Factory.OpenConnection())
        using (var command = db.CreateCommand())
        {
            command.CommandText = "SELECT source_text,raw_parsed_json,error_text,structure_summary_json FROM action_events WHERE action_event_id='old'";
            using var reader = command.ExecuteReader(); Assert.True(reader.Read());
            Assert.True(reader.IsDBNull(0)); Assert.True(reader.IsDBNull(1)); Assert.True(reader.IsDBNull(2));
            Assert.Equal("{\"command\":\"create_todo\"}", reader.GetString(3));
        }
        Assert.Equal(2, privacy.DeleteAll());
    }

    [Fact]
    public void PersonaProfile_ContainsWordingOnly()
    {
        var profile = AssistantPersonaFormatter.GetProfile(AssistantPersona.Witty);
        Assert.Equal("轻松型", profile.Name);
        Assert.All(typeof(AssistantWordingProfile).GetProperties(), property => Assert.Equal(typeof(string), property.PropertyType));
        Assert.DoesNotContain(typeof(AssistantWordingProfile).GetProperties(), property =>
            property.Name.Contains("policy", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("execute", StringComparison.OrdinalIgnoreCase));
    }

    sealed class DatabaseScope : IDisposable
    {
        readonly string path = Path.Combine(Path.GetTempPath(), $"chrono-isle-report-{Guid.NewGuid():N}.db");
        public DatabaseScope()
        {
            Factory = new(path); Queue = new SqliteDbWriteQueue(Factory);
            Queue.Execute(unitOfWork => new LifeSchemaMigrator("Etc/UTC", () => Now).Migrate(unitOfWork.Connection, unitOfWork.Transaction));
        }
        public SqliteConnectionFactory Factory { get; }
        public IDbWriteQueue Queue { get; }
        public void Execute(string sql) => Queue.Execute(unitOfWork =>
        { using var command = unitOfWork.Connection.CreateCommand(); command.Transaction = unitOfWork.Transaction; command.CommandText = sql; command.ExecuteNonQuery(); });
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }
}

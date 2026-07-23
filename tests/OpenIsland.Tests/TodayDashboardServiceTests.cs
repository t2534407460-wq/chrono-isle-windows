using Microsoft.Data.Sqlite;
using OpenIsland.App;
using OpenIsland.App.Services;
using OpenIsland.App.Services.Domain;
using OpenIsland.App.Services.Persistence;
using OpenIsland.App.Services.Productivity;
using OpenIsland.App.Services.State;

namespace OpenIsland.Tests;

public sealed class TodayDashboardServiceTests
{
    [Fact]
    public void SnapshotUsesFreshOfficialWorkdayOccurrenceForNextAction()
    {
        using var database = new DatabaseScope();
        var now = new DateTimeOffset(2026, 7, 22, 22, 39, 0, TimeSpan.FromHours(8)); // Wednesday evening
        var data = new LifeDataService(database.DatabasePath, () => now.LocalDateTime);
        var schedule = data.SaveOfficialSleepReminderSchedule(new TimeOnly(0, 0), new TimeOnly(1, 0));

        var snapshot = new TodayDashboardService(data).GetSnapshot(now);

        Assert.Equal(schedule.Single(item => item.Recurrence == RecurrenceKind.OfficialWorkdays).Id, snapshot.NextAction!.Id);
        Assert.Equal(new DateTimeOffset(2026, 7, 22, 16, 0, 0, TimeSpan.Zero), snapshot.NextAction.ScheduledAtUtc);
    }
    [Fact]
    public void SnapshotSeparatesTodayOverdueAndInbox_UsingCanonicalRows()
    {
        using var database = new DatabaseScope();
        // A fixed instant keeps the "today" boundary independent of the machine clock.
        var now = new DateTimeOffset(2026, 7, 21, 10, 0, 0, TimeSpan.Zero);
        database.Insert("overdue", "Todo", "overdue", due: now.AddMinutes(-30), priority: "High");
        database.Insert("reminder", "Reminder", "upcoming", remind: now.AddMinutes(30));
        database.Insert("inbox", "Todo", "unscheduled");
        database.Insert("later", "Event", "later", start: now.AddDays(2), end: now.AddDays(2).AddHours(1));

        var snapshot = database.Service.GetSnapshot(now);

        Assert.Equal("reminder", snapshot.NextAction!.Id);
        Assert.Contains(snapshot.Today, item => item.Id == "overdue");
        Assert.Contains(snapshot.Today, item => item.Id == "reminder");
        Assert.Single(snapshot.Overdue, item => item.Id == "overdue");
        Assert.Single(snapshot.Inbox, item => item.Id == "inbox");
        Assert.Equal("overdue", snapshot.SuggestedItemIds[0]);
    }


    [Fact]
    public void ScheduleInboxUsesOptimisticRowVersion_AndRequiresResolvedTemporalValue()
    {
        using var database = new DatabaseScope();
        database.Insert("inbox", "Todo", "schedule me");
        var due = TemporalValue.Absolute(DateTimeOffset.UtcNow.AddHours(2));

        Assert.Equal(InboxMutationResult.Succeeded, database.Service.ScheduleInbox("inbox", 1, due));
        Assert.Equal(InboxMutationResult.ConcurrentConflict, database.Service.ScheduleInbox("inbox", 1, due));
        Assert.Equal(2L, database.Scalar("SELECT row_version FROM life_items WHERE id='inbox'"));

        var unresolved = TemporalValue.Zoned(DateTime.Now, "Asia/Shanghai");
        Assert.Throws<ArgumentException>(() => database.Service.ScheduleInbox("missing", 1, unresolved));
    }


    [Fact]
    public void ReadOnlyInboxCannotBeIgnored_ButItsLocalCopyCanBeDeleted()
    {
        using var database = new DatabaseScope();
        database.Insert("mirror", "Todo", "external", isReadOnly: true);

        Assert.Equal(InboxMutationResult.ReadOnly, database.Service.Ignore("mirror", 1));
        Assert.Equal(InboxMutationResult.Succeeded, database.Service.DeleteLocalCopy("mirror", 1));
        Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM life_items WHERE id='mirror' AND deleted_at IS NOT NULL"));
    }


    [Fact]
    public void RestoreDeletedItem_IsExplicitAndUsesTheExpectedRowVersion()
    {
        using var database = new DatabaseScope();
        database.Insert("inbox", "Todo", "restore me");

        Assert.Equal(InboxMutationResult.Succeeded, database.Service.DeleteLocalCopy("inbox", 1));
        Assert.Equal(InboxMutationResult.ConcurrentConflict, database.Service.RestoreDeletedItem("inbox", 1));
        Assert.Equal(InboxMutationResult.Succeeded, database.Service.RestoreDeletedItem("inbox", 2));
        Assert.Equal(3L, database.Scalar("SELECT row_version FROM life_items WHERE id='inbox'"));
        Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM life_items WHERE id='inbox' AND deleted_at IS NOT NULL"));
    }
    sealed class DatabaseScope : IDisposable
    {
        public string DatabasePath => path;
        readonly string path = Path.Combine(Path.GetTempPath(), $"open-island-today-{Guid.NewGuid():N}.db");
        readonly SqliteConnectionFactory factory;
        readonly IDbWriteQueue queue;

        public DatabaseScope()
        {
            using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                connection.Open();
                new LifeSchemaMigrator("Etc/UTC").Migrate(connection);
            }
            factory = new(path);
            queue = new SqliteDbWriteQueue(factory);
            new ProductivitySchemaInitializer(queue).Initialize();
            Service = new(factory, queue);
        }

        public TodayDashboardService Service { get; }

        public void Insert(
            string id,
            string kind,
            string title,
            DateTimeOffset? due = null,
            DateTimeOffset? remind = null,
            DateTimeOffset? start = null,
            DateTimeOffset? end = null,
            string priority = "Normal",
            bool isReadOnly = false)
        {
            queue.Execute(uow =>
            {
                using var command = uow.Connection.CreateCommand();
                command.Transaction = uow.Transaction;
                command.CommandText = """
                    INSERT INTO life_items(
                      id,kind,title,status,row_version,
                      due_utc_instant,due_time_semantics,remind_utc_instant,remind_time_semantics,
                      start_utc_instant,start_time_semantics,end_utc_instant,end_time_semantics,
                      origin_type,is_readonly,readonly_reason,created_at,updated_at,priority)
                    VALUES($id,$kind,$title,'Pending',1,
                      $due,$dueSemantics,$remind,$remindSemantics,
                      $start,$startSemantics,$end,$endSemantics,
                      $origin,$readonly,$reason,$now,$now,$priority)
                    """;
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$kind", kind);
                command.Parameters.AddWithValue("$title", title);
                Add(command, "$due", "$dueSemantics", due);
                Add(command, "$remind", "$remindSemantics", remind);
                Add(command, "$start", "$startSemantics", start);
                Add(command, "$end", "$endSemantics", end);
                command.Parameters.AddWithValue("$origin", isReadOnly ? "IcsImport" : "Local");
                command.Parameters.AddWithValue("$readonly", isReadOnly ? 1 : 0);
                command.Parameters.AddWithValue("$reason", isReadOnly ? "Unsupported external semantics" : DBNull.Value);
                command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                command.Parameters.AddWithValue("$priority", priority);
                command.ExecuteNonQuery();
            });
        }

        public long Scalar(string sql)
        {
            using var connection = factory.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar());
        }

        static void Add(SqliteCommand command, string valueName, string semanticsName, DateTimeOffset? value)
        {
            command.Parameters.AddWithValue(valueName, value is null ? DBNull.Value : value.Value.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue(semanticsName, value is null ? DBNull.Value : "AbsoluteInstant");
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
                if (File.Exists(candidate)) File.Delete(candidate);
        }
    }
}

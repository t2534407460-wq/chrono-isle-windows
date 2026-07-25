using System.IO;
using Microsoft.Data.Sqlite;
using OpenIsland.App;
using OpenIsland.App.Services;
using OpenIsland.App.Services.Persistence;

namespace OpenIsland.Tests;

public sealed class M0AFoundationTests
{
    [Fact]
    public void LifeDataService_UsesAnInjectedClockForPersistedTimestamps()
    {
        using var scope = new TempDatabase();
        var now = new DateTime(2026, 7, 16, 9, 30, 0, DateTimeKind.Local);
        var data = new LifeDataService(scope.Path, () => now);

        var item = data.Save("clock-controlled", null, null, null);

        Assert.Equal(now, item.CreatedAt);
        Assert.Equal(now, item.UpdatedAt);
    }
    [Fact]
    public void ConnectionFactory_ConfiguresWalForeignKeysAndBusyTimeoutOnEveryConnection()
    {
        using var scope = new TempDatabase();
        var factory = new SqliteConnectionFactory(scope.Path);

        using var connection = factory.OpenConnection();

        Assert.Equal("wal", ScalarText(connection, "PRAGMA journal_mode"), ignoreCase: true);
        Assert.Equal(1L, ScalarInt64(connection, "PRAGMA foreign_keys"));
        Assert.Equal(5000L, ScalarInt64(connection, "PRAGMA busy_timeout"));
    }

    [Fact]
    public async Task WriteQueue_SerializesTransactionsAndRollsBackFailures()
    {
        using var scope = new TempDatabase();
        var factory = new SqliteConnectionFactory(scope.Path);
        var queue = new SqliteDbWriteQueue(factory);
        queue.Execute(unitOfWork =>
        {
            using var command = unitOfWork.Connection.CreateCommand();
            command.Transaction = unitOfWork.Transaction;
            command.CommandText = "CREATE TABLE values_table(value INTEGER NOT NULL)";
            command.ExecuteNonQuery();
        });

        var activeWriters = 0;
        var maximumActiveWriters = 0;
        var writes = Enumerable.Range(0, 12).Select(value => queue.ExecuteAsync(async (unitOfWork, cancellationToken) =>
        {
            var active = Interlocked.Increment(ref activeWriters);
            UpdateMaximum(ref maximumActiveWriters, active);
            try
            {
                await Task.Delay(5, cancellationToken);
                using var command = unitOfWork.Connection.CreateCommand();
                command.Transaction = unitOfWork.Transaction;
                command.CommandText = "INSERT INTO values_table(value) VALUES($value)";
                command.Parameters.AddWithValue("$value", value);
                return command.ExecuteNonQuery();
            }
            finally
            {
                Interlocked.Decrement(ref activeWriters);
            }
        })).ToArray();

        await Task.WhenAll(writes);

        Assert.Equal(1, maximumActiveWriters);
        Assert.Throws<InvalidOperationException>(() => queue.Execute(unitOfWork =>
        {
            using var command = unitOfWork.Connection.CreateCommand();
            command.Transaction = unitOfWork.Transaction;
            command.CommandText = "INSERT INTO values_table(value) VALUES(99)";
            command.ExecuteNonQuery();
            throw new InvalidOperationException("force rollback");
        }));

        using var connection = factory.OpenConnection();
        Assert.Equal(12L, ScalarInt64(connection, "SELECT COUNT(*) FROM values_table"));
    }

    [Fact]
    public async Task ReminderScanning_UsesTheCallerProvidedTime()
    {
        using var scope = new TempDatabase();
        var data = new LifeDataService(scope.Path);
        var now = DateTimeOffset.UtcNow;
        data.SaveReminder("due", null, now.LocalDateTime.AddMinutes(-1));
        using var service = new ReminderService(data, new LifePreferencesService(), new WindowsNotificationService());
        var deliveries = 0;
        service.ReminderDue += (_, _) => deliveries++;

        Assert.True(await service.ScanOnceAsync(now));
        Assert.True(await service.ScanOnceAsync(now));

        Assert.Equal(1, deliveries);
    }
    [Fact]
    public async Task ReminderPolling_UsesTheInjectedLocalClockForDueTime()
    {
        using var scope = new TempDatabase();
        var localNow = DateTime.Now.AddHours(1);
        var data = new LifeDataService(scope.Path, () => localNow);
        data.SaveReminder("local-clock", null, localNow.AddMinutes(-1));
        using var service = new ReminderService(
            data,
            new LifePreferencesService(),
            new WindowsNotificationService(),
            localNow: () => localNow);
        var deliveries = 0;
        service.ReminderDue += (_, _) => deliveries++;

        Assert.True(await service.PollNowAsync());
        Assert.Equal(1, deliveries);
    }

    [Fact]
    public async Task ReminderPolling_SkipsAnOverlappingTickAndReportsSuccess()
    {
        using var scope = new TempDatabase();
        var data = new LifeDataService(scope.Path);
        data.SaveReminder("due", null, DateTime.Now.AddMinutes(-1));
        using var service = new ReminderService(data, new LifePreferencesService(), new WindowsNotificationService());
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        service.ReminderDue += (_, _) =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
        };

        var firstPoll = service.PollNowAsync();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            Assert.False(await service.PollNowAsync());
            Assert.True(service.Health.IsPolling);
            Assert.Equal(1, service.Health.SkippedOverlappingTicks);
        }
        finally
        {
            release.Set();
        }

        Assert.True(await firstPoll.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(ReminderSchedulerStatus.Running, service.Health.Status);
        Assert.NotNull(service.Health.LastSuccessfulTickAt);
        Assert.Equal(0, service.Health.ConsecutiveFailures);
    }

    [Fact]
    public async Task ReminderPolling_CatchesFailuresAndRecoversOnTheNextTick()
    {
        using var scope = new TempDatabase();
        var data = new LifeDataService(scope.Path);
        data.SaveReminder("first", null, DateTime.Now.AddMinutes(-2));
        using var service = new ReminderService(data, new LifePreferencesService(), new WindowsNotificationService());
        EventHandler<AgendaItem> failingHandler = (_, _) => throw new InvalidOperationException("poll failed");
        service.ReminderDue += failingHandler;

        Assert.True(await service.PollNowAsync());
        Assert.Equal(ReminderSchedulerStatus.Degraded, service.Health.Status);
        Assert.Equal(1, service.Health.ConsecutiveFailures);
        Assert.NotNull(service.Health.LastFailedTickAt);
        Assert.Equal("poll failed", service.Health.LastErrorMessage);

        service.ReminderDue -= failingHandler;
        data.SaveReminder("second", null, DateTime.Now.AddMinutes(-1));
        Assert.True(await service.PollNowAsync());

        Assert.Equal(ReminderSchedulerStatus.Running, service.Health.Status);
        Assert.Equal(0, service.Health.ConsecutiveFailures);
        Assert.Null(service.Health.LastErrorMessage);
        Assert.NotNull(service.Health.LastSuccessfulTickAt);
    }
    [Fact]
    public async Task ReminderPolling_UsesOccurrenceLeaseAndNotificationOutboxBeforeRaisingTheBanner()
    {
        using var scope = new TempDatabase();
        var data = new LifeDataService(scope.Path);
        data.SaveReminder("outbox reminder", null, DateTime.Now.AddMinutes(-1));
        using var service = new ReminderService(data, new LifePreferencesService(), new WindowsNotificationService());
        AgendaItem? delivered = null;
        service.ReminderDue += (_, item) => delivered = item;

        Assert.True(await service.PollNowAsync());
        var item = Assert.IsType<AgendaItem>(delivered);
        Assert.Equal("outbox reminder", item.Title);

        using var connection = new SqliteConnection($"Data Source={scope.Path}");
        connection.Open();
        using var occurrences = connection.CreateCommand();
        occurrences.CommandText = "SELECT COUNT(*) FROM reminder_occurrences WHERE delivery_status='Delivered'";
        Assert.Equal(1L, Convert.ToInt64(occurrences.ExecuteScalar()));
        using var outbox = connection.CreateCommand();
        outbox.CommandText = "SELECT COUNT(*) FROM notification_outbox WHERE status='Delivered'";
        Assert.Equal(1L, Convert.ToInt64(outbox.ExecuteScalar()));
        Assert.False(service.NotificationHealth.IsDegraded);
    }


    static string ScalarText(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar()) ?? "";
    }

    static long ScalarInt64(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    static void UpdateMaximum(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current) return;
            current = observed;
        }
    }

    sealed class TempDatabase : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"open-island-m0a-{Guid.NewGuid():N}.db");

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { Path, Path + "-wal", Path + "-shm" })
                if (File.Exists(path)) File.Delete(path);
        }
    }
}

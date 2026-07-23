using Microsoft.Data.Sqlite;
using OpenIsland.App.Services.Persistence;
using OpenIsland.App.Services.Scheduling;

namespace OpenIsland.Tests;

public sealed class NotificationDispatcherTests
{
    [Fact]
    public async Task DeliveryRunsOutsideWriteQueue_AndIsRecordedOnce()
    {
        using var database = new TempDatabase();
        var now = new DateTimeOffset(2026, 7, 16, 10, 0, 0, TimeSpan.Zero);
        database.Enqueue(now);
        var transport = new QueueProbingTransport(database.Queue);
        var dispatcher = new NotificationDispatcher(database.Outbox, transport);

        Assert.Equal(NotificationDispatchOutcome.Delivered, await dispatcher.DispatchNextAsync(now));
        Assert.Equal(NotificationDispatchOutcome.NothingDue, await dispatcher.DispatchNextAsync(now));
        Assert.Equal(1, transport.Deliveries);
        Assert.False(dispatcher.Health.IsDegraded);

        using var connection = database.Factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM notification_deliveries WHERE status='Delivered'";
        Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar()));
    }

    [Fact]
    public void ExpiredClaimCanBeRecovered_AndOldOwnerCannotComplete()
    {
        using var database = new TempDatabase();
        var now = new DateTimeOffset(2026, 7, 16, 10, 0, 0, TimeSpan.Zero);
        database.Enqueue(now);

        var first = Assert.IsType<NotificationDeliveryRequest>(
            database.Outbox.TryClaim(now, "worker-a", TimeSpan.FromSeconds(60)));
        Assert.Null(database.Outbox.TryClaim(now.AddSeconds(59), "worker-b", TimeSpan.FromSeconds(60)));
        var recovered = Assert.IsType<NotificationDeliveryRequest>(
            database.Outbox.TryClaim(now.AddSeconds(60), "worker-b", TimeSpan.FromSeconds(60)));

        Assert.Equal(2, recovered.AttemptCount);
        Assert.False(database.Outbox.MarkDelivered(first, now.AddSeconds(61)));
        Assert.True(database.Outbox.MarkDelivered(recovered, now.AddSeconds(61)));
    }

    [Fact]
    public async Task RepeatedFailuresUseBoundedBackoffAndSurfaceDegradedHealth()
    {
        using var database = new TempDatabase();
        var now = DateTimeOffset.UtcNow;
        database.Enqueue(now);
        var dispatcher = new NotificationDispatcher(database.Outbox, new FailingTransport(), maximumAttempts: 2);

        Assert.Equal(NotificationDispatchOutcome.RetryScheduled, await dispatcher.DispatchNextAsync(now));
        Assert.Equal(NotificationDispatchOutcome.NothingDue, await dispatcher.DispatchNextAsync(now.AddSeconds(4)));
        Assert.Equal(NotificationDispatchOutcome.DeadLettered, await dispatcher.DispatchNextAsync(now.AddSeconds(5)));
        Assert.True(dispatcher.Health.IsDegraded);
        Assert.Equal(2, dispatcher.Health.ConsecutiveFailures);
        Assert.Equal(NotificationDispatchOutcome.NothingDue, await dispatcher.DispatchNextAsync(now.AddDays(1)));
    }

    sealed class QueueProbingTransport(IDbWriteQueue queue) : INotificationTransport
    {
        public int Deliveries { get; private set; }

        public Task DeliverAsync(NotificationDeliveryRequest request, CancellationToken cancellationToken)
        {
            // This would throw as a nested write if the dispatcher held the queue during transport work.
            queue.Execute(_ => Deliveries++);
            return Task.CompletedTask;
        }
    }

    sealed class FailingTransport : INotificationTransport
    {
        public Task DeliverAsync(NotificationDeliveryRequest request, CancellationToken cancellationToken) =>
            Task.FromException(new InvalidOperationException("transport unavailable"));
    }

    sealed class TempDatabase : IDisposable
    {
        readonly string path = Path.Combine(Path.GetTempPath(), $"open-island-outbox-{Guid.NewGuid():N}.db");

        public TempDatabase()
        {
            Factory = new SqliteConnectionFactory(path);
            Queue = new SqliteDbWriteQueue(Factory);
            var reminderStore = new ReminderDeliveryStore(Queue);
            Outbox = new NotificationOutboxStore(Queue);
            ReminderStore = reminderStore;
        }

        public SqliteConnectionFactory Factory { get; }
        public IDbWriteQueue Queue { get; }
        public ReminderDeliveryStore ReminderStore { get; }
        public NotificationOutboxStore Outbox { get; }

        public void Enqueue(DateTimeOffset target)
        {
            var key = NotificationIdempotencyKey.Create("item", "occurrence", "Show", 1, target);
            Assert.True(ReminderStore.EnqueueNotification(new(key, "item", "occurrence", "Show", target)));
            Assert.False(ReminderStore.EnqueueNotification(new(key, "item", "occurrence", "Show", target)));
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
                if (File.Exists(candidate)) File.Delete(candidate);
        }
    }
}

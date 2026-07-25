using System.IO;
using Microsoft.Data.Sqlite;
using ChronoIsle.App.Services.Persistence;
using ChronoIsle.App.Services.Scheduling;

namespace ChronoIsle.Tests;

public sealed class ReminderDeliveryStoreTests
{
    [Fact]
    public void Claim_IsExclusiveUntilLeaseExpires_ThenCanBeRecovered()
    {
        using var db = new TempDatabase();
        var detector = db.Detector();
        var now = new DateTimeOffset(2026, 7, 16, 10, 0, 0, TimeSpan.Zero);
        Assert.True(detector.Register(new("occ-1", "item-1", now.AddMinutes(-1))));

        var first = detector.TryClaimDue(now, "worker-a");
        Assert.NotNull(first);
        Assert.Null(detector.TryClaimDue(now, "worker-b"));
        Assert.Null(detector.TryClaimDue(now.AddSeconds(59), "worker-b"));

        var recovered = detector.TryClaimDue(now.AddSeconds(60), "worker-b");
        Assert.NotNull(recovered);
        Assert.Equal("worker-b", recovered.ClaimToken);
        Assert.Equal(2, recovered.AttemptCount);
        Assert.False(detector.MarkDelivered(first!, now.AddSeconds(61)));
        Assert.True(detector.MarkDelivered(recovered, now.AddSeconds(61)));
    }

    [Fact]
    public async Task ConcurrentClaims_ReturnOnlyOneLease()
    {
        using var db = new TempDatabase();
        var detector = db.Detector();
        var now = DateTimeOffset.UtcNow;
        detector.Register(new("occ-exclusive", "item", now.AddSeconds(-1)));

        var claims = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(i => Task.Run(() => detector.TryClaimDue(now, $"worker-{i}"))));

        Assert.Single(claims.Where(value => value is not null));
    }

    [Fact]
    public void NotificationOutbox_UsesStableActiveIdempotencyKey()
    {
        using var db = new TempDatabase();
        var store = db.Store();
        var target = new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
        var key = NotificationIdempotencyKey.Create("item", "occ", "Show", 3, target);
        Assert.Equal(key, NotificationIdempotencyKey.Create("item", "occ", "Show", 3, target.ToOffset(TimeSpan.FromHours(8))));

        var entry = new NotificationOutboxEntry(key, "item", "occ", "Show", target);
        Assert.True(store.EnqueueNotification(entry));
        Assert.False(store.EnqueueNotification(entry));
    }

    [Fact]
    public void Failures_UseBoundedAttemptsAndEndInDeadLetter()
    {
        using var db = new TempDatabase();
        var detector = db.Detector(maximumAttempts: 2);
        var now = new DateTimeOffset(2026, 7, 16, 10, 0, 0, TimeSpan.Zero);
        detector.Register(new("occ-retry", "item", now));

        var first = Assert.IsType<ReminderOccurrenceLease>(detector.TryClaimDue(now, "a"));
        Assert.Equal(ReminderFailureOutcome.RetryScheduled, detector.MarkFailed(first, now, "temporary"));
        Assert.Null(detector.TryClaimDue(now.AddSeconds(4), "b"));
        var second = Assert.IsType<ReminderOccurrenceLease>(detector.TryClaimDue(now.AddSeconds(5), "b"));
        Assert.Equal(ReminderFailureOutcome.DeadLettered, detector.MarkFailed(second, now.AddSeconds(5), "permanent"));
        Assert.Null(detector.TryClaimDue(now.AddDays(1), "c"));
    }

    [Fact]
    public void DoNotDisturb_ReleasesOneAggregateInsteadOfIndividualDeliveries()
    {
        using var db = new TempDatabase();
        var policy = new ReminderPolicyService(db.Store());
        var now = new DateTimeOffset(2026, 7, 16, 10, 0, 0, TimeSpan.Zero);
        var dnd = new ReminderPresentationContext(false, false, true);

        Assert.Equal(ReminderPolicyDecisionKind.Defer,
            policy.Apply(new("item-1", "occ-1", now.AddHours(-1), ReminderPriority.Normal, dnd), now).Kind);
        Assert.Equal(ReminderPolicyDecisionKind.Defer,
            policy.Apply(new("item-2", "occ-2", now.AddHours(1), ReminderPriority.High, dnd), now).Kind);
        Assert.Equal(ReminderPolicyDecisionKind.DeliverImmediately,
            policy.Apply(new("item-3", "occ-3", now, ReminderPriority.Urgent, dnd), now).Kind);

        var summary = policy.ReleaseDeferredSummary(now, "batch-1");
        Assert.Equal(2, summary.Total);
        Assert.Equal(1, summary.Overdue);
        Assert.Equal(1, summary.Normal);
        Assert.Equal(1, summary.High);
        Assert.Equal("batch-1", summary.SummaryBatchId);
        Assert.Equal(0, policy.ReleaseDeferredSummary(now, "batch-2").Total);
    }

    sealed class TempDatabase : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"chrono-isle-reminders-{Guid.NewGuid():N}.db");
        public ReminderDeliveryStore Store() => new(new SqliteDbWriteQueue(new SqliteConnectionFactory(Path)));
        public ReminderDueDetector Detector(int maximumAttempts = 5) => new(Store(), maximumAttempts);
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { Path, Path + "-wal", Path + "-shm" }) if (File.Exists(path)) File.Delete(path);
        }
    }
}

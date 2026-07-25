using Microsoft.Data.Sqlite;
using ChronoIsle.App.Services.Persistence;
using ChronoIsle.App.Services.Sync;

namespace ChronoIsle.Tests;

public sealed class GraphSyncBoundariesTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "chrono-isle-graph-sync-" + Guid.NewGuid().ToString("N"));
    DateTimeOffset now = new(2026, 7, 16, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Todo_and_calendar_cursors_preserve_scope_and_opaque_links()
    {
        var store = CreateStore();
        var todo = SyncCursor.ForTodo("account", "list-a", SyncResourceKind.TodoTask,
            "https://graph/next?$skiptoken=opaque", "https://graph/delta?$deltatoken=opaque", now);
        var calendar = SyncCursor.ForCalendar("account", "calendar-a", now.AddDays(-90), now.AddDays(365),
            null, "https://graph/calendar/delta?opaque", now);
        store.SaveCursor(todo);
        store.SaveCursor(calendar);
        Assert.Equal(todo, store.LoadCursor("account", SyncProvider.MicrosoftToDo, SyncResourceKind.TodoTask, "list-a"));
        Assert.Equal(calendar, store.LoadCursor("account", SyncProvider.OutlookCalendar, SyncResourceKind.CalendarEvent,
            "calendar-a", now.AddDays(-90)));
    }

    [Fact]
    public void Calendar_cursor_rejects_invalid_window_instead_of_guessing() =>
        Assert.Throws<ArgumentException>(() => SyncCursor.ForCalendar("a", "c", now, now, null, null));

    [Fact]
    public void Outbox_is_idempotent_and_retries_stop_at_dead_letter()
    {
        var store = CreateStore();
        var first = store.Enqueue("a", SyncProvider.MicrosoftToDo, SyncDirection.Push, "upsert", "stable-key", "{}", 3);
        Assert.Equal(first, store.Enqueue("a", SyncProvider.MicrosoftToDo, SyncDirection.Push, "upsert", "stable-key", "{}", 3));
        var work = Assert.IsType<SyncOutboxWork>(store.ClaimNext());
        Assert.Equal(SyncOutboxStatus.Pending, store.Fail(work.Id, work.ClaimToken, "one"));
        now = now.AddSeconds(6);
        work = Assert.IsType<SyncOutboxWork>(store.ClaimNext());
        Assert.Equal(SyncOutboxStatus.Pending, store.Fail(work.Id, work.ClaimToken, "two"));
        now = now.AddSeconds(11);
        work = Assert.IsType<SyncOutboxWork>(store.ClaimNext());
        Assert.Equal(SyncOutboxStatus.DeadLetter, store.Fail(work.Id, work.ClaimToken, "three"));
        now = now.AddDays(1);
        Assert.Null(store.ClaimNext());
    }

    [Fact]
    public void Expired_lease_is_reclaimed_but_old_owner_cannot_complete()
    {
        var store = CreateStore();
        store.Enqueue("a", SyncProvider.OutlookCalendar, SyncDirection.Pull, "delta", "calendar-delta", "{}");
        var first = Assert.IsType<SyncOutboxWork>(store.ClaimNext());
        now = now.AddSeconds(61);
        var second = Assert.IsType<SyncOutboxWork>(store.ClaimNext());
        Assert.NotEqual(first.ClaimToken, second.ClaimToken);
        Assert.False(store.Complete(first.Id, first.ClaimToken));
        Assert.True(store.Complete(second.Id, second.ClaimToken));
    }

    [Fact]
    public void Soft_deleted_mapping_releases_unique_remote_identity()
    {
        var store = CreateStore();
        store.AddMapping("m1", "a", SyncProvider.MicrosoftToDo, "local-1", "remote-1", ExternalMappingResult.Editable());
        Assert.Throws<SqliteException>(() => store.AddMapping("m2", "a", SyncProvider.MicrosoftToDo,
            "local-2", "remote-1", ExternalMappingResult.Editable()));
        Assert.True(store.SoftDeleteMapping("m1"));
        store.AddMapping("m2", "a", SyncProvider.MicrosoftToDo, "local-2", "remote-1",
            ExternalMappingResult.ReadOnly("Remote recurrence cannot be represented losslessly."));
    }

    [Fact]
    public void Secret_failure_requires_reauth_and_preserves_business_rows()
    {
        var store = CreateStore(out var factory);
        store.UpsertAccount("a", SyncProvider.OutlookCalendar, SyncAccountStatus.Connected);
        store.AddMapping("m", "a", SyncProvider.OutlookCalendar, "local", "remote", ExternalMappingResult.Editable());
        store.MarkReauthRequiredAfterSecretFailure("a", SyncProvider.OutlookCalendar, "DPAPI");
        Assert.Equal(SyncAccountStatus.ReauthRequired, store.GetAccountStatus("a", SyncProvider.OutlookCalendar));
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sync_mappings WHERE deleted_at_utc IS NULL";
        Assert.Equal(1L, command.ExecuteScalar());
    }

    [Theory]
    [InlineData("same", "same", "same", "same", SyncChangeDisposition.NoChange)]
    [InlineData("old", "new", "same", "same", SyncChangeDisposition.ApplyLocalToRemote)]
    [InlineData("same", "same", "old", "new", SyncChangeDisposition.ApplyRemoteToLocal)]
    [InlineData("a", "b", "c", "d", SyncChangeDisposition.Conflict)]
    public void Classifier_never_overwrites_two_sided_changes(string oldLocal, string local,
        string oldRemote, string remote, SyncChangeDisposition expected) =>
        Assert.Equal(expected, GraphSyncChangeClassifier.Classify(new(oldLocal, local, oldRemote, remote)));

    [Fact]
    public void Unresolved_conflict_is_unique_and_requires_explicit_resolution()
    {
        var store = CreateStore();
        store.RecordConflict("a", SyncProvider.OutlookCalendar, "local", "remote", "l1", "r1");
        Assert.Throws<SqliteException>(() =>
            store.RecordConflict("a", SyncProvider.OutlookCalendar, "local", "remote", "l2", "r2"));
    }

    [Fact]
    public async Task Adapter_boundary_reports_not_configured_without_network_side_effects()
    {
        ISyncAdapter adapter = new NotConfiguredSyncAdapter(SyncProvider.MicrosoftToDo);
        var result = await adapter.PushAsync(new("a", "item", 1));
        Assert.Equal(SyncAdapterOutcome.NotConfigured, result.Outcome);
    }

    GraphSyncStore CreateStore() => CreateStore(out _);
    GraphSyncStore CreateStore(out SqliteConnectionFactory factory)
    {
        Directory.CreateDirectory(directory);
        factory = new SqliteConnectionFactory(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".db"));
        return new GraphSyncStore(factory, new SqliteDbWriteQueue(factory), () => now);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}

using System.IO;
using Microsoft.Data.Sqlite;
using OpenIsland.App.Services.Persistence;
using OpenIsland.App.Services.Domain;
using OpenIsland.App.Services.Productivity;

namespace OpenIsland.Tests;

public sealed class ProductivityServicesTests
{
    static readonly DateTimeOffset Now = new(2026, 7, 16, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Draft_PersistsAcrossStoreRestart_AndRejectsMoreThanTenItems()
    {
        using var scope = new DatabaseScope();
        var first = scope.Drafts(() => Now);
        var id = first.Create([new("one"), new("two", Now.AddHours(1))]);

        var restored = scope.Drafts(() => Now).Get(id);

        Assert.NotNull(restored);
        Assert.Equal(["one", "two"], restored.Items.Select(x => x.Proposal.Title));
        Assert.Throws<ArgumentOutOfRangeException>(() => first.Create(
            Enumerable.Range(0, 11).Select(i => new DraftProposal($"item-{i}")).ToArray()));
    }

    [Fact]
    public void DraftConfirmation_WhenOneInsertFails_RollsBackEveryItemAndState()
    {
        using var scope = new DatabaseScope();
        var store = scope.Drafts(() => Now);
        var id = store.Create([new("first"), new("second")]);
        var conflictingId = store.Get(id)!.Items[1].LifeItemId;
        scope.InsertTodo(conflictingId, "existing");

        Assert.Throws<SqliteException>(() => store.Confirm(id));

        Assert.Equal("Pending", store.Get(id)!.Status);
        Assert.Equal(0, scope.Count("SELECT COUNT(*) FROM life_items WHERE title='first'"));
        Assert.Equal(1, scope.Count("SELECT COUNT(*) FROM life_items WHERE id=$id", ("$id", conflictingId)));
    }

    [Fact]
    public void Focus_RestoresAfterRestart_AndRecordsActualMinutes()
    {
        using var scope = new DatabaseScope();
        scope.InsertTodo("todo", "work");
        var clock = Now;
        var service = scope.Focus(() => clock);
        var started = service.Start("todo", 45);

        var restored = scope.Focus(() => clock).RestoreActive();
        Assert.Equal(started.Id, restored!.Id);

        clock = clock.AddMinutes(12).AddSeconds(1);
        var result = service.End(started.Id);
        Assert.Equal(13, result.Session.ActualMinutes);
        Assert.True(result.ShouldAskToCompleteItem);
        Assert.Null(service.RestoreActive());
    }
    [Fact]
    public void Focus_PauseResume_PersistsAcrossRestart_AndExcludesPausedTime()
    {
        using var scope = new DatabaseScope();
        scope.InsertTodo("todo", "work");
        var clock = Now;
        var service = scope.Focus(() => clock);
        var started = service.Start("todo", 45);

        clock = clock.AddMinutes(10);
        var paused = service.Pause(started.Id);
        Assert.True(paused.IsPaused);
        Assert.Equal(clock, paused.PausedAtUtc);

        var restoredPaused = scope.Focus(() => clock).RestoreActive();
        Assert.True(restoredPaused!.IsPaused);

        clock = clock.AddMinutes(20);
        var resumed = service.Resume(started.Id);
        Assert.False(resumed.IsPaused);
        Assert.Equal(20 * 60, resumed.AccumulatedPausedSeconds);

        clock = clock.AddMinutes(15);
        var result = service.End(started.Id);
        Assert.Equal(25, result.Session.ActualMinutes);
        Assert.Equal(20 * 60, result.Session.AccumulatedPausedSeconds);
    }


    [Fact]
    public void Task_attributes_use_row_version_and_reject_stale_overwrite()
    {
        using var scope = new DatabaseScope();
        scope.InsertTodo("todo", "work");
        var service = scope.Attributes();
        var original = service.Get("todo")!;
        var changed = original with { Priority = LifePriority.High, Category = "工作", EstimatedMinutes = 25, Energy = EnergyLevel.High };

        Assert.Equal(TaskAttributesUpdateResult.Succeeded, service.Update(changed));
        Assert.Equal(TaskAttributesUpdateResult.ConcurrentConflict, service.Update(changed));
        var stored = service.Get("todo")!;
        Assert.Equal(LifePriority.High, stored.Priority);
        Assert.Equal("工作", stored.Category);
        Assert.Equal(25, stored.EstimatedMinutes);
        Assert.Equal(EnergyLevel.High, stored.Energy);
        scope.SetTaskMetadata("todo", "parent", Now, 2);
        var metadata = service.Get("todo")!;
        Assert.Equal("parent", metadata.ParentItemId);
        Assert.Equal(Now, metadata.CompletedAtUtc);
        Assert.Equal(2, metadata.DeferredCount);
    }

    [Fact]
    public void Recommendations_filter_by_available_time_and_energy_then_use_local_ranker()
    {
        using var scope = new DatabaseScope();
        scope.InsertTodo("short", "短任务");
        scope.InsertTodo("long", "长任务");
        scope.InsertTodo("high-energy", "高能任务");
        var service = scope.Attributes();
        Assert.Equal(TaskAttributesUpdateResult.Succeeded, service.Update(service.Get("short")! with { Priority = LifePriority.High, EstimatedMinutes = 20, Energy = EnergyLevel.Low }));
        Assert.Equal(TaskAttributesUpdateResult.Succeeded, service.Update(service.Get("long")! with { Priority = LifePriority.Urgent, EstimatedMinutes = 45, Energy = EnergyLevel.Low }));
        Assert.Equal(TaskAttributesUpdateResult.Succeeded, service.Update(service.Get("high-energy")! with { Priority = LifePriority.Urgent, EstimatedMinutes = 15, Energy = EnergyLevel.High }));

        var result = service.Recommend(30, EnergyLevel.Medium, Now);

        var item = Assert.Single(result);
        Assert.Equal("short", item.ItemId);
    }

    [Fact]
    public void LocalRanker_IsDeterministicAndUsesBusinessOrder()
    {
        TaskRankCandidate[] candidates =
        [
            new("later-short", Now.AddHours(2), "Normal", 5),
            new("overdue-low", Now.AddMinutes(-1), "Low", 5),
            new("urgent", Now.AddHours(3), "Urgent", 60),
            new("later-long", Now.AddHours(2), "Normal", 30)
        ];

        var first = LocalTaskRanker.Rank(candidates, Now).Select(x => x.Id).ToArray();
        var second = LocalTaskRanker.Rank(candidates.Reverse(), Now).Select(x => x.Id).ToArray();

        Assert.Equal(["overdue-low", "urgent", "later-short", "later-long"], first);
        Assert.Equal(first, second);
    }

    sealed class DatabaseScope : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"open-island-productivity-{Guid.NewGuid():N}.db");
        readonly SqliteConnectionFactory factory;
        readonly IDbWriteQueue queue;

        public DatabaseScope()
        {
            using (var db = new SqliteConnection($"Data Source={Path}"))
            {
                db.Open();
                new LifeSchemaMigrator("Etc/UTC", () => Now).Migrate(db);
            }
            factory = new(Path);
            queue = new SqliteDbWriteQueue(factory);
            new ProductivitySchemaInitializer(queue).Initialize();
        }

        public DraftStore Drafts(Func<DateTimeOffset> clock) => new(queue, factory, clock);
        public FocusService Focus(Func<DateTimeOffset> clock) => new(queue, factory, clock);

        public void InsertTodo(string id, string title) => queue.Execute(uow =>
        {
            using var command = uow.Connection.CreateCommand(); command.Transaction = uow.Transaction;
            command.CommandText = "INSERT INTO life_items(id,kind,title,status,row_version,origin_type,is_readonly,created_at,updated_at) VALUES($id,'Todo',$title,'Pending',1,'Local',0,$now,$now)";
            command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$title", title); command.Parameters.AddWithValue("$now", Now.ToString("O"));
            command.ExecuteNonQuery();
        });

        public TaskAttributesService Attributes() => new(factory, queue);
        public long Count(string sql, params (string, object)[] values)
        {
            using var db = factory.OpenConnection(); using var command = db.CreateCommand(); command.CommandText = sql;
            foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
            return Convert.ToInt64(command.ExecuteScalar());
        }
        public void SetTaskMetadata(string id, string parentId, DateTimeOffset completedAt, int deferredCount) => queue.Execute(uow =>
        {
            using var command = uow.Connection.CreateCommand(); command.Transaction = uow.Transaction;
            command.CommandText = "UPDATE life_items SET parent_item_id=$parent,completed_at_utc=$completed,deferred_count=$deferred WHERE id=$id";
            command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$parent", parentId);
            command.Parameters.AddWithValue("$completed", completedAt.ToString("O")); command.Parameters.AddWithValue("$deferred", deferredCount);
            command.ExecuteNonQuery();
        });

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in new[] { Path, Path + "-wal", Path + "-shm" }) if (File.Exists(file)) File.Delete(file);
        }
    }
}

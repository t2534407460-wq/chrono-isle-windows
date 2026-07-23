using Microsoft.Data.Sqlite;
using OpenIsland.App.Services.Persistence;
using OpenIsland.App.Services.Productivity;

namespace OpenIsland.Tests;

public sealed class DraftSelectionTests : IDisposable
{
    readonly string path = Path.Combine(Path.GetTempPath(), "open-island-draft-selection-" + Guid.NewGuid().ToString("N") + ".db");
    readonly DateTimeOffset now = new(2026, 7, 21, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Confirmation_creates_only_selected_tasks_and_applies_only_explicit_schedule()
    {
        var store = CreateStore();
        var draftId = store.Create([new("安排到日历"), new("只做待办")]);
        var draft = store.Get(draftId)!;
        var selected = new HashSet<string> { draft.Items[0].Id, draft.Items[1].Id };
        var due = now.AddDays(1);

        var ids = store.ConfirmSelected(draftId, selected, new Dictionary<string, DateTimeOffset?> { [draft.Items[0].Id] = due });

        Assert.Equal(2, ids.Count);
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT title,due_utc_instant FROM life_items ORDER BY title";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("只做待办", reader.GetString(0));
        Assert.True(reader.IsDBNull(1));
        Assert.True(reader.Read());
        Assert.Equal("安排到日历", reader.GetString(0));
        Assert.Equal(due, DateTimeOffset.Parse(reader.GetString(1)));
    }

    DraftStore CreateStore()
    {
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            new LifeSchemaMigrator("Asia/Shanghai", () => now).Migrate(connection);
        }
        var factory = new SqliteConnectionFactory(path);
        var queue = new SqliteDbWriteQueue(factory);
        new ProductivitySchemaInitializer(queue).Initialize();
        return new DraftStore(queue, factory, () => now);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(file)) File.Delete(file);
    }
}

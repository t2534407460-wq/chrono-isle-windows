using ChronoIsle.App;
using ChronoIsle.App.Services;
using ChronoIsle.App.Services.Commanding;
using Microsoft.Data.Sqlite;

namespace ChronoIsle.Tests;

public sealed class LongTermItemStorageTests : IDisposable
{
    readonly string directory = Path.Combine(
        Path.GetTempPath(), "chrono-isle-long-term-storage-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Unscheduled_long_term_item_is_managed_but_not_a_daily_agenda_or_reminder()
    {
        var (path, data, pipeline) = Create();
        var result = pipeline.SubmitParsed(
            "创建长期事项学习外语",
            LongTerm("学习外语"));
        var id = Assert.Single(result.ItemIds);

        Assert.Equal("long_term", Assert.Single(data.LongTermItems()).Kind);
        Assert.DoesNotContain(data.AgendaForRange(DateTime.Today, DateTime.Today.AddDays(30)), item => item.Id == id);
        Assert.DoesNotContain(data.ReminderItems(), item => item.Id == id);

        using var connection = Open(path);
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT kind,item_type FROM life_items WHERE id=$id";
        query.Parameters.AddWithValue("$id", id);
        using var reader = query.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("Todo", reader.GetString(0));
        Assert.Equal("LongTerm", reader.GetString(1));
    }

    [Fact]
    public void Long_term_item_enters_reminder_schedule_only_when_it_has_a_reminder()
    {
        var (_, data, pipeline) = Create();
        var tomorrow = DateTime.Today.AddDays(1).AddHours(9);
        var command = new AssistantCommandEnvelope(
            AssistantCommandSchema.V1,
            AssistantCommandName.CreateLongTermItem,
            new CreateLongTermItemArgumentsV1(
                "长期复盘",
                null,
                null,
                At(tomorrow, "明天九点"),
                null),
            [],
            []);

        var result = pipeline.SubmitParsed("创建长期事项并在明天九点提醒我复盘", command);
        var id = Assert.Single(result.ItemIds);

        Assert.Contains(data.ReminderItems(), item => item.Id == id && item.Kind == "long_term");
        Assert.Contains(data.AgendaFor(tomorrow), item => item.Id == id && item.Kind == "long_term");
    }

    [Fact]
    public void Long_term_item_can_be_archived_and_restored_without_forcing_a_date()
    {
        var (_, data, pipeline) = Create();
        var id = Assert.Single(pipeline.SubmitParsed(
            "创建长期事项阅读经典",
            LongTerm("阅读经典")).ItemIds);
        var item = Assert.Single(data.LongTermItems());
        var agenda = new AgendaItem(id, item.Kind, item.Title, item.Notes, DateTime.Now, null, null, false);

        Assert.Equal(1, data.ArchiveAgendaItems([agenda]));
        Assert.Empty(data.LongTermItems());
        Assert.True(data.RestoreArchivedItem(id, null));
        Assert.Equal(id, Assert.Single(data.LongTermItems()).Id);
    }

    static AssistantCommandEnvelope LongTerm(string title) => new(
        AssistantCommandSchema.V1,
        AssistantCommandName.CreateLongTermItem,
        new CreateLongTermItemArgumentsV1(title, null, null, null, null),
        [],
        []);

    static AssistantTimeExpressionV1 At(DateTime value, string originalText) => new(
        DateOnly.FromDateTime(value),
        TimeOnly.FromDateTime(value),
        null,
        null,
        originalText);

    (string Path, LifeDataService Data, AssistantCommandPipeline Pipeline) Create()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".db");
        var data = new LifeDataService(path);
        return (path, data, new AssistantCommandPipeline(path));
    }

    static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();
        return connection;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (!Directory.Exists(directory)) return;
        try { Directory.Delete(directory, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

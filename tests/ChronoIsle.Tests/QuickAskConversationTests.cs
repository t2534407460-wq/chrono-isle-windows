using ChronoIsle.App.Services;
using ChronoIsle.App.ViewModels;
using Microsoft.Data.Sqlite;

namespace ChronoIsle.Tests;

public sealed class QuickAskConversationTests : IDisposable
{
    readonly string directory = Path.Combine(
        Path.GetTempPath(), "chrono-isle-quick-ask-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Every_quick_ask_starts_a_distinct_empty_conversation()
    {
        Directory.CreateDirectory(directory);
        var data = new LifeDataService(Path.Combine(directory, "quick-ask.db"));
        var viewModel = new LifeViewModel(data, null!, null!, null!, null!);
        var initialCount = data.Sessions().Count;

        var first = viewModel.BeginQuickAskConversation();
        var second = viewModel.BeginQuickAskConversation();

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(initialCount + 2, data.Sessions().Count);
        Assert.Equal(second.Id, viewModel.SelectedSession?.Id);
        Assert.Empty(data.Messages(first.Id));
        Assert.Empty(data.Messages(second.Id));
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

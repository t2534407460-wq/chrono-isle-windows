using ChronoIsle.App;
using ChronoIsle.App.Services;
using ChronoIsle.App.Services.Knowledge;
using ChronoIsle.App.Services.State;
using ChronoIsle.App.ViewModels;

namespace ChronoIsle.Tests;

[Collection("Assistant draft runtime")]
public sealed class KnowledgeConversationTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "chronoisle-knowledge-conversation", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Knowledge_questions_bypass_actions_and_preserve_existing_confirmation_and_conversation()
    {
        Directory.CreateDirectory(directory);
        var data = new LifeDataService(Path.Combine(directory, "life.db"));
        var noModel = new NoModel();
        var actions = new AssistantActionService(data, new ChinaStatutoryHolidayCalendar(), new ConversationRouter(), new LocalAgendaQueryService(data), noModel);
        var settings = new KnowledgeBaseSettingsService(directory);
        settings.SavePath(directory);
        var knowledge = new KnowledgeQuestionService(settings, new ObsidianKnowledgeIndex(), noModel);
        var vm = new LifeViewModel(data, actions, new ProviderSettingsService(), null!, new IslandStateCoordinator(), knowledge);
        var sessionId = vm.SelectedSession!.Id;
        var pending = new AssistantAction("pending", sessionId, "existing", "{}", "awaiting_confirmation", null, DateTime.Now, DateTime.Now);
        vm.PendingAction = pending;
        vm.PendingActionText = "原有待确认计划";
        vm.IsKnowledgeMode = true;
        await vm.SubmitQuickAskAsync("明天九点提醒我恢复数据库");
        Assert.Same(pending, vm.PendingAction);
        Assert.Equal("原有待确认计划", vm.PendingActionText);
        Assert.Equal(sessionId, vm.SelectedSession!.Id);
        Assert.Empty(data.ReminderItems());
        Assert.Empty(data.Todos());
        Assert.Equal(2, data.Messages(sessionId).Count);
        Assert.Contains("未检索到", vm.Messages.Last().Content);
        Assert.False(vm.IsSending);
        Assert.True(vm.CanChangeKnowledgeMode);
        Assert.Contains("完整问题", vm.ChatHint);
        vm.IsKnowledgeMode = false;
        Assert.Contains("明早九点", vm.ChatHint);
    }

    sealed class NoModel : IChatCompletionClient
    {
        public Task<string> Complete(ProviderSettings provider, IEnumerable<ModelMessage> messages, bool jsonObject = false) => throw new InvalidOperationException("Unexpected model call.");
        public Task<string> Reply(ProviderSettings provider, IEnumerable<ChatMessage> history, string input) => throw new InvalidOperationException("Unexpected model call.");
        public Task Test(ProviderSettings provider) => throw new NotSupportedException();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}

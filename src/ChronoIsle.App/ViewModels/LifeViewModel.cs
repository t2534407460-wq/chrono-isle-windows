using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ChronoIsle.App.Services;
using ChronoIsle.App.Services.Productivity;
using ChronoIsle.App.Services.State;

namespace ChronoIsle.App.ViewModels;

public partial class LifeViewModel : ObservableObject
{
    readonly LifeDataService data;
    readonly AssistantActionService actions;
    readonly ProviderSettingsService settings;
    readonly ReminderService reminders;

    [ObservableProperty] string chatInput = "";
    readonly IIslandStateCoordinator islandState;
    [ObservableProperty] string status = "准备就绪";
    [ObservableProperty] ChatSession? selectedSession;
    [ObservableProperty] AssistantAction? pendingAction;
    [ObservableProperty] string pendingActionText = "";

    public ObservableCollection<ChatSession> Sessions { get; } = [];
    public ObservableCollection<ChatMessage> Messages { get; } = [];
    public bool HasPendingAction => PendingAction is not null;

    public LifeViewModel(LifeDataService data, AssistantActionService actions, ProviderSettingsService settings, ReminderService reminders, IIslandStateCoordinator islandState)
    {
        this.data = data;
        this.actions = actions;
        this.settings = settings;
        this.reminders = reminders;
        RefreshSessions();
        this.islandState = islandState;
        if (Sessions.Count == 0) NewChat();
        else SelectedSession = Sessions[0];
    }

    partial void OnPendingActionChanged(AssistantAction? value) => OnPropertyChanged(nameof(HasPendingAction));

    void RefreshSessions()
    {
        Sessions.Clear();
        foreach (var session in data.Sessions()) Sessions.Add(session);
    }

    partial void OnSelectedSessionChanged(ChatSession? value)
    {
        if (value is null) return;
        Messages.Clear();
        foreach (var message in data.Messages(value.Id)) Messages.Add(message);
        PendingAction = null;
        PendingActionText = "";
    }

    [RelayCommand]
    void NewChat()
    {
        var session = data.NewSession();
        RefreshSessions();
        SelectedSession = Sessions.First(x => x.Id == session.Id);
    }

    [RelayCommand]
    void DeleteChat(ChatSession? session)
    {
        if (session is null) return;
        var wasSelected = SelectedSession?.Id == session.Id;
        data.DeleteSession(session.Id);
        RefreshSessions();
        if (wasSelected) SelectedSession = Sessions.FirstOrDefault();
        if (SelectedSession is null) NewChat();
    }

    [RelayCommand]
    async Task Send()
    {
        if (string.IsNullOrWhiteSpace(ChatInput) || SelectedSession is null) return;

        var text = ChatInput.Trim();
        var history = data.Messages(SelectedSession.Id);
        ChatInput = "";
        AddMessage("user", text);
        Status = "正在理解…";
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var handling = actions.HandleAsync(settings.Load(), SelectedSession, history, text);
            var processingVisible = false;
            if (await Task.WhenAny(handling, Task.Delay(300)) != handling)
            {
                processingVisible = IslandStateCoordinator.ShouldShowAiProcessing(stopwatch.Elapsed);
                if (processingVisible)
                    islandState.Publish(new IslandStateSnapshot("ai:processing", 35, "AI 正在处理…", null,
                        TimeSpan.FromMilliseconds(300), 70, IslandAnimationLevel.Subtle), DateTimeOffset.UtcNow);
            }
            var result = await handling;
            if (processingVisible)
            {
                if (result.IsFailure)
                    islandState.Clear("ai:processing", DateTimeOffset.UtcNow);
                else
                    islandState.Publish(IslandStateCoordinator.AiSucceeded(
                        result.PendingAction is null ? "AI 已生成建议" : "AI 已生成待确认操作", DateTimeOffset.UtcNow), DateTimeOffset.UtcNow);
            }
            AddMessage("assistant", result.Reply);
            if (result.RefreshReminders) reminders.RefreshSchedule();
            PendingAction = result.PendingAction?.Status == "awaiting_confirmation" ? result.PendingAction : null;
            PendingActionText = PendingAction is null ? "" : result.Reply;
            Status = result.IsFailure ? "未创建" : PendingAction is null ? "准备就绪" : "等待确认";
        }
        catch (Exception exception)
        {
            islandState.Clear("ai:processing", DateTimeOffset.UtcNow);
            var message = $"处理失败：{exception.Message}。未创建任何事项。";
            AddMessage("assistant", message);
            Status = "未创建";
        }
    }

    [RelayCommand]
    void ConfirmPendingAction()
    {
        if (PendingAction is null) return;
        ActionExecutionResult result;
        var draft = actions.GetPendingDecompositionDraft(PendingAction.Id);
        if (draft is not null)
        {
            var selection = DraftScheduleDialog.Show(draft);
            if (selection is null) return;
            result = actions.ConfirmDecompositionDraft(PendingAction.Id, selection.ItemIds, selection.ScheduleOverrides);
        }
        else result = actions.Confirm(PendingAction.Id);
        AddMessage("assistant", result.Message);
        if (result.Succeeded)
        {
            if (result.AgendaItem is not null)
                reminders.Schedule(result.AgendaItem);
            else
                reminders.RefreshSchedule();
        }
        PendingAction = null;
        PendingActionText = "";
        Status = result.Succeeded ? "已创建" : "未创建";
    }

    [RelayCommand]
    void CancelPendingAction()
    {
        if (PendingAction is null) return;
        actions.Cancel(PendingAction.Id);
        AddMessage("assistant", "已取消，本次没有创建任何事项。");
        PendingAction = null;
        PendingActionText = "";
        Status = "已取消";
    }

    void AddMessage(string role, string text)
    {
        if (SelectedSession is null) return;
        data.Message(SelectedSession.Id, role, text);
        Messages.Add(new("", SelectedSession.Id, role, text, DateTime.Now));
    }
}

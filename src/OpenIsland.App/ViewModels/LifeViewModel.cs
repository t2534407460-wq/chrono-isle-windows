using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenIsland.App.Services;

namespace OpenIsland.App.ViewModels;

public partial class LifeViewModel : ObservableObject
{
    readonly LifeDataService data;
    readonly AssistantActionService actions;
    readonly ProviderSettingsService settings;
    readonly ReminderService reminders;

    [ObservableProperty] string chatInput = "";
    [ObservableProperty] string status = "准备就绪";
    [ObservableProperty] ChatSession? selectedSession;
    [ObservableProperty] AssistantAction? pendingAction;
    [ObservableProperty] string pendingActionText = "";

    public ObservableCollection<ChatSession> Sessions { get; } = [];
    public ObservableCollection<ChatMessage> Messages { get; } = [];
    public bool HasPendingAction => PendingAction is not null;

    public LifeViewModel(LifeDataService data, AssistantActionService actions, ProviderSettingsService settings, ReminderService reminders)
    {
        this.data = data;
        this.actions = actions;
        this.settings = settings;
        this.reminders = reminders;
        RefreshSessions();
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
            var result = await actions.HandleAsync(settings.Load(), SelectedSession, history, text);
            AddMessage("assistant", result.Reply);
            PendingAction = result.PendingAction?.Status == "awaiting_confirmation" ? result.PendingAction : null;
            PendingActionText = PendingAction is null ? "" : result.Reply;
            Status = result.IsFailure ? "未创建" : PendingAction is null ? "准备就绪" : "等待确认";
        }
        catch (Exception exception)
        {
            var message = $"处理失败：{exception.Message}。未创建任何事项。";
            AddMessage("assistant", message);
            Status = "未创建";
        }
    }

    [RelayCommand]
    void ConfirmPendingAction()
    {
        if (PendingAction is null) return;
        var result = actions.Confirm(PendingAction.Id);
        AddMessage("assistant", result.Message);
        if (result.Succeeded && result.AgendaItem is not null) reminders.Schedule(result.AgendaItem);
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

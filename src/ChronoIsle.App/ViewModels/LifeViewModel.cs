using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
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
    CancellationTokenSource? sendCancellation;

    [ObservableProperty] string chatInput = "";
    readonly IIslandStateCoordinator islandState;
    [ObservableProperty] string status = "准备就绪";
    [ObservableProperty] bool isSending;
    [ObservableProperty] ChatSession? selectedSession;
    [ObservableProperty] AssistantAction? pendingAction;
    [ObservableProperty] string pendingActionText = "";
    [ObservableProperty] AssistantPendingPlan? pendingPlan;

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
    partial void OnChatInputChanged(string value) => SendCommand.NotifyCanExecuteChanged();
    partial void OnIsSendingChanged(bool value)
    {
        SendCommand.NotifyCanExecuteChanged();
        StopGeneratingCommand.NotifyCanExecuteChanged();
    }

    bool CanSend() => !IsSending && !string.IsNullOrWhiteSpace(ChatInput) && SelectedSession is not null;
    bool CanStopGenerating() => IsSending;

    public async Task SubmitAsync(string input)
    {
        ChatInput = input;
        if (CanSend()) await Send();
    }

    public async Task SubmitQuickAskAsync(string input)
    {
        if (IsSending || string.IsNullOrWhiteSpace(input)) return;
        BeginQuickAskConversation();
        await SubmitAsync(input);
    }

    void RefreshSessions()
    {
        Sessions.Clear();
        foreach (var session in data.Sessions()) Sessions.Add(session);
    }

    partial void OnSelectedSessionChanged(ChatSession? value)
    {
        sendCancellation?.Cancel();
        if (value is null) return;
        Messages.Clear();
        foreach (var message in data.Messages(value.Id)) Messages.Add(message);
        PendingAction = null;
        PendingPlan = null;
        PendingActionText = "";
    }

    [RelayCommand]
    void NewChat() => BeginQuickAskConversation();

    public ChatSession BeginQuickAskConversation()
    {
        var session = data.NewSession();
        RefreshSessions();
        SelectedSession = Sessions.First(value => value.Id == session.Id);
        return SelectedSession;
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

    [RelayCommand(CanExecute = nameof(CanSend))]
    async Task Send()
    {
        if (!CanSend() || SelectedSession is null) return;

        var session = SelectedSession;
        var text = ChatInput.Trim();
        var history = data.Messages(session.Id);
        ChatInput = "";
        AddMessage("user", text);
        IsSending = true;
        var cancellation = new CancellationTokenSource();
        sendCancellation = cancellation;
        var streamed = new StringBuilder();
        var streamingIndex = -1;
        Status = "正在理解…";
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var handling = actions.HandleAsync(settings.Load(), session, history, text, delta =>
            {
                if (string.IsNullOrEmpty(delta) || SelectedSession?.Id != session.Id) return;
                streamed.Append(delta);
                var message = new ChatMessage("streaming", session.Id, "assistant", streamed.ToString(), DateTime.Now);
                if (streamingIndex < 0)
                {
                    streamingIndex = Messages.Count;
                    Messages.Add(message);
                }
                else if (streamingIndex < Messages.Count)
                {
                    Messages[streamingIndex] = message;
                }
                Status = "正在生成…";
            }, cancellation.Token);
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
            if (streamingIndex >= 0)
            {
                data.Message(session.Id, "assistant", result.Reply);
                if (SelectedSession?.Id == session.Id && streamingIndex < Messages.Count)
                    Messages[streamingIndex] = new("", session.Id, "assistant", result.Reply, DateTime.Now);
            }
            else AddMessage(session, "assistant", result.Reply);
            if (result.RefreshReminders) reminders.RefreshSchedule();
            PendingAction = result.PendingAction?.Status == "awaiting_confirmation" ? result.PendingAction : null;
            PendingPlan = PendingAction is null ? null : result.PendingPlan;
            var awaitingDetails = result.PendingAction?.Status == "clarifying" || data.ActiveClarification(session.Id) is not null;
            PendingActionText = PendingAction is null ? "" : result.Reply;
            Status = result.IsFailure ? "执行失败" : PendingAction is not null ? "等待确认" : awaitingDetails ?
                "等待补充" : result.RefreshReminders ? "已执行" : "准备就绪";
        }
        catch (OperationCanceledException)
        {
            islandState.Clear("ai:processing", DateTimeOffset.UtcNow);
            var partial = streamed.Length == 0 ? "已停止生成。" : $"{streamed}\n\n> 已停止生成";
            if (streamingIndex >= 0)
            {
                data.Message(session.Id, "assistant", partial);
                if (SelectedSession?.Id == session.Id && streamingIndex < Messages.Count)
                    Messages[streamingIndex] = new("", session.Id, "assistant", partial, DateTime.Now);
            }
            else AddMessage(session, "assistant", partial);
            Status = "已停止";
        }
        catch (Exception exception)
        {
            islandState.Clear("ai:processing", DateTimeOffset.UtcNow);
            var message = $"处理失败：{exception.Message}。未创建任何事项。";
            if (streamingIndex >= 0)
            {
                data.Message(session.Id, "assistant", message);
                if (SelectedSession?.Id == session.Id && streamingIndex < Messages.Count)
                    Messages[streamingIndex] = new("", session.Id, "assistant", message, DateTime.Now);
            }
            else AddMessage(session, "assistant", message);
            Status = "执行失败";
        }
        finally
        {
            if (ReferenceEquals(sendCancellation, cancellation)) sendCancellation = null;
            cancellation.Dispose();
            IsSending = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStopGenerating))]
    void StopGenerating() => sendCancellation?.Cancel();

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
        PendingPlan = null;
        PendingActionText = "";
        Status = result.Succeeded ? "已执行" : "执行失败";
    }

    [RelayCommand]
    void CancelPendingAction()
    {
        if (PendingAction is null) return;
        actions.Cancel(PendingAction.Id);
        AddMessage("assistant", "已取消，本次没有执行任何写操作。");
        PendingAction = null;
        PendingPlan = null;
        PendingActionText = "";
        Status = "已取消";
    }

    void AddMessage(string role, string text)
    {
        if (SelectedSession is null) return;
        AddMessage(SelectedSession, role, text);
    }

    void AddMessage(ChatSession session, string role, string text)
    {
        data.Message(session.Id, role, text);
        if (SelectedSession?.Id == session.Id)
            Messages.Add(new("", session.Id, role, text, DateTime.Now));
    }
}

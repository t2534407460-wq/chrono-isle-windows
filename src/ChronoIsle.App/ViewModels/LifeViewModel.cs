using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ChronoIsle.App.Services;
using ChronoIsle.App.Services.Commanding;
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
    [ObservableProperty] AssistantInteraction? interaction;
    public ObservableCollection<AssistantInputViewModel> InteractionFields { get; } = [];
    public bool HasInteraction => Interaction is not null;

    partial void OnInteractionChanged(AssistantInteraction? value)
    {
        InteractionFields.Clear();
        foreach (var field in value?.Fields ?? [])
        {
            var input = new AssistantInputViewModel(field);
            input.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AssistantInputViewModel.InputValue)) RefreshFieldVisibility(); };
            InteractionFields.Add(input);
        }
        RefreshFieldVisibility();
        OnPropertyChanged(nameof(HasInteraction));
        UpdateInteractionCommands();
    }

    void RefreshFieldVisibility()
    {
        foreach (var field in InteractionFields)
            field.IsVisible = field.DependsOn is null || InteractionFields.FirstOrDefault(f => f.Key == field.DependsOn)?.InputValue == field.DependsValue;
    }

    bool CanInteract() => !IsSending && Interaction is not null;

    void UpdateInteractionCommands()
    {
        SubmitInteractionCommand.NotifyCanExecuteChanged();
        ConfirmInteractionCommand.NotifyCanExecuteChanged();
        CancelInteractionCommand.NotifyCanExecuteChanged();
        RetryInteractionCommand.NotifyCanExecuteChanged();
        EditInteractionCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanInteract))]
    Task SubmitInteraction() => ExecuteInteractionAsync("submit", "已提交补充信息");
    [RelayCommand(CanExecute = nameof(CanInteract))]
    Task ConfirmInteraction() => ExecuteInteractionAsync("confirm", "确认执行");
    [RelayCommand(CanExecute = nameof(CanInteract))]
    Task CancelInteraction() => ExecuteInteractionAsync("cancel", "取消任务");
    [RelayCommand(CanExecute = nameof(CanInteract))]
    Task RetryInteraction() => ExecuteInteractionAsync("retry", "重试任务");
    [RelayCommand(CanExecute = nameof(CanInteract))]
    Task EditInteraction() => ExecuteInteractionAsync("edit", "修改任务信息");

    async Task ExecuteInteractionAsync(string action, string label)
    {
        if (!CanInteract() || SelectedSession is null || Interaction is null) return;
        var session = SelectedSession;
        var card = Interaction;
        var values = InteractionFields.Where(field => field.IsVisible).ToDictionary(field => field.Key, field => field.InputValue);
        if (action == "submit")
        {
            foreach (var field in InteractionFields) field.Validate();
            if (InteractionFields.FirstOrDefault(field => field.HasError) is { } invalid)
            {
                Status = invalid.Label + "：" + invalid.Error;
                return;
            }
        }
        await RunTurnAsync(session, label, (delta, token) =>
            actions.InteractAsync(settings.Load(), session, card.RequestId, card.Revision, action, values, token));
    }

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
        UpdateInteractionCommands();
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
        if (SelectedSession is null) BeginQuickAskConversation();
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
        PendingAction = data.ActiveConfirmation(value.Id);
        PendingPlan = null;
        PendingActionText = PendingAction is null ? "" : data.Messages(value.Id).LastOrDefault(m => m.Role == "assistant")?.Content ?? "请核对待确认操作。";
        Interaction = actions?.GetInteraction(value.Id);
        Status = Interaction?.State == "NeedsInput" ? "等待补充" : Interaction?.CanConfirm == true || PendingAction is not null ? "等待确认" : "准备就绪";
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
        await RunTurnAsync(session, text, (delta, token) =>
            actions.HandleAsync(settings.Load(), session, history, text, delta, token));
    }

    async Task RunTurnAsync(ChatSession session, string text,
        Func<Action<string>, CancellationToken, Task<AssistantConversationResult>> handle)
    {
        if (IsSending) return;
        IsSending = true;
        AddMessage(session, "user", text);
        var cancellation = new CancellationTokenSource();
        sendCancellation = cancellation;
        var streamed = new StringBuilder();
        var streamingIndex = -1;
        Status = "正在处理…";
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var handling = handle(delta =>
            {
                if (string.IsNullOrEmpty(delta) || SelectedSession?.Id != session.Id) return;
                streamed.Append(delta);
                var message = new ChatMessage("streaming", session.Id, "assistant", streamed.ToString(), DateTime.Now);
                if (streamingIndex < 0) { streamingIndex = Messages.Count; Messages.Add(message); }
                else if (streamingIndex < Messages.Count) Messages[streamingIndex] = message;
                Status = "正在生成…";
            }, cancellation.Token);
            if (await Task.WhenAny(handling, Task.Delay(300)) != handling &&
                IslandStateCoordinator.ShouldShowAiProcessing(stopwatch.Elapsed))
                islandState.Publish(new IslandStateSnapshot("ai:processing", 35, "AI 正在处理…", null,
                    TimeSpan.FromMilliseconds(300), 70, IslandAnimationLevel.Subtle), DateTimeOffset.UtcNow);
            var result = await handling;
            var reply = result.Reply;
            if (result.RefreshReminders)
            {
                try { reminders.RefreshSchedule(); }
                catch (Exception)
                {
                    reply += "\n\n事项已保存，但提醒调度刷新失败。请重启应用恢复调度，勿重复创建。";
                }
            }
            SaveReply(reply);
            if (SelectedSession?.Id != session.Id) return;
            PendingAction = result.PendingAction?.Status == "awaiting_confirmation" ? result.PendingAction : null;
            PendingPlan = PendingAction is null ? null : result.PendingPlan;
            PendingActionText = PendingAction is null ? "" : result.Reply;
            Interaction = result.Interaction ?? actions.GetInteraction(session.Id);
            var awaitingDetails = Interaction?.State == "NeedsInput" || result.PendingAction?.Status == "clarifying" || data.ActiveClarification(session.Id) is not null;
            Status = result.IsFailure ? "需要处理" : PendingAction is not null || Interaction?.CanConfirm == true ? "等待确认" :
                awaitingDetails ? "等待补充" : result.RefreshReminders ? "已执行" : "准备就绪";
        }
        catch (OperationCanceledException)
        {
            SaveReply(streamed.Length == 0 ? "已停止处理。" : $"{streamed}\n\n> 已停止生成");
            if (SelectedSession?.Id == session.Id)
            {
                Status = "已停止";
                Interaction = actions.GetInteraction(session.Id);
            }
        }
        catch (Exception)
        {
            SaveReply("处理暂时中断。请查看任务卡片核验状态后继续。");
            if (SelectedSession?.Id == session.Id)
            {
                Status = "需要处理";
                Interaction = actions.GetInteraction(session.Id);
            }
        }
        finally
        {
            islandState.Clear("ai:processing", DateTimeOffset.UtcNow);
            if (ReferenceEquals(sendCancellation, cancellation)) sendCancellation = null;
            cancellation.Dispose();
            IsSending = false;
        }

        void SaveReply(string reply)
        {
            data.Message(session.Id, "assistant", reply);
            if (SelectedSession?.Id != session.Id) return;
            if (streamingIndex >= 0 && streamingIndex < Messages.Count)
                Messages[streamingIndex] = new("", session.Id, "assistant", reply, DateTime.Now);
            else Messages.Add(new("", session.Id, "assistant", reply, DateTime.Now));
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

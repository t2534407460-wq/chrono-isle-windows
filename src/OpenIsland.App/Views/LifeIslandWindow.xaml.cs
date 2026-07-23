using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Globalization;
using OpenIsland.App.Services.Domain;
using OpenIsland.App.Services.State;
using OpenIsland.App.Services.Productivity;
using OpenIsland.App.Services.Reporting;
using OpenIsland.App.Services;

namespace OpenIsland.App.Views;

public partial class LifeIslandWindow : Window
{
    const double CollapsedWidth = 420;
    const double ExpandedWidth = 650;
    const double SnapThreshold = 28;
    const double UnsnapThreshold = 48;
    readonly IIslandStateCoordinator islandState;

    readonly TaskAttributesService taskAttributes;
    readonly LifeDataService data;
    readonly ReminderService reminders;
    readonly ChinaStatutoryHolidayCalendar holidays;
    readonly TodayDashboardService todayDashboard;
    readonly FocusService focus;
    readonly ReportService reports;
    readonly DispatcherTimer focusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    FocusSession? activeFocus;
    FocusCompletion? pendingFocusCompletion;
    string? focusTitle;
    Border? todayPanel;
    StackPanel? todayDashboardContent;
    readonly DispatcherTimer clockTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    readonly DispatcherTimer collapseTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    DateTime displayedMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    DateTime selectedDate = DateTime.Today;
    bool dragging;
    bool dragged;
    bool expanded;
    bool notch;
    string? reminderBannerKind;
    string? reminderBannerItemId;
    AgendaItem? reminderBannerItem;
    System.Windows.Point dragStart;
    bool addingReminder = true;

    enum IslandQuickAction { AddTodo, AddReminder, StartFocus, ViewToday, AskAi, Settings, PauseReminders, ToggleDoNotDisturb }
    public event EventHandler? OpenRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler<string>? ChatRequested;
    int recommendationMinutes = 30;
    EnergyLevel recommendationEnergy = EnergyLevel.Medium;
    DateOnly? automaticWeeklyReportDate;
    bool nextWeekPlanVisible;

    public LifeIslandWindow(LifeDataService data, ReminderService reminders, ChinaStatutoryHolidayCalendar holidays, TodayDashboardService todayDashboard, FocusService focus, ReportService reports, IIslandStateCoordinator islandState, TaskAttributesService taskAttributes)
    {
        InitializeComponent();
        this.data = data;
        this.reminders = reminders;
        this.holidays = holidays;
        this.todayDashboard = todayDashboard;
        this.focus = focus;
        this.reports = reports;
        this.islandState = islandState;
        focusTimer.Tick += (_, _) => RefreshFocusSummary();
        Header.ContextMenu = CreateQuickActionMenu();
        clockTimer.Tick += (_, _) => Refresh();
        this.taskAttributes = taskAttributes;
        collapseTimer.Tick += (_, _) => Collapse();
        Loaded += (_, _) =>
        {
            InitializeQuickAdd();
            InitializeReminderActions();
            InitializeTodayDashboard();
            PositionAtTopCenter();
            Refresh();
            clockTimer.Start();
            focusTimer.Start();
        };
        data.AgendaChanged += (_, _) => Dispatcher.BeginInvoke(Refresh);
        islandState.StateChanged += _ => Dispatcher.BeginInvoke(Refresh);
        reminders.DeferredSummaryReleased += (_, summary) => Dispatcher.BeginInvoke(() =>
        {
            if (summary.Total == 0) return;
            ReminderText.Text = $"勿扰期间有 {summary.Total} 个提醒：逾期 {summary.Overdue} 个，其中高优先级 {summary.High + summary.Urgent} 个。";
            ReminderBanner.Visibility = Visibility.Visible;
            Expand();
            Touch();
        });
        reminders.NotificationHealthChanged += (_, health) => Dispatcher.BeginInvoke(() =>
        {
            var now = DateTimeOffset.UtcNow;
            if (!health.IsDegraded)
            {
                islandState.Clear("notification:degraded", now);
                return;
            }
            islandState.Publish(new IslandStateSnapshot(
                "notification:degraded", 85,
                "\u63d0\u9192\u6295\u9012\u6301\u7eed\u5931\u8d25\uff1a\u8bf7\u68c0\u67e5 Windows \u901a\u77e5\u8bbe\u7f6e\u3002",
                null, TimeSpan.FromSeconds(2), 100, IslandAnimationLevel.Prominent), now);
            Expand();
        });
    }

    public void ShowReminder(string kind, string itemId)
    {
        var item = data.FindAgendaItem(kind, itemId);
        if (item is not null)
        {
            selectedDate = item.StartsAt.Date;
            displayedMonth = new DateTime(selectedDate.Year, selectedDate.Month, 1);
            ReminderText.Text = $"提醒时间到：{item.Title}";
            ReminderBanner.Visibility = Visibility.Visible;
            reminderBannerKind = kind;
            reminderBannerItemId = itemId;
            reminderBannerItem = item;
        }
        Expand();
        Refresh();
        Touch();
    }

    void PositionAtTopCenter()
    {
        var area = CurrentScreenWorkArea();
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Top + 10;
    }

    Rect CurrentScreenWorkArea()
    {
        var center = PointToScreen(new System.Windows.Point(ActualWidth / 2, ActualHeight / 2));
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(
            (int)Math.Round(center.X), (int)Math.Round(center.Y)));
        var working = screen.WorkingArea;
        var topLeft = PointFromScreen(new System.Windows.Point(working.Left, working.Top));
        var bottomRight = PointFromScreen(new System.Windows.Point(working.Right, working.Bottom));
        return new Rect(Left + topLeft.X, Top + topLeft.Y,
            bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);
    }

    void Refresh()
    {
        if (reminderBannerKind is not null && reminderBannerItemId is not null &&
            data.FindAgendaItem(reminderBannerKind, reminderBannerItemId) is null)
        {
            ReminderBanner.Visibility = Visibility.Collapsed;
            reminderBannerKind = null;
            reminderBannerItemId = null;
            reminderBannerItem = null;
        }

        Clock.Text = DateTime.Now.ToString("HH:mm");
        if (!TryRenderFocusSummary())
        {
            StatusLight.Fill = IndicatorBrush(data.GetIslandIndicatorState(DateTime.Now));
            var now = DateTimeOffset.UtcNow;
            var transient = islandState.Current;
            if (transient?.ExpiresAt is not null && transient.ExpiresAt <= now)
            {
                islandState.Clear(transient.StateKey, now);
                transient = null;
            }
            var conflicts = CalendarConflictDetector.Find(data.AgendaFor(DateTime.Today));
            if (conflicts.Count > 0)
            {
                StatusLight.Fill = new SolidColorBrush(Color.FromRgb(255, 69, 58));
                Summary.Text = $"日程冲突 · 今日 {conflicts.Count} 组重叠 · {TodayCompletionText()}";
            }
            else if (transient is not null)
            {
                StatusLight.Fill = transient.StateKey == "ai:processing"
                    ? new SolidColorBrush(Color.FromRgb(94, 92, 230))
                    : new SolidColorBrush(Color.FromRgb(48, 209, 88));
                Summary.Text = transient.DisplayText;
            }
            else
            {
                var next = data.NextAgenda();
                Summary.Text = next is null
                    ? $"今天暂无安排 · {TodayCompletionText()}"
                    : $"下一项 · {next.StartsAt:HH:mm} {next.Title} · {TodayCompletionText()}";
            }
        }
        BuildCalendar();
        BuildDayAgenda();
        BuildTodayDashboard();
    }

    string TodayCompletionText()
    {
        var progress = TodayCompletionProgressCalculator.Calculate(data.Todos(), DateTime.Today);
        return $"今日 {progress.Completed}/{progress.Total}";
    }

    static System.Windows.Media.Brush IndicatorBrush(IslandIndicatorState state) => state switch
    {
        IslandIndicatorState.OverdueTodo => new SolidColorBrush(Color.FromRgb(255, 69, 58)),
        IslandIndicatorState.DueSoonTodo => new SolidColorBrush(Color.FromRgb(255, 159, 10)),
        IslandIndicatorState.PendingTodo => new SolidColorBrush(Color.FromRgb(174, 174, 178)),
        IslandIndicatorState.ReminderOnly => new SolidColorBrush(Color.FromRgb(255, 214, 10)),
        _ => new SolidColorBrush(Color.FromRgb(48, 209, 88))
    };

    void RefreshFocusSummary()
    {
        var hadFocus = activeFocus is not null;
        if (!TryRenderFocusSummary() && hadFocus) Refresh();
    }

    bool TryRenderFocusSummary()
    {
        var current = focus.RestoreActive();
        if (current is null)
        {
            activeFocus = null;
            focusTitle = null;
            return false;
        }
        if (activeFocus?.Id != current.Id)
        {
            activeFocus = current;
            focusTitle = data.Todos().FirstOrDefault(item => item.Id == current.ItemId)?.Title ?? "待办";
        }
        var now = DateTimeOffset.UtcNow;
        var pausedSeconds = current.AccumulatedPausedSeconds +
            (current.PausedAtUtc is null ? 0 : Math.Max(0, (int)Math.Floor((now - current.PausedAtUtc.Value).TotalSeconds)));
        var remaining = current.IntendedMinutes * 60 -
            (int)Math.Floor((now - current.StartedAtUtc).TotalSeconds) + pausedSeconds;
        StatusLight.Fill = remaining > 0
            ? new SolidColorBrush(Color.FromRgb(174, 174, 178))
            : new SolidColorBrush(Color.FromRgb(255, 159, 10));
        Summary.Text = current.IsPaused
            ? $"专注已暂停 · {focusTitle}"
            : remaining > 0
            ? $"专注中 · {focusTitle} {remaining / 60:00}:{remaining % 60:00}"
            : $"专注完成 · {focusTitle}";
        return true;
    }

    static System.Windows.Media.Brush CalendarForeground(bool inMonth, OfficialCalendarDay officialDay) =>
        !inMonth ? new SolidColorBrush(Color.FromRgb(92, 92, 98)) : officialDay.Kind switch
        {
            OfficialCalendarDayKind.StatutoryHoliday => new SolidColorBrush(Color.FromRgb(255, 105, 97)),
            OfficialCalendarDayKind.AdjustedWorkday => new SolidColorBrush(Color.FromRgb(255, 159, 10)),
            _ => Brushes.White
        };

    void InitializeReminderActions()
    {
        if (ReminderBanner.Child is not DockPanel dock) return;
        var close = dock.Children.OfType<Button>().FirstOrDefault();
        if (close is not null) dock.Children.Remove(close);
        dock.Children.Remove(ReminderText);

        var header = new DockPanel();
        if (close is not null)
        {
            DockPanel.SetDock(close, Dock.Right);
            header.Children.Add(close);
        }
        ReminderText.FontSize = 12;
        ReminderText.Margin = new Thickness(0, 0, 8, 0);
        header.Children.Add(ReminderText);

        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        actions.Children.Add(ReminderAction("完成", CompleteReminderBanner));
        actions.Children.Add(ReminderAction("10分钟", () => SnoozeReminderBanner(DateTime.Now.AddMinutes(10))));
        actions.Children.Add(ReminderAction("1小时", () => SnoozeReminderBanner(DateTime.Now.AddHours(1))));
        actions.Children.Add(ReminderAction("今晚", () => SnoozeReminderBanner(TonightAt())));
        actions.Children.Add(ReminderAction("明天", () => SnoozeReminderBanner(DateTime.Today.AddDays(1).AddHours(9))));
        actions.Children.Add(ReminderAction("跳过本次", SkipReminderBanner));
        actions.Children.Add(ReminderAction("自定义", CustomSnoozeReminderBanner));
        actions.Children.Add(ReminderAction("改时间", RescheduleReminderBanner));
        ReminderBanner.Child = new StackPanel { Children = { header, actions } };
    }

    Button ReminderAction(string label, Action action)
    {
        var button = new Button
        {
            Content = label,
            Style = (Style)FindResource("IslandIcon"),
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 0, 5, 5),
            FontSize = 11
        };
        button.Click += (_, _) => action();
        return button;
    }

    static DateTime TonightAt()
    {
        var tonight = DateTime.Today.AddHours(20);
        return DateTime.Now < tonight ? tonight : tonight.AddDays(1);
    }

    void SnoozeReminderBanner(DateTime remindAt)
    {
        var item = CurrentReminderBannerItem();
        if (item is null) { ClearReminderBanner(); return; }
        try
        {
            data.SnoozeReminder(item, remindAt);
            reminders.RefreshSchedule();
            ClearReminderBanner();
        }
        catch (Exception exception)
        {
            ReminderText.Text = $"稍后提醒失败：{exception.Message}";
        }
        Touch();
    }

    AgendaItem? CurrentReminderBannerItem() => reminderBannerItem ?? (reminderBannerKind is null || reminderBannerItemId is null
        ? null : data.FindAgendaItem(reminderBannerKind, reminderBannerItemId));

    void CustomSnoozeReminderBanner()
    {
        var time = PromptReminderTime("自定义稍后提醒");
        if (time is not null) SnoozeReminderBanner(time.Value);
    }

    void RescheduleReminderBanner()
    {
        var item = CurrentReminderBannerItem();
        if (item is null) { ClearReminderBanner(); return; }
        var time = PromptReminderTime("修改任务时间");
        if (time is null) return;
        try
        {
            if (item.Kind == "recurring") data.RescheduleRecurringOccurrence(item, time.Value);
            else data.RescheduleAgenda(item, time.Value);
            reminders.RefreshSchedule();
            ClearReminderBanner();
        }
        catch (Exception exception)
        {
            ReminderText.Text = $"修改任务时间失败：{exception.Message}";
        }
        Touch();
    }

    DateTime? PromptReminderTime(string title)
    {
        var input = new TextBox
        {
            Text = DateTime.Now.AddHours(1).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            Margin = new Thickness(0, 8, 0, 4), Padding = new Thickness(7), MinWidth = 230
        };
        var error = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(255, 105, 97)), FontSize = 11, TextWrapping = TextWrapping.Wrap };
        var dialog = new Window
        {
            Title = title, Owner = this, Width = 320, Height = 170, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new SolidColorBrush(Color.FromRgb(36, 36, 40)), Foreground = Brushes.White
        };
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "请输入明确时间（yyyy-MM-dd HH:mm）" });
        panel.Children.Add(input);
        panel.Children.Add(error);
        var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 7, 0, 0) };
        var cancel = new Button { Content = "取消", Padding = new Thickness(10, 4, 10, 4) };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        var confirm = new Button { Content = "确定", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0) };
        confirm.Click += (_, _) =>
        {
            if (!DateTime.TryParseExact(input.Text.Trim(), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            { error.Text = "请输入 yyyy-MM-dd HH:mm 格式。"; return; }
            if (parsed <= DateTime.Now)
            { error.Text = "时间必须晚于现在。"; return; }
            dialog.Tag = parsed;
            dialog.DialogResult = true;
        };
        actions.Children.Add(cancel);
        actions.Children.Add(confirm);
        panel.Children.Add(actions);
        dialog.Content = panel;
        return dialog.ShowDialog() == true ? (DateTime?)dialog.Tag : null;
    }

    void CompleteReminderBanner()
    {
        if (reminderBannerItem?.Kind == "todo") data.Complete(reminderBannerItem.Id);
        reminders.RefreshSchedule();
        ClearReminderBanner();
        Touch();
    }

    void SkipReminderBanner()
    {
        var item = CurrentReminderBannerItem();
        if (item?.Kind == "recurring")
        {
            try { data.SkipRecurringOccurrence(item); }
            catch (Exception exception) { ReminderText.Text = $"跳过本次失败：{exception.Message}"; return; }
        }
        reminders.RefreshSchedule();
        ClearReminderBanner();
        Touch();
    }
    void ClearReminderBanner()
    {
        ReminderBanner.Visibility = Visibility.Collapsed;
        reminderBannerKind = null;
        reminderBannerItemId = null;
        reminderBannerItem = null;
        // Dismissing a banner changes the user's current context. Rebuild the
        // compact summary immediately instead of leaving an expired reminder
        // title visible until the next one-minute clock tick.
        Refresh();
    }

    void InitializeTodayDashboard()
    {
        todayDashboardContent = new StackPanel();
        var panel = new StackPanel();
        panel.Children.Add(todayDashboardContent);
        todayPanel = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(28, 28, 30)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(58, 58, 60)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Margin = new Thickness(12, 0, 12, 12),
            Padding = new Thickness(14),
            Child = panel
        };
        ExpandedContent.Children.Insert(3, todayPanel);
        CalendarPanel.Visibility = Visibility.Collapsed;
        BuildTodayDashboard();
    }

    void BuildTodayDashboard()
    {
        if (todayDashboardContent is null) return;
        var snapshot = todayDashboard.GetSnapshot(DateTimeOffset.Now);
        todayDashboardContent.Children.Clear();
        var runningFocus = focus.RestoreActive();
        if (runningFocus is not null)
        {
            activeFocus = runningFocus;
            focusTitle ??= data.Todos().FirstOrDefault(item => item.Id == runningFocus.ItemId)?.Title ?? "待办";
            var focusRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var endFocus = new Button { Content = "结束专注", Style = (Style)FindResource("IslandType"), Background = new SolidColorBrush(Color.FromRgb(74, 37, 40)), Foreground = Brushes.White };
            var pauseFocus = new Button { Content = runningFocus.IsPaused ? "继续专注" : "暂停专注", Style = (Style)FindResource("IslandType"), Margin = new Thickness(6, 0, 0, 0) };
            var deepDnd = new Button { Content = reminders.IsDoNotDisturbEnabled ? "退出深度勿扰" : "深度勿扰", Style = (Style)FindResource("IslandType"), Margin = new Thickness(6, 0, 0, 0) };
            endFocus.Click += (_, _) => EndActiveFocus();
            pauseFocus.Click += (_, _) => ToggleFocusPause();
            deepDnd.Click += (_, _) =>
            {
                reminders.SetDoNotDisturb(!reminders.IsDoNotDisturbEnabled);
                BuildTodayDashboard();
            };
            DockPanel.SetDock(endFocus, Dock.Right);
            DockPanel.SetDock(pauseFocus, Dock.Right);
            DockPanel.SetDock(deepDnd, Dock.Right);
            focusRow.Children.Add(endFocus);
            focusRow.Children.Add(pauseFocus);
            focusRow.Children.Add(deepDnd);
            focusRow.Children.Add(new TextBlock { Text = $"正在专注：{focusTitle}", Foreground = new SolidColorBrush(Color.FromRgb(124, 196, 127)), VerticalAlignment = VerticalAlignment.Center });
            todayDashboardContent.Children.Add(focusRow);
        }
        if (pendingFocusCompletion is not null)
        {
            var pending = pendingFocusCompletion;
            var completionRow = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            completionRow.Children.Add(new TextBlock { Text = $"专注结束（实际 {pending.Session.ActualMinutes} 分钟）。要完成对应待办吗？", Foreground = new SolidColorBrush(Color.FromRgb(255, 214, 10)), TextWrapping = TextWrapping.Wrap });
            var choices = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
            var complete = new Button { Content = "完成任务", Style = (Style)FindResource("IslandType"), Background = new SolidColorBrush(Color.FromRgb(48, 107, 64)), Foreground = Brushes.White };
            complete.Click += (_, _) => CompleteFocusedTodo();
            var keep = new Button { Content = "保留未完成", Style = (Style)FindResource("IslandType"), Margin = new Thickness(6, 0, 0, 0) };
            keep.Click += (_, _) => KeepFocusedTodoPending();
            choices.Children.Add(complete);
            choices.Children.Add(keep);
            completionRow.Children.Add(choices);
            todayDashboardContent.Children.Add(completionRow);
        }
        var conflicts = CalendarConflictDetector.Find(data.AgendaFor(DateTime.Today));
        if (conflicts.Count > 0)
        {
            todayDashboardContent.Children.Add(new TextBlock { Text = $"日程冲突：今日有 {conflicts.Count} 组重叠日程", Foreground = new SolidColorBrush(Color.FromRgb(255, 105, 97)), Margin = new Thickness(0, 5, 0, 2), FontWeight = FontWeights.SemiBold });
            foreach (var conflict in conflicts.Take(2))
                todayDashboardContent.Children.Add(new TextBlock { Text = $"{conflict.First.StartsAt:HH:mm}–{conflict.First.EndsAt:HH:mm}  {conflict.First.Title} / {conflict.Second.Title}", Foreground = new SolidColorBrush(Color.FromRgb(255, 159, 10)), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
        }

        var quickActions = new WrapPanel { Margin = new Thickness(0, 0, 0, 9) };
        foreach (var action in new[] { IslandQuickAction.AddTodo, IslandQuickAction.StartFocus, IslandQuickAction.AskAi, IslandQuickAction.Settings })
        {
            var button = new Button { Content = QuickActionLabel(action), Style = (Style)FindResource("IslandQuick") };
            button.Click += (_, _) => RunQuickAction(action);
            quickActions.Children.Add(button);
        }
        todayDashboardContent.Children.Add(quickActions);

        var quickRow = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var quickInput = new TextBox
        {
            MinHeight = 38,
            Style = (Style)FindResource("IslandTextInput"),
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "例如：明早九点提醒我开会"
        };
        var quickSubmit = new Button { Content = "发送", Style = (Style)FindResource("IslandPrimary"), Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 7, 12, 7), ToolTip = "发送给 AI 助手" };
        quickSubmit.Click += (_, _) => SubmitQuickNaturalLanguage(quickInput);
        quickInput.KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.Key != Key.Enter) return;
            eventArgs.Handled = true;
            SubmitQuickNaturalLanguage(quickInput);
        };
        DockPanel.SetDock(quickSubmit, Dock.Right);
        quickRow.Children.Add(quickSubmit);
        quickRow.Children.Add(quickInput);
        todayDashboardContent.Children.Add(quickRow);

        todayDashboardContent.Children.Add(new TextBlock { Text = "今日概览", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 17 });
        todayDashboardContent.Children.Add(new TextBlock
        {
            Text = snapshot.NextAction is null ? "下一行动：暂无已安排事项" : $"下一行动：{DashboardTime(snapshot.NextAction)} {snapshot.NextAction.Title}",
            Foreground = new SolidColorBrush(Color.FromRgb(159, 217, 161)), Margin = new Thickness(0, 5, 0, 11), TextTrimming = TextTrimming.CharacterEllipsis
        });
        var counts = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 13) };
        counts.Children.Add(DashboardCount("今日", snapshot.Today.Count, Color.FromRgb(174, 174, 178)));
        counts.Children.Add(DashboardCount("逾期", snapshot.Overdue.Count, Color.FromRgb(255, 69, 58)));
        counts.Children.Add(DashboardCount("待整理", snapshot.Inbox.Count, Color.FromRgb(255, 159, 10)));
        todayDashboardContent.Children.Add(counts);
        var recommendationRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 5) };
        recommendationRow.Children.Add(new TextBlock { Text = "可用时间", Foreground = new SolidColorBrush(Color.FromRgb(152, 152, 157)), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        var minutes = new System.Windows.Controls.ComboBox { Width = 62, Height = 28, Style = (Style)FindResource("IslandSelect"), ItemsSource = new[] { 5, 15, 30, 60 }, SelectedItem = recommendationMinutes, Margin = new Thickness(0, 0, 6, 0) };
        recommendationRow.Children.Add(minutes);
        recommendationRow.Children.Add(new TextBlock { Text = "分钟 · 能量", Foreground = new SolidColorBrush(Color.FromRgb(152, 152, 157)), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        var energy = new System.Windows.Controls.ComboBox { Width = 82, Height = 28, Style = (Style)FindResource("IslandSelect"), ItemsSource = Enum.GetValues<EnergyLevel>(), SelectedItem = recommendationEnergy, Margin = new Thickness(0, 0, 6, 0) };
        recommendationRow.Children.Add(energy);
        var recommend = new Button { Content = "给我推荐", Style = (Style)FindResource("IslandType"), Padding = new Thickness(7, 2, 7, 2), FontSize = 10 };
        recommend.Click += (_, _) =>
        {
            if (minutes.SelectedItem is int selectedMinutes) recommendationMinutes = selectedMinutes;
            if (energy.SelectedItem is EnergyLevel selectedEnergy) recommendationEnergy = selectedEnergy;
            BuildTodayDashboard();
        };
        recommendationRow.Children.Add(recommend);
        todayDashboardContent.Children.Add(recommendationRow);
        var executable = taskAttributes.Recommend(recommendationMinutes, recommendationEnergy, DateTimeOffset.UtcNow);
        todayDashboardContent.Children.Add(new TextBlock
        {
            Text = executable.Count == 0
                ? "可执行推荐：还没有匹配当前时长与能量的待办。"
                : $"可执行推荐：{string.Join("、", executable.Select(item => $"{item.Title}（{item.EstimatedMinutes}分钟）"))}",
            Foreground = new SolidColorBrush(Color.FromRgb(188, 233, 192)), TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 0, 0, 6)
        });

        var overview = new Grid { Margin = new Thickness(0, 3, 0, 10) };
        overview.ColumnDefinitions.Add(new ColumnDefinition());
        overview.ColumnDefinitions.Add(new ColumnDefinition());
        var nextItems = snapshot.NextAction is { } nextAction ? new[] { nextAction } : Array.Empty<TodayDashboardItem>();
        var nextCard = DashboardOverviewCard("下一行动", nextItems, "暂无已安排事项", Color.FromRgb(87, 202, 130));
        var overdueCard = DashboardOverviewCard("逾期事项", snapshot.Overdue, "没有逾期事项", Color.FromRgb(255, 105, 97));
        Grid.SetColumn(nextCard, 0);
        Grid.SetColumn(overdueCard, 1);
        overview.Children.Add(nextCard);
        overview.Children.Add(overdueCard);
        todayDashboardContent.Children.Add(overview);
        AddDashboardSection("待整理 / Inbox", snapshot.Inbox, includeInboxActions: true);
        var all = snapshot.Today.Concat(snapshot.Overdue).Concat(snapshot.Inbox).GroupBy(item => item.Id).ToDictionary(group => group.Key, group => group.First());
        var suggestions = snapshot.SuggestedItemIds.Where(all.ContainsKey).Select(id => all[id].Title).ToArray();
        var localNow = DateTimeOffset.Now;
        var weekStart = DateTime.Today.AddDays(-((int)DateTime.Today.DayOfWeek + 6) % 7);
        var weekPeriod = new ReportPeriod(ReportPeriodKind.Weekly,
            new DateTimeOffset(weekStart, TimeZoneInfo.Local.GetUtcOffset(weekStart)),
            new DateTimeOffset(weekStart.AddDays(7), TimeZoneInfo.Local.GetUtcOffset(weekStart.AddDays(7))));
        var weeklyFacts = reports.Generate(weekPeriod, "facts-v1").Facts;
        todayDashboardContent.Children.Add(new TextBlock { Text = $"本周复盘：完成 {weeklyFacts.CompletedCount} · 逾期 {weeklyFacts.OverdueCount} · 高优先级 {weeklyFacts.HighPriorityCount}", Foreground = new SolidColorBrush(Color.FromRgb(152, 152, 157)), Margin = new Thickness(0, 8, 0, 0), FontSize = 11 });
        var suggestionCard = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(28, 28, 30)), BorderBrush = new SolidColorBrush(Color.FromRgb(58, 58, 60)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(9), Margin = new Thickness(0, 6, 0, 0),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "AI 今日建议（本地排序）", Foreground = new SolidColorBrush(Color.FromRgb(209, 209, 214)), FontSize = 11, FontWeight = FontWeights.SemiBold },
                    new TextBlock { Text = suggestions.Length == 0 ? "暂时没有需要优先处理的事项。" : $"优先处理 {string.Join("、", suggestions)}", Foreground = new SolidColorBrush(Color.FromRgb(229, 229, 234)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0), FontSize = 11 }
                }
            }
        };
        todayDashboardContent.Children.Add(suggestionCard);
    }

    void SubmitQuickNaturalLanguage(TextBox input)
    {
        var text = input.Text.Trim();
        if (text.Length == 0) return;
        input.Clear();
        ChatRequested?.Invoke(this, text);
    }

    ContextMenu CreateQuickActionMenu()
    {
        var menu = new ContextMenu();
        foreach (var action in Enum.GetValues<IslandQuickAction>())
        {
            var item = new MenuItem { Header = QuickActionLabel(action) };
            item.Click += (_, _) => RunQuickAction(action);
            menu.Items.Add(item);
        }
        return menu;
    }

    static string QuickActionLabel(IslandQuickAction action) => action switch
    {
        IslandQuickAction.AddTodo => "＋ 新建事项",
        IslandQuickAction.AddReminder => "◌ 添加提醒",
        IslandQuickAction.StartFocus => "◎ 专注模式",
        IslandQuickAction.ViewToday => "▣ 查看今天",
        IslandQuickAction.AskAi => "✦ 问 AI",
        IslandQuickAction.Settings => "设置",
        IslandQuickAction.PauseReminders => "暂停提醒",
        IslandQuickAction.ToggleDoNotDisturb => "勿扰模式",
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    void RunQuickAction(IslandQuickAction action)
    {
        switch (action)
        {
            case IslandQuickAction.AddTodo:
                addingReminder = false; UpdateQuickAddType(); ShowCalendarDashboard();
                Dispatcher.BeginInvoke(QuickAddInput.Focus); break;
            case IslandQuickAction.AddReminder:
                addingReminder = true; UpdateQuickAddType(); ShowCalendarDashboard();
                Dispatcher.BeginInvoke(QuickAddInput.Focus); break;
            case IslandQuickAction.StartFocus:
                try
                {
                    var candidate = taskAttributes.Recommend(60, EnergyLevel.High, DateTimeOffset.UtcNow).FirstOrDefault();
                    if (candidate is null) throw new InvalidOperationException("请先为一个待办设置预计分钟数，再开始专注。");
                    activeFocus = focus.Start(candidate.ItemId, 25);
                    focusTitle = candidate.Title;
                    ShowTodayDashboard();
                }
                catch (Exception exception)
                {
                    ReminderText.Text = $"无法开始专注：{exception.Message}";
                    ReminderBanner.Visibility = Visibility.Visible;
                    Expand();
                }
                break;
            case IslandQuickAction.ViewToday: ShowTodayDashboard(); break;
            case IslandQuickAction.AskAi: OpenAssistant(); break;
            case IslandQuickAction.Settings: OpenSettings(); break;
            case IslandQuickAction.PauseReminders: reminders.SetDoNotDisturb(true); BuildTodayDashboard(); break;
            case IslandQuickAction.ToggleDoNotDisturb: reminders.SetDoNotDisturb(!reminders.IsDoNotDisturbEnabled); BuildTodayDashboard(); break;
            default: throw new ArgumentOutOfRangeException(nameof(action));
        }
        Touch();
    }

    void ToggleFocusPause()
    {
        var current = activeFocus ?? focus.RestoreActive();
        if (current is null) return;
        try
        {
            activeFocus = current.IsPaused ? focus.Resume(current.Id) : focus.Pause(current.Id);
            Refresh();
        }
        catch (Exception exception)
        {
            ReminderText.Text = $"专注状态更新失败：{exception.Message}";
            ReminderBanner.Visibility = Visibility.Visible;
        }
    }

    void EndActiveFocus()
    {
        if (activeFocus is null) return;
        try
        {
            pendingFocusCompletion = focus.End(activeFocus.Id);
            activeFocus = null;
            focusTitle = null;
            Refresh();
        }
        catch (Exception exception)
        {
            ReminderText.Text = $"结束专注失败：{exception.Message}";
            ReminderBanner.Visibility = Visibility.Visible;
        }
    }

    void CompleteFocusedTodo()
    {
        if (pendingFocusCompletion is null) return;
        data.Complete(pendingFocusCompletion.Session.ItemId);
        pendingFocusCompletion = null;
        Refresh();
    }

    void KeepFocusedTodoPending()
    {
        pendingFocusCompletion = null;
        Refresh();
    }

    void AddDashboardSection(string title, IReadOnlyList<TodayDashboardItem> items, bool includeInboxActions = false)
    {
        if (todayDashboardContent is null || items.Count == 0) return;
        todayDashboardContent.Children.Add(new TextBlock { Text = title, Foreground = new SolidColorBrush(Color.FromRgb(152, 152, 157)), FontSize = 11, Margin = new Thickness(0, 3, 0, 3) });
        foreach (var item in items.Take(3))
        {
            if (!includeInboxActions)
            {
                todayDashboardContent.Children.Add(new TextBlock { Text = $"{DashboardTime(item)}  {item.Title}", Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0, 0, 3), FontSize = 12 });
                continue;
            }

            var card = new StackPanel { Margin = new Thickness(2, 0, 0, 6) };
            card.Children.Add(new TextBlock
            {
                Text = item.IsReadOnly ? $"{item.Title}（只读副本）" : item.Title,
                Foreground = Brushes.White,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontSize = 12
            });
            var controls = new WrapPanel { Margin = new Thickness(0, 3, 0, 0) };
            var when = new TextBox
            {
                Text = NextDashboardSchedule().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                Width = 112,
                Height = 25,
                Padding = new Thickness(5, 2, 5, 2),
                Background = new SolidColorBrush(Color.FromRgb(44, 44, 46)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                ToolTip = "请输入明确时间，例如 2026-07-17 09:00"
            };
            controls.Children.Add(when);
            controls.Children.Add(DashboardAction("安排", () =>
            {
                if (!DateTime.TryParseExact(when.Text.Trim(), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var local))
                {
                    ShowDashboardMessage("请输入 yyyy-MM-dd HH:mm 格式的明确时间。");
                    return;
                }
                ScheduleInbox(item, local);
            }));
            controls.Children.Add(DashboardAction("设为今天", () =>
            {
                var today = NextTodaySlot();
                if (today is null) ShowDashboardMessage("今天已没有可安排时段，请输入明确时间。");
                else ScheduleInbox(item, today.Value);
            }));
            controls.Children.Add(DashboardAction("设为本周", () =>
            {
                var thisWeek = NextThisWeekSlot();
                if (thisWeek is null) ShowDashboardMessage("本周已没有可安排时段，请输入明确时间。");
                else ScheduleInbox(item, thisWeek.Value);
            }));
            controls.Children.Add(DashboardAction("忽略", () => MutateInbox(item, deleteLocalCopy: false), !item.IsReadOnly));
            controls.Children.Add(DashboardAction("删除副本", () => MutateInbox(item, deleteLocalCopy: true)));
            card.Children.Add(controls);
            todayDashboardContent.Children.Add(card);
        }
    }

    Button DashboardAction(string label, Action action, bool enabled = true)
    {
        var button = new Button
        {
            Content = label,
            Style = (Style)FindResource("IslandType"),
            Margin = new Thickness(4, 0, 0, 3),
            Padding = new Thickness(6, 2, 6, 2),
            FontSize = 10,
            IsEnabled = enabled
        };
        button.Click += (_, _) => action();
        return button;
    }

    void ScheduleInbox(TodayDashboardItem item, DateTime local)
    {
        if (local <= DateTime.Now)
        {
            ShowDashboardMessage("安排时间必须晚于现在。");
            return;
        }
        var zones = new SystemTimeZoneCatalog();
        if (!zones.TryResolveIana(zones.LocalIanaTimeZoneId, out _, out var windows))
        {
            ShowDashboardMessage("当前系统时区无法解析，未安排事项。");
            return;
        }
        var wallClock = new TemporalValue(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), null,
            zones.LocalIanaTimeZoneId, windows, TimeSemantics.ZonedWallClock);
        var due = wallClock with { UtcInstant = new OccurrenceTimeResolver(zones).ResolveSingleLocalTime(wallClock) };
        ShowInboxResult(todayDashboard.ScheduleInbox(item.Id, item.RowVersion, due));
    }

    void MutateInbox(TodayDashboardItem item, bool deleteLocalCopy) =>
        ShowInboxResult(deleteLocalCopy
            ? todayDashboard.DeleteLocalCopy(item.Id, item.RowVersion)
            : todayDashboard.Ignore(item.Id, item.RowVersion));

    void ShowInboxResult(InboxMutationResult result)
    {
        if (result == InboxMutationResult.Succeeded)
        {
            Refresh();
            return;
        }
        ShowDashboardMessage(result switch
        {
            InboxMutationResult.ReadOnly => "只读外部副本不能修改，可删除本地副本。",
            InboxMutationResult.ConcurrentConflict => "事项刚刚发生变化，请刷新后再试。",
            InboxMutationResult.NotInboxItem => "该事项已不在待整理列表中。",
            _ => "没有找到该待整理事项。"
        });
    }

    void ShowDashboardMessage(string message)
    {
        ReminderText.Text = message;
        ReminderBanner.Visibility = Visibility.Visible;
        Touch();
    }

    static DateTime NextDashboardSchedule()
    {
        var now = DateTime.Now;
        return now.AddHours(1).AddMinutes(-now.Minute).AddSeconds(-now.Second);
    }

    static DateTime? NextTodaySlot()
    {
        var now = DateTime.Now;
        foreach (var hour in new[] { 10, 14, 20, 23 })
        {
            var candidate = now.Date.AddHours(hour);
            if (candidate > now.AddMinutes(5)) return candidate;
        }
        return null;
    }

    static DateTime? NextThisWeekSlot()
    {
        var now = DateTime.Now;
        var endOfWeek = now.Date.AddDays(6 - ((int)now.DayOfWeek + 6) % 7);
        for (var day = now.Date; day <= endOfWeek; day = day.AddDays(1))
            foreach (var hour in new[] { 10, 14, 20 })
            {
                var candidate = day.AddHours(hour);
                if (candidate > now.AddMinutes(5)) return candidate;
            }
        return null;
    }

    static Border DashboardCount(string label, int count, Color color) => new()
    {
        Background = new SolidColorBrush(Color.FromRgb(36, 36, 40)),
        CornerRadius = new CornerRadius(7),
        Padding = new Thickness(8, 6, 8, 6),
        Margin = new Thickness(0, 0, 5, 0),
        Child = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = count.ToString(), Foreground = new SolidColorBrush(color), FontWeight = FontWeights.SemiBold, HorizontalAlignment = System.Windows.HorizontalAlignment.Center },
                new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.FromRgb(152, 152, 157)), FontSize = 10, HorizontalAlignment = System.Windows.HorizontalAlignment.Center }
            }
        }
    };

    static Border DashboardOverviewCard(string title, IReadOnlyList<TodayDashboardItem> items, string emptyText, Color accent)
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = title, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 12 });
        if (items.Count == 0)
            content.Children.Add(new TextBlock { Text = emptyText, Foreground = new SolidColorBrush(Color.FromRgb(142, 142, 147)), FontSize = 11, Margin = new Thickness(0, 9, 0, 0), TextWrapping = TextWrapping.Wrap });
        else
            foreach (var item in items.Take(2))
                content.Children.Add(new TextBlock { Text = $"{DashboardTime(item)}  {item.Title}", Foreground = new SolidColorBrush(Color.FromRgb(229, 229, 234)), FontSize = 11, Margin = new Thickness(0, 8, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        return new Border { Background = new SolidColorBrush(Color.FromRgb(28, 28, 30)), BorderBrush = new SolidColorBrush(Color.FromRgb(58, 58, 60)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(12), Margin = new Thickness(0, 0, 8, 0), Child = content, Tag = accent };
    }

    static string DashboardTime(TodayDashboardItem item) => item.ScheduledAtUtc?.ToLocalTime().ToString("HH:mm") ?? "待安排";

    public void CollapsePanel() => Collapse();

    public void OpenTodayPanel()
    {
        Show();
        Expand();
        ShowTodayDashboard();
        Touch();
    }

    public void OpenCalendarPanel()
    {
        Show();
        Expand();
        ShowCalendarDashboard();
        Touch();
    }
    void ShowTodayDashboard()
    {
        if (todayPanel is null) return;
        todayPanel.Visibility = Visibility.Visible;
        if (weeklyReportPanel is not null) weeklyReportPanel.Visibility = Visibility.Visible;
        if (weeklyHistoryPanel is not null) weeklyHistoryPanel.Visibility = Visibility.Visible;
        CalendarPanel.Visibility = Visibility.Collapsed;
        TodayDashboardTab.Background = new SolidColorBrush(Color.FromRgb(72, 72, 74));
        TodayDashboardTab.Foreground = Brushes.White;
        CalendarDashboardTab.Background = new SolidColorBrush(Color.FromRgb(44, 44, 46));
        CalendarDashboardTab.Foreground = new SolidColorBrush(Color.FromRgb(184, 184, 191));
        BuildTodayDashboard();
        Touch();
    }
    void TodayTab_Click(object sender, RoutedEventArgs e) => ShowTodayDashboard();

    void CalendarTab_Click(object sender, RoutedEventArgs e) => ShowCalendarDashboard();


    void ShowCalendarDashboard()
    {
        if (todayPanel is null) return;
        todayPanel.Visibility = Visibility.Collapsed;
        if (weeklyReportPanel is not null) weeklyReportPanel.Visibility = Visibility.Collapsed;
        if (weeklyHistoryPanel is not null) weeklyHistoryPanel.Visibility = Visibility.Collapsed;
        CalendarPanel.Visibility = Visibility.Visible;
        CalendarDashboardTab.Background = new SolidColorBrush(Color.FromRgb(72, 72, 74));
        CalendarDashboardTab.Foreground = Brushes.White;
        TodayDashboardTab.Background = new SolidColorBrush(Color.FromRgb(44, 44, 46));
        TodayDashboardTab.Foreground = new SolidColorBrush(Color.FromRgb(184, 184, 191));
        Touch();
    }

    void BuildCalendar()
    {
        MonthTitle.Text = displayedMonth.ToString("yyyy 年 M 月");
        CalendarGrid.Children.Clear();
        var first = displayedMonth;
        var offset = ((int)first.DayOfWeek + 6) % 7;
        var cursor = first.AddDays(-offset);
        for (var index = 0; index < 42; index++, cursor = cursor.AddDays(1))
        {
            var day = cursor;
            var indicator = data.GetCalendarIndicatorState(day, DateTime.Now);
            var inMonth = day.Month == displayedMonth.Month;
            var officialDay = holidays.Get(day);
            var button = new Button
            {
                Tag = day,
                Height = 42,
                Style = (Style)FindResource("IslandDay"),
                Background = day.Date == selectedDate.Date ? new SolidColorBrush(Color.FromRgb(72, 72, 74)) : Brushes.Transparent,
                Foreground = day.Date == selectedDate.Date ? Brushes.White : CalendarForeground(inMonth, officialDay),
            };
            var content = new StackPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(new TextBlock { Text = day.Day.ToString(), HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Foreground = button.Foreground });
            var metadata = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
            if (officialDay.IsHoliday || officialDay.IsAdjustedWorkday)
            {
                metadata.Children.Add(new TextBlock
                {
                    Text = officialDay.IsHoliday ? "\u4F11" : "\u73ED",
                    Foreground = button.Foreground,
                    FontSize = 9,
                    FontWeight = FontWeights.SemiBold
                });
                button.ToolTip = officialDay.Name;
            }
            if (indicator != IslandIndicatorState.Idle)
                metadata.Children.Add(new Ellipse { Width = 5, Height = 5, Fill = IndicatorBrush(indicator), Stroke = day.Date == selectedDate.Date ? Brushes.White : null, StrokeThickness = day.Date == selectedDate.Date ? 1 : 0, Margin = new Thickness(officialDay.Kind == OfficialCalendarDayKind.None ? 0 : 3, 3, 0, 0) });
            if (metadata.Children.Count > 0)
                content.Children.Add(metadata);
            button.Content = content;
            button.Click += Day_Click;
            CalendarGrid.Children.Add(button);
        }
    }

    void BuildDayAgenda()
    {
        SelectedDateTitle.Text = selectedDate.Date == DateTime.Today ? "今天的安排" : selectedDate.ToString("M 月 d 日");
        var officialDay = holidays.Get(selectedDate);
        if (officialDay.IsHoliday)
            SelectedDateTitle.Text += $" \u00B7 {officialDay.Name}\uFF08\u6CD5\u5B9A\u8282\u5047\u65E5\uFF09";
        else if (officialDay.IsAdjustedWorkday)
            SelectedDateTitle.Text += " \u00B7 \u8C03\u4F11\u4E0A\u73ED";
        UpdateQuickAddDate();
        DayAgendaList.Children.Clear();
        var items = data.AgendaFor(selectedDate);
        if (items.Count == 0)
        {
            DayAgendaList.Children.Add(new TextBlock { Text = "这一天还没有安排", Foreground = new SolidColorBrush(Color.FromRgb(142, 142, 147)), FontSize = 12 });
            return;
        }

        foreach (var item in items)
        {
            var row = new Border { Background = new SolidColorBrush(Color.FromRgb(36, 36, 40)), CornerRadius = new CornerRadius(7), Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 5) };
            var panel = new DockPanel { LastChildFill = false };
            if (item.Kind == "todo")
            {
                var complete = new Button { Content = item.IsCompleted ? "✓" : "○", Width = 25, Height = 25, Padding = new Thickness(0), Background = item.IsCompleted ? new SolidColorBrush(Color.FromRgb(70, 101, 75)) : new SolidColorBrush(Color.FromRgb(58, 58, 62)), Tag = item };
                complete.Click += CompleteTodo_Click;
                DockPanel.SetDock(complete, Dock.Left);
                panel.Children.Add(complete);
            }
            var copy = new StackPanel { Margin = new Thickness(item.Kind == "todo" ? 8 : 0, 0, 0, 0) };
            var time = item.Kind == "event" && item.EndsAt is not null ? $"{item.StartsAt:HH:mm}–{item.EndsAt:HH:mm}" : item.StartsAt.TimeOfDay == TimeSpan.Zero ? "待办" : item.StartsAt.ToString("HH:mm");
            copy.Children.Add(new TextBlock { Text = time, Foreground = new SolidColorBrush(Color.FromRgb(124, 196, 127)), FontSize = 11 });
            copy.Children.Add(new TextBlock { Text = item.Title, Foreground = item.IsCompleted ? new SolidColorBrush(Color.FromRgb(142, 142, 147)) : Brushes.White, TextDecorations = item.IsCompleted ? TextDecorations.Strikethrough : null });
            panel.Children.Add(copy);
            var remove = new Button { Content = "\u00D7", Width = 26, Height = 25, Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(0), Background = new SolidColorBrush(Color.FromRgb(58, 58, 62)), Foreground = new SolidColorBrush(Color.FromRgb(255, 120, 120)), Tag = item, ToolTip = item.Kind == "recurring" ? "删除整个周期计划" : "删除" };
            remove.Click += DeleteAgenda_Click;
            DockPanel.SetDock(remove, Dock.Right);
            panel.Children.Add(remove);
            row.Child = panel;
            DayAgendaList.Children.Add(row);
        }
    }

    void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractiveSource(e.OriginalSource as DependencyObject)) return;
        Touch();
        dragging = true;
        dragged = false;
        dragStart = e.GetPosition(this);
        Header.CaptureMouse();
    }

    void Header_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!dragging || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(this);
        if (Math.Abs(point.X - dragStart.X) <= 5 && Math.Abs(point.Y - dragStart.Y) <= 5) return;
        dragged = true;
        dragging = false;
        Header.ReleaseMouseCapture();
        try { DragMove(); SnapToTop(); } catch (InvalidOperationException) { }
    }

    void Header_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractiveSource(e.OriginalSource as DependencyObject)) return;
        if (dragging && !dragged)
        {
            ToggleExpanded();
            e.Handled = true;
        }
        dragging = false;
        Header.ReleaseMouseCapture();
        if (dragged) SnapToTop();
    }

    void IslandSurface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || IsInteractiveSource(e.OriginalSource as DependencyObject)) return;
        ToggleExpanded();
    }

    static bool IsInteractiveSource(DependencyObject? source)
    {
        for (var current = source; current is not null; current = current switch
        {
            FrameworkElement element when element.Parent is not null => element.Parent,
            FrameworkContentElement element => element.Parent,
            _ => VisualTreeHelper.GetParent(current)
        })
        {
            if (current is Button or TextBox or System.Windows.Controls.ComboBox or System.Windows.Controls.Primitives.ScrollBar) return true;
        }
        return false;
    }

    void ToggleExpanded()
    {
        if (expanded) Collapse();
        else Expand();
    }

    void Expand()
    {
        if (expanded) return;
        ResizeIsland(ExpandedWidth);
        expanded = true;
        ExpandedContent.Opacity = 0;
        ExpandedContent.Visibility = Visibility.Visible;
        ExpandedContent.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        ChevronRotate.Angle = 90;
        Touch();
    }

    void Collapse()
    {
        if (!expanded) return;
        expanded = false;
        ExpandedContent.Visibility = Visibility.Collapsed;
        ChevronRotate.Angle = 0;
        ResizeIsland(CollapsedWidth);
        collapseTimer.Stop();
    }

    void ResizeIsland(double width)
    {
        var delta = width - Width;
        Width = width;
        Left -= delta / 2;
    }

    void Touch() => collapseTimer.Stop();

    void Island_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => collapseTimer.Stop();

    void Island_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!expanded) return;
        collapseTimer.Stop();
        collapseTimer.Start();
    }

    void ExpandedContent_MouseMove(object sender, System.Windows.Input.MouseEventArgs e) => Touch();
    void QuickAddInput_TextChanged(object sender, TextChangedEventArgs e) => Touch();
    void TimeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e) => Touch();

    void InitializeQuickAdd()
    {
        for (var hour = 0; hour < 24; hour++) HourSelector.Items.Add(new TimePart(hour));
        for (var minute = 0; minute < 60; minute++) MinuteSelector.Items.Add(new TimePart(minute));
        HourSelector.SelectedIndex = DateTime.Now.Hour;
        MinuteSelector.SelectedIndex = DateTime.Now.Minute;
        UpdateQuickAddType();
    }

    void ReminderType_Click(object sender, RoutedEventArgs e)
    {
        addingReminder = true;
        UpdateQuickAddType();
        Touch();
    }

    void TodoType_Click(object sender, RoutedEventArgs e)
    {
        addingReminder = false;
        UpdateQuickAddType();
        Touch();
    }

    void UpdateQuickAddType()
    {
        var active = new SolidColorBrush(Color.FromRgb(72, 72, 74));
        var inactive = new SolidColorBrush(Color.FromRgb(44, 44, 46));
        ReminderTypeButton.Background = addingReminder ? active : inactive;
        ReminderTypeButton.Foreground = addingReminder ? Brushes.White : new SolidColorBrush(Color.FromRgb(184, 184, 191));
        TodoTypeButton.Background = addingReminder ? inactive : active;
        TodoTypeButton.Foreground = addingReminder ? new SolidColorBrush(Color.FromRgb(184, 184, 191)) : Brushes.White;
        UpdateQuickAddDate();
    }

    void UpdateQuickAddDate() => QuickAddDate.Text = $"{selectedDate:M'\u6708'd'\u65e5'} · {(addingReminder ? "\u63d0\u9192" : "\u5f85\u529e")}";

    void QuickAdd_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(QuickAddInput.Text) || HourSelector.SelectedItem is not TimePart hour || MinuteSelector.SelectedItem is not TimePart minute) return;
        var scheduledAt = selectedDate.Date.AddHours(hour.Value).AddMinutes(minute.Value);
        if (addingReminder)
        {
            data.SaveReminder(QuickAddInput.Text, null, scheduledAt);
            reminders.RefreshSchedule();
        }
        else data.Save(QuickAddInput.Text, null, scheduledAt, null);
        QuickAddInput.Clear();
        Touch();
    }

    sealed record TimePart(int Value)
    {
        public override string ToString() => Value.ToString("00");
    }

    void CompleteTodo_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AgendaItem item || item.IsCompleted) return;
        data.Complete(item.Id);
        reminders.Cancel(item);
        Touch();
    }

    void DismissReminderBanner_Click(object sender, RoutedEventArgs e)
    {
        ClearReminderBanner();
        Refresh();
        Touch();
    }

    void DeleteAgenda_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AgendaItem item) return;
        reminders.Delete(item);
        Touch();
    }

    void Day_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DateTime day)
        {
            selectedDate = day.Date;
            BuildCalendar();
            BuildDayAgenda();
        }
        Touch();
    }

    void PreviousMonth_Click(object sender, RoutedEventArgs e)
    {
        displayedMonth = displayedMonth.AddMonths(-1);
        selectedDate = displayedMonth;
        BuildCalendar();
        BuildDayAgenda();
        Touch();
    }

    void NextMonth_Click(object sender, RoutedEventArgs e)
    {
        displayedMonth = displayedMonth.AddMonths(1);
        selectedDate = displayedMonth;
        BuildCalendar();
        BuildDayAgenda();
        Touch();
    }

    void SnapToTop()
    {
        var area = CurrentScreenWorkArea();
        var distance = Top - area.Top;
        if ((!notch && distance < SnapThreshold) || (notch && distance < UnsnapThreshold))
        {
            notch = true;
            Top = area.Top;
            Left = area.Left + (area.Width - ActualWidth) / 2;
            Outer.Margin = new Thickness(0);
            MainBorder.CornerRadius = new CornerRadius(0, 0, 18, 18);
        }
        else if (notch)
        {
            notch = false;
            Outer.Margin = new Thickness(10, 8, 10, 12);
            MainBorder.CornerRadius = new CornerRadius(20);
        }
    }

    void MascotButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        OpenAssistant();
    }

    void OpenAssistant()
    {
        Collapse();
        Dispatcher.BeginInvoke(() => OpenRequested?.Invoke(this, EventArgs.Empty));
    }

    void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    void OpenSettings()
    {
        Collapse();
        Dispatcher.BeginInvoke(() => SettingsRequested?.Invoke(this, EventArgs.Empty));
    }

    void OpenAssistant_Click(object sender, RoutedEventArgs e) => OpenAssistant();
}

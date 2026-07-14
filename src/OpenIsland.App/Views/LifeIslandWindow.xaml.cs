using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using OpenIsland.App.Services;

namespace OpenIsland.App.Views;

public partial class LifeIslandWindow : Window
{
    const double CollapsedWidth = 340;
    const double ExpandedWidth = 520;
    const double SnapThreshold = 28;
    const double UnsnapThreshold = 48;

    readonly LifeDataService data;
    readonly ReminderService reminders;
    readonly DispatcherTimer clockTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    readonly DispatcherTimer collapseTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    DateTime displayedMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    DateTime selectedDate = DateTime.Today;
    bool dragging;
    bool dragged;
    bool expanded;
    bool notch;
    System.Windows.Point dragStart;
    bool addingReminder = true;

    public event EventHandler? OpenRequested;

    public LifeIslandWindow(LifeDataService data, ReminderService reminders)
    {
        InitializeComponent();
        this.data = data;
        this.reminders = reminders;
        clockTimer.Tick += (_, _) => Refresh();
        collapseTimer.Tick += (_, _) => Collapse();
        Loaded += (_, _) =>
        {
            InitializeQuickAdd();
            PositionAtTopCenter();
            Refresh();
            clockTimer.Start();
        };
        data.AgendaChanged += (_, _) => Dispatcher.BeginInvoke(Refresh);
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
        }
        Expand();
        Refresh();
        Touch();
    }

    void PositionAtTopCenter()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Top + 10;
    }

    void Refresh()
    {
        Clock.Text = DateTime.Now.ToString("HH:mm");
        StatusLight.Fill = IndicatorBrush(data.GetIslandIndicatorState(DateTime.Now));
        var next = data.NextAgenda();
        Summary.Text = next is null ? "今天暂无安排" : $"下一项 · {next.StartsAt:HH:mm} {next.Title}";
        BuildCalendar();
        BuildDayAgenda();
    }

    static System.Windows.Media.Brush IndicatorBrush(IslandIndicatorState state) => state switch
    {
        IslandIndicatorState.OverdueTodo => new SolidColorBrush(Color.FromRgb(255, 69, 58)),
        IslandIndicatorState.DueSoonTodo => new SolidColorBrush(Color.FromRgb(255, 159, 10)),
        IslandIndicatorState.PendingTodo => new SolidColorBrush(Color.FromRgb(10, 132, 255)),
        IslandIndicatorState.ReminderOnly => new SolidColorBrush(Color.FromRgb(255, 214, 10)),
        _ => new SolidColorBrush(Color.FromRgb(48, 209, 88))
    };
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
            var button = new Button
            {
                Tag = day,
                Height = 42,
                Style = (Style)FindResource("IslandDay"),
                Background = day.Date == selectedDate.Date ? new SolidColorBrush(Color.FromRgb(10, 132, 255)) : Brushes.Transparent,
                Foreground = inMonth ? Brushes.White : new SolidColorBrush(Color.FromRgb(92, 92, 98))
            };
            var content = new StackPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(new TextBlock { Text = day.Day.ToString(), HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Foreground = button.Foreground });
            if (indicator != IslandIndicatorState.Idle)
                content.Children.Add(new Ellipse { Width = 5, Height = 5, Fill = IndicatorBrush(indicator), Stroke = day.Date == selectedDate.Date ? Brushes.White : null, StrokeThickness = day.Date == selectedDate.Date ? 1 : 0, Margin = new Thickness(0, 3, 0, 0) });
            button.Content = content;
            button.Click += Day_Click;
            CalendarGrid.Children.Add(button);
        }
    }

    void BuildDayAgenda()
    {
        SelectedDateTitle.Text = selectedDate.Date == DateTime.Today ? "今天的安排" : selectedDate.ToString("M 月 d 日");
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
        if (dragging && !dragged) ToggleExpanded();
        dragging = false;
        Header.ReleaseMouseCapture();
        if (dragged) SnapToTop();
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
        var active = new SolidColorBrush(Color.FromRgb(10, 132, 255));
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
        var area = SystemParameters.WorkArea;
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

    void OpenAssistant_Click(object sender, RoutedEventArgs e)
    {
        Collapse();
        OpenRequested?.Invoke(this, EventArgs.Empty);
    }
}

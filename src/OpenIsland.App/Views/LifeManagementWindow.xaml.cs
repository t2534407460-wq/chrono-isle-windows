using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using OpenIsland.App.Services;
using OpenIsland.App.Services.Productivity;
using OpenIsland.App.Services.Domain;

namespace OpenIsland.App.Views;

public partial class LifeManagementWindow : Window
{
    readonly LifeDataService data;
    readonly ReminderService reminders;
    readonly TaskAttributesService taskAttributes;
    readonly FocusService focus;
    readonly HashSet<string> selected = [];
    readonly Dictionary<string, Border> itemRows = [];
    readonly Dictionary<string, Border> itemRowsById = [];
    static readonly string[] CommonCategories = ["工作", "学习", "生活", "健康", "家庭", "财务", "其他"];
    ItemNavigationTarget? itemToLocate;
    bool awaitingConfirmation;
    bool showingArchive;

    public LifeManagementWindow(LifeDataService data, ReminderService reminders, FocusService focus, TaskAttributesService taskAttributes)
    {
        InitializeComponent();
        this.data = data;
        this.reminders = reminders;
        this.focus = focus;
        Loaded += (_, _) => RefreshItems();
        this.taskAttributes = taskAttributes;
    }

    void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else DragMove();
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
    public void OpenItem(ItemNavigationTarget target) => itemToLocate = target;

    void RefreshItems()
    {
        data.ArchiveCompletedAndOverdue();
        UpdatePageTabs();
        if (showingArchive)
        {
            RefreshArchivedItems();
            return;
        }
        var managed = data.ManagedItems().ToList();
        var itemIndicators = data.GetManagedItemIndicatorStates(managed, DateTime.Now);
        selected.RemoveWhere(key => !managed.Any(item => Key(item.Id, item.Kind) == key));
        Items.Children.Clear();
        itemRows.Clear();
        itemRowsById.Clear();

        foreach (var item in managed)
        {
            var agenda = new AgendaItem(item.Id, item.Kind, item.Title, item.Notes, item.ScheduledAt ?? DateTime.Now, null, item.ScheduledAt, item.IsCompleted, item.Kind == "recurring");
            var row = new Border { Background = new SolidColorBrush(Color.FromRgb(36, 36, 38)), CornerRadius = new CornerRadius(10), Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 8), Tag = agenda };
            var panel = new Grid();
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(82) });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });

            var check = new CheckBox { Style = (Style)FindResource("SelectionBox"), Tag = agenda, IsChecked = selected.Contains(Key(agenda)) };
            check.Checked += (_, _) => SetSelected(agenda, true);
            check.Unchecked += (_, _) => SetSelected(agenda, false);
            Grid.SetColumn(check, 0);
            panel.Children.Add(check);

            var text = new StackPanel { Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            var title = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            var indicator = itemIndicators[item.Id];
            title.Children.Add(new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = LifeIslandWindow.IndicatorBrush(indicator), ToolTip = LifeIslandWindow.IndicatorTooltip(indicator), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
            title.Children.Add(new TextBlock { Text = item.Title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(title);
            text.Children.Add(new TextBlock { Text = ItemDetails(item), Foreground = new SolidColorBrush(Color.FromRgb(152, 152, 157)), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(text, 1);
            panel.Children.Add(text);

            if (agenda.Kind == "todo" && !agenda.IsCompleted)
            {
                var startFocus = new Button { Content = "开始专注", Height = 32, Style = (Style)FindResource("Action"), Background = new SolidColorBrush(Color.FromRgb(30, 82, 120)), Tag = agenda, ToolTip = "开始 25 分钟专注" };
                startFocus.Click += (sender, _) => StartFocus((AgendaItem)((FrameworkElement)sender).Tag);
                Grid.SetColumn(startFocus, 2);
                panel.Children.Add(startFocus);
            }

            var remove = new Button { Content = "\u00D7", Width = 32, Height = 32, FontSize = 18, FontWeight = FontWeights.SemiBold, Style = (Style)FindResource("Action"), Background = new SolidColorBrush(Color.FromRgb(74, 37, 40)), Foreground = new SolidColorBrush(Color.FromRgb(255, 120, 120)), Tag = agenda, ToolTip = agenda.Kind == "recurring" ? "\u5220\u9664\u6574\u4E2A\u5468\u671F\u8BA1\u5212" : "\u5220\u9664" };
            remove.Click += (sender, _) => DeleteOne((AgendaItem)((FrameworkElement)sender).Tag);
            Grid.SetColumn(remove, 3);
            panel.Children.Add(remove);

            row.Child = agenda.Kind is "todo" or "reminder" && taskAttributes.Get(agenda.Id) is { } attributes ? BuildTaskAttributesEditor(panel, attributes) : panel;
            itemRows[Key(agenda)] = row;
            itemRowsById[agenda.Id] = row;
            Items.Children.Add(row);
        }
        UpdateSelectionUi();
        LocateRequestedItem();
    }

    void ActiveTab_Click(object sender, RoutedEventArgs e)
    {
        showingArchive = false;
        RefreshItems();
    }

    void ArchiveTab_Click(object sender, RoutedEventArgs e)
    {
        showingArchive = true;
        RefreshItems();
    }

    void UpdatePageTabs()
    {
        ActiveTab.Background = new SolidColorBrush(showingArchive ? Color.FromRgb(44, 44, 46) : Color.FromRgb(70, 70, 74));
        ArchiveTab.Background = new SolidColorBrush(showingArchive ? Color.FromRgb(70, 70, 74) : Color.FromRgb(44, 44, 46));
        DeleteSelected.Visibility = showingArchive ? Visibility.Collapsed : Visibility.Visible;
    }

    void RefreshArchivedItems()
    {
        selected.Clear();
        Items.Children.Clear();
        foreach (var item in data.ArchivedTodos())
        {
            var row = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(36, 36, 38)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14),
                Margin = new Thickness(0, 0, 0, 8)
            };
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = item.Title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            panel.Children.Add(new TextBlock
            {
                Text = $"{item.Reason} · 归档于 {item.ArchivedAt:yyyy-MM-dd HH:mm} · 将于 {item.ArchivedAt.AddDays(7):MM-dd HH:mm} 自动删除",
                Foreground = new SolidColorBrush(Color.FromRgb(152, 152, 157)),
                FontSize = 12,
                Margin = new Thickness(0, 4, 0, 10)
            });
            var controls = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            controls.Children.Add(new TextBlock { Text = "恢复日期", Foreground = new SolidColorBrush(Color.FromRgb(184, 184, 191)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            var date = ArchiveDateSelector(DateTime.Today.AddDays(1));
            var restore = new Button { Content = "恢复", Height = 30, Style = (Style)FindResource("Action"), Background = new SolidColorBrush(Color.FromRgb(30, 82, 120)), Padding = new Thickness(12, 4, 12, 4) };
            restore.Click += (_, _) => RestoreArchivedTodo(item, date.Tag is DateTime selectedDate ? selectedDate : null);
            controls.Children.Add(date);
            controls.Children.Add(restore);
            panel.Children.Add(controls);
            row.Child = panel;
            Items.Children.Add(row);
        }
        if (Items.Children.Count == 0)
            Items.Children.Add(new TextBlock { Text = "暂无过期事项。", Foreground = new SolidColorBrush(Color.FromRgb(152, 152, 157)), Margin = new Thickness(0, 8, 0, 0) });
    }

    Button ArchiveDateSelector(DateTime initialDate)
    {
        var selected = initialDate.Date;
        var button = new Button
        {
            Content = selected.ToString("yyyy-MM-dd"),
            Tag = selected,
            Height = 30,
            Style = (Style)FindResource("Action"),
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(10, 4, 10, 4)
        };
        var popup = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = button,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true
        };
        var calendar = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(28, 28, 30)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(92, 100, 114)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 6, 0, 0),
            Width = 264
        };
        popup.Child = calendar;
        var displayedMonth = new DateTime(selected.Year, selected.Month, 1);
        void RenderMonth()
        {
            var panel = new StackPanel();
            var header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var previous = new Button { Content = "‹", Width = 28, Height = 28, Style = (Style)FindResource("Action"), Padding = new Thickness(0) };
            previous.Click += (_, _) => { displayedMonth = displayedMonth.AddMonths(-1); RenderMonth(); };
            var title = new TextBlock { Text = displayedMonth.ToString("yyyy 年 M 月"), FontWeight = FontWeights.SemiBold, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var next = new Button { Content = "›", Width = 28, Height = 28, Style = (Style)FindResource("Action"), Padding = new Thickness(0) };
            next.Click += (_, _) => { displayedMonth = displayedMonth.AddMonths(1); RenderMonth(); };
            Grid.SetColumn(title, 1);
            Grid.SetColumn(next, 2);
            header.Children.Add(previous);
            header.Children.Add(title);
            header.Children.Add(next);
            panel.Children.Add(header);

            var weekdays = new System.Windows.Controls.Primitives.UniformGrid { Columns = 7, Margin = new Thickness(0, 0, 0, 3) };
            foreach (var weekday in new[] { "一", "二", "三", "四", "五", "六", "日" })
                weekdays.Children.Add(new TextBlock { Text = weekday, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(152, 152, 157)), HorizontalAlignment = System.Windows.HorizontalAlignment.Center });
            panel.Children.Add(weekdays);

            var days = new System.Windows.Controls.Primitives.UniformGrid { Columns = 7 };
            var offset = ((int)displayedMonth.DayOfWeek + 6) % 7;
            for (var index = 0; index < offset; index++) days.Children.Add(new Border { Height = 28 });
            for (var day = 1; day <= DateTime.DaysInMonth(displayedMonth.Year, displayedMonth.Month); day++)
            {
                var value = displayedMonth.AddDays(day - 1);
                var cell = new Button { Content = day.ToString(), Height = 28, Style = (Style)FindResource("Action"), Padding = new Thickness(0), Margin = new Thickness(1) };
                if (value == selected) cell.Background = new SolidColorBrush(Color.FromRgb(70, 70, 74));
                cell.Click += (_, _) =>
                {
                    selected = value;
                    button.Tag = selected;
                    button.Content = selected.ToString("yyyy-MM-dd");
                    popup.IsOpen = false;
                };
                days.Children.Add(cell);
            }
            panel.Children.Add(days);
            calendar.Child = panel;
        }
        RenderMonth();
        button.Click += (_, _) => popup.IsOpen = !popup.IsOpen;
        return button;
    }

    void RestoreArchivedTodo(ArchivedTodoItem item, DateTime? selectedDate)
    {
        if (selectedDate is null)
        {
            Result.Foreground = new SolidColorBrush(Color.FromRgb(255, 120, 120));
            Result.Text = "请选择恢复日期。";
            return;
        }
        var time = item.DueAt?.TimeOfDay ?? TimeSpan.FromHours(9);
        var scheduledAt = selectedDate.Value.Date.Add(time);
        if (!data.RestoreArchivedTodo(item.Id, scheduledAt))
        {
            Result.Foreground = new SolidColorBrush(Color.FromRgb(255, 120, 120));
            Result.Text = "恢复日期需要晚于当前时间。";
            return;
        }
        Result.Foreground = new SolidColorBrush(Color.FromRgb(157, 214, 157));
        Result.Text = $"已恢复到 {scheduledAt:yyyy-MM-dd HH:mm}。";
        RefreshItems();
    }

    void LocateRequestedItem()
    {
        var target = itemToLocate;
        itemToLocate = null;
        if (target is null || !itemRows.TryGetValue(Key(target.Id, target.Kind), out var row) && !itemRowsById.TryGetValue(target.Id, out row)) return;
        Dispatcher.BeginInvoke(() =>
        {
            ItemsScroller.UpdateLayout();
            row.BringIntoView();
            FlashItem(row);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    static void FlashItem(Border row)
    {
        var baseColor = row.Background is SolidColorBrush brush ? brush.Color : Color.FromRgb(36, 36, 38);
        var highlight = new SolidColorBrush(baseColor);
        row.Background = highlight;
        highlight.BeginAnimation(SolidColorBrush.ColorProperty, new System.Windows.Media.Animation.ColorAnimation(baseColor, Color.FromRgb(55, 96, 145), TimeSpan.FromMilliseconds(260))
        {
            AutoReverse = true,
            RepeatBehavior = new System.Windows.Media.Animation.RepeatBehavior(2)
        });
    }
    StackPanel BuildTaskAttributesEditor(Grid row, TaskAttributes attributes)
    {
        var container = new StackPanel();
        container.Children.Add(row);
        var editor = new WrapPanel { Margin = new Thickness(36, 8, 0, 0) };
        var priorityOptions = Enum.GetValues<LifePriority>().Select(value => new ComboBoxItem { Content = TaskDisplayLabels.Priority(value), Tag = value }).ToArray();
        var priority = new System.Windows.Controls.ComboBox { Width = 76, Height = 27, ItemsSource = priorityOptions, SelectedItem = priorityOptions.Single(item => (LifePriority)item.Tag == attributes.Priority), Margin = new Thickness(0, 0, 8, 4), ToolTip = "优先级" };
        var category = new System.Windows.Controls.ComboBox { Width = 86, Height = 27, ItemsSource = CommonCategories, IsEditable = true, Text = attributes.Category ?? "", Margin = new Thickness(0, 0, 5, 4), ToolTip = "分类" };
        var estimatedMinutes = attributes.EstimatedMinutes ?? 30;
        var estimateUnitMinutes = estimatedMinutes >= 60 && estimatedMinutes % 60 == 0 ? 60 : 1;
        var estimateStepper = PositiveNumberStepper(estimatedMinutes / estimateUnitMinutes, "预计时长", out var estimateValue);
        var estimateUnit = TimeUnitSelector(estimateUnitMinutes);
        var energyOptions = new ComboBoxItem[] { new() { Content = "不限", Tag = null } }
            .Concat(Enum.GetValues<EnergyLevel>().Select(value => new ComboBoxItem { Content = TaskDisplayLabels.Energy(value), Tag = value })).ToArray();
        var energy = new System.Windows.Controls.ComboBox { Width = 92, Height = 27, ItemsSource = energyOptions, SelectedItem = energyOptions.Single(item => Equals(item.Tag, attributes.Energy)), Margin = new Thickness(0, 0, 5, 4), ToolTip = "能量" };
        var overdueGraceStepper = PositiveNumberStepper(attributes.OverdueGraceMinutes, "超时宽限分钟", out var overdueGrace);
        var details = new WrapPanel { Margin = new Thickness(36, 4, 0, 0), Visibility = Visibility.Collapsed };
        var more = new Button { Content = "更多设置 ▾", Height = 27, Style = (Style)FindResource("Action"), Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 4) };
        more.Click += (_, _) =>
        {
            var expanded = details.Visibility != Visibility.Visible;
            details.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            more.Content = expanded ? "收起设置 ▴" : "更多设置 ▾";
        };
        var save = new Button { Content = "保存", Height = 27, Style = (Style)FindResource("Action"), Background = new SolidColorBrush(Color.FromRgb(30, 82, 120)), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 0, 4) };
        save.Click += (_, _) => SaveTaskAttributes(attributes, priority, category, estimateValue, estimateUnit, energy, overdueGrace);
        editor.Children.Add(new TextBlock { Text = "优先级", Foreground = new SolidColorBrush(Color.FromRgb(152, 152, 157)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 4) });
        editor.Children.Add(priority);
        editor.Children.Add(new TextBlock { Text = "超时宽限", Foreground = new SolidColorBrush(Color.FromRgb(152, 152, 157)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 3, 4) });
        editor.Children.Add(overdueGraceStepper);
        editor.Children.Add(new TextBlock { Text = "分钟", Foreground = new SolidColorBrush(Color.FromRgb(152, 152, 157)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 4) });
        editor.Children.Add(more);
        editor.Children.Add(save);
        details.Children.Add(new TextBlock { Text = "分类", Foreground = new SolidColorBrush(Color.FromRgb(152, 152, 157)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 4) });
        details.Children.Add(category);
        details.Children.Add(new TextBlock { Text = "预计", Foreground = new SolidColorBrush(Color.FromRgb(152, 152, 157)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 4) });
        details.Children.Add(estimateStepper);
        details.Children.Add(estimateUnit);
        details.Children.Add(new TextBlock { Text = "能量", Foreground = new SolidColorBrush(Color.FromRgb(152, 152, 157)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 4) });
        details.Children.Add(energy);
        var completed = attributes.CompletedAtUtc is null ? "未完成" : attributes.CompletedAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        details.Children.Add(new TextBlock
        {
            Text = $"完成：{completed} · 已延期 {attributes.DeferredCount} 次",
            Foreground = new SolidColorBrush(Color.FromRgb(152, 152, 157)), FontSize = 11, Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis
        });
        container.Children.Add(editor);
        container.Children.Add(details);
        return container;
    }

    TextBox EditableTextBox(double width, string text, string toolTip) => new()
    {
        Width = width,
        Height = 27,
        Text = text,
        Margin = new Thickness(0, 0, 5, 4),
        ToolTip = toolTip,
        Style = (Style)FindResource("EditableInput")
    };

    Grid PositiveNumberStepper(int initialValue, string toolTip, out TextBox valueBox)
    {
        var box = EditableTextBox(42, TaskAttributeNumbers.Normalize(initialValue).ToString(), toolTip);
        box.Margin = new Thickness(0, 0, 0, 4);
        box.IsReadOnly = true;
        box.IsTabStop = false;
        box.TextAlignment = TextAlignment.Center;
        valueBox = box;

        var up = StepperButton("▲", () => box.Text = TaskAttributeNumbers.Increase(StepperValue(box)).ToString());
        var down = StepperButton("▼", () => box.Text = TaskAttributeNumbers.Decrease(StepperValue(box)).ToString());
        var arrows = new StackPanel { Orientation = System.Windows.Controls.Orientation.Vertical, Margin = new Thickness(2, 0, 5, 4) };
        arrows.Children.Add(up);
        arrows.Children.Add(down);

        var stepper = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        stepper.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        stepper.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        stepper.Children.Add(box);
        Grid.SetColumn(arrows, 1);
        stepper.Children.Add(arrows);
        return stepper;
    }

    System.Windows.Controls.ComboBox TimeUnitSelector(int selectedMinutes) => new()
    {
        Width = 58,
        Height = 27,
        ItemsSource = new[]
        {
            new ComboBoxItem { Content = "分钟", Tag = 1 },
            new ComboBoxItem { Content = "小时", Tag = 60 }
        },
        SelectedIndex = selectedMinutes == 60 ? 1 : 0,
        Margin = new Thickness(0, 0, 5, 4),
        ToolTip = "时间单位"
    };

    Button StepperButton(string content, Action action)
    {
        var button = new Button { Content = content, Width = 18, Height = 13, MinHeight = 13, Padding = new Thickness(0), FontSize = 8, Style = (Style)FindResource("Action") };
        button.Click += (_, _) => action();
        return button;
    }

    static int StepperValue(TextBox valueBox) => int.TryParse(valueBox.Text, out var value) ? TaskAttributeNumbers.Normalize(value) : 1;

    void SaveTaskAttributes(TaskAttributes original, System.Windows.Controls.ComboBox priorityBox, System.Windows.Controls.ComboBox categoryBox, TextBox estimateValueBox, System.Windows.Controls.ComboBox estimateUnitBox, System.Windows.Controls.ComboBox energyBox, TextBox overdueGraceBox)
    {
        if (priorityBox.SelectedItem is not ComboBoxItem { Tag: LifePriority priority }) return;
        if (!int.TryParse(estimateValueBox.Text.Trim(), out var estimateValue) || estimateValue <= 0)
        {
            Result.Foreground = new SolidColorBrush(Color.FromRgb(255, 120, 120));
            Result.Text = "预计时长必须大于 0。";
            return;
        }
        var unitMinutes = estimateUnitBox.SelectedItem is ComboBoxItem { Tag: int unit } ? unit : 1;
        if (estimateValue > int.MaxValue / unitMinutes)
        {
            Result.Foreground = new SolidColorBrush(Color.FromRgb(255, 120, 120));
            Result.Text = "预计时长过大。";
            return;
        }
        if (!int.TryParse(overdueGraceBox.Text.Trim(), out var overdueGrace) || overdueGrace <= 0)
        {
            Result.Foreground = new SolidColorBrush(Color.FromRgb(255, 120, 120));
            Result.Text = "超时宽限必须是正整数分钟。";
            return;
        }
        var updated = original with
        {
            Priority = priority,
            Category = categoryBox.Text,
            EstimatedMinutes = estimateValue * unitMinutes,
            Energy = energyBox.SelectedItem is ComboBoxItem { Tag: EnergyLevel energy } ? energy : null,
            OverdueGraceMinutes = overdueGrace
        };
        var result = taskAttributes.Update(updated);
        Result.Foreground = result == TaskAttributesUpdateResult.Succeeded
            ? new SolidColorBrush(Color.FromRgb(157, 214, 157))
            : new SolidColorBrush(Color.FromRgb(255, 120, 120));
        Result.Text = result switch
        {
            TaskAttributesUpdateResult.Succeeded => "任务属性已保存。",
            TaskAttributesUpdateResult.ConcurrentConflict => "事项刚被其他操作更新，请刷新后再保存。",
            TaskAttributesUpdateResult.ReadOnly => "只读事项不能修改属性。",
            _ => "未找到可编辑待办。"
        };
        if (result == TaskAttributesUpdateResult.Succeeded) RefreshItems();
    }

    void StartFocus(AgendaItem item)
    {
        try
        {
            var active = focus.RestoreActive();
            if (active is not null)
            {
                Result.Foreground = new SolidColorBrush(Color.FromRgb(157, 214, 157));
                Result.Text = active.ItemId == item.Id
                    ? $"正在专注：{item.Title}。"
                    : "已有进行中的专注，请先在灵动岛结束当前专注。";
                return;
            }
            var session = focus.Start(item.Id, 25);
            Result.Foreground = new SolidColorBrush(Color.FromRgb(157, 214, 157));
            Result.Text = $"已开始专注：{item.Title}（25 分钟）。灵动岛会显示倒计时。";
        }
        catch (Exception exception)
        {
            Result.Foreground = new SolidColorBrush(Color.FromRgb(255, 120, 120));
            Result.Text = $"无法开始专注：{exception.Message}";
        }
    }

    void SetSelected(AgendaItem item, bool isSelected)
    {
        if (isSelected) selected.Add(Key(item));
        else selected.Remove(Key(item));
        awaitingConfirmation = false;
        Result.Text = "";
        UpdateSelectionUi();
    }

    void DeleteOne(AgendaItem item)
    {
        reminders.Delete(item);
        selected.Remove(Key(item));
        awaitingConfirmation = false;
        Result.Text = "\u5DF2\u5220\u9664\u4E8B\u9879\u3002";
        RefreshItems();
    }

    void UpdateSelectionUi() => DeleteSelected.Content = selected.Count == 0
        ? "\u6279\u91CF\u5220\u9664"
        : $"\u5220\u9664\u5DF2\u9009 {selected.Count} \u9879";

    void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var targets = Items.Children.OfType<Border>().Select(border => border.Tag).OfType<AgendaItem>().Where(item => selected.Contains(Key(item))).ToList();
        if (targets.Count == 0)
        {
            Result.Text = "\u8BF7\u5148\u9009\u62E9\u4E8B\u9879\u3002";
            return;
        }
        if (!awaitingConfirmation)
        {
            awaitingConfirmation = true;
            Result.Text = $"\u518D\u6B21\u70B9\u51FB\u6279\u91CF\u5220\u9664\uFF0C\u786E\u8BA4\u5220\u9664 {targets.Count} \u9879\u3002";
            return;
        }
        reminders.Delete(targets);
        selected.Clear();
        awaitingConfirmation = false;
        Result.Text = "\u5DF2\u5220\u9664\u6240\u9009\u4E8B\u9879\u3002";
        RefreshItems();
    }

    static string ItemDetails(ManagedLifeItem item) => item.Kind == "recurring"
        ? $"\u5468\u671F\u63D0\u9192 \u00B7 {item.RecurrenceLabel} \u00B7 \u4E0B\u4E00\u6B21 {item.ScheduledAt:MM-dd HH:mm}"
        : $"{item.Kind switch { "event" => "\u65E5\u7A0B", "reminder" => "\u63D0\u9192", _ => "\u5F85\u529E" }} \u00B7 {item.ScheduledAt:yyyy-MM-dd HH:mm}";

    static string Key(AgendaItem item) => Key(item.Id, item.Kind);
    static string Key(string id, string kind) => kind + ":" + id;
}

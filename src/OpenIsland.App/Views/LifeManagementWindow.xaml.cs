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
    bool awaitingConfirmation;

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
    void RefreshItems()
    {
        var managed = data.ManagedItems().ToList();
        selected.RemoveWhere(key => !managed.Any(item => Key(item.Id, item.Kind) == key));
        Items.Children.Clear();

        foreach (var item in managed)
        {
            var agenda = new AgendaItem(item.Id, item.Kind, item.Title, item.Notes, item.ScheduledAt ?? DateTime.Now, null, item.ScheduledAt, item.IsCompleted, item.Kind == "recurring");
            var row = new Border { Background = new SolidColorBrush(Color.FromRgb(36, 36, 38)), CornerRadius = new CornerRadius(10), Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 8), Tag = agenda };
            var panel = new Grid();
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(66) });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });

            var check = new CheckBox { Style = (Style)FindResource("SelectionBox"), Tag = agenda, IsChecked = selected.Contains(Key(agenda)) };
            check.Checked += (_, _) => SetSelected(agenda, true);
            check.Unchecked += (_, _) => SetSelected(agenda, false);
            Grid.SetColumn(check, 0);
            panel.Children.Add(check);

            var text = new StackPanel { Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = item.Title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = ItemDetails(item), Foreground = new SolidColorBrush(Color.FromRgb(152, 152, 157)), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(text, 1);
            panel.Children.Add(text);

            if (agenda.Kind == "todo" && !agenda.IsCompleted)
            {
                var startFocus = new Button { Content = "专注 25", Height = 32, Style = (Style)FindResource("Action"), Background = new SolidColorBrush(Color.FromRgb(30, 82, 120)), Tag = agenda };
                startFocus.Click += (sender, _) => StartFocus((AgendaItem)((FrameworkElement)sender).Tag);
                Grid.SetColumn(startFocus, 2);
                panel.Children.Add(startFocus);
            }

            var remove = new Button { Content = "\u00D7", Width = 32, Height = 32, FontSize = 18, FontWeight = FontWeights.SemiBold, Style = (Style)FindResource("Action"), Background = new SolidColorBrush(Color.FromRgb(74, 37, 40)), Foreground = new SolidColorBrush(Color.FromRgb(255, 120, 120)), Tag = agenda, ToolTip = agenda.Kind == "recurring" ? "\u5220\u9664\u6574\u4E2A\u5468\u671F\u8BA1\u5212" : "\u5220\u9664" };
            remove.Click += (sender, _) => DeleteOne((AgendaItem)((FrameworkElement)sender).Tag);
            Grid.SetColumn(remove, 3);
            panel.Children.Add(remove);

            row.Child = agenda.Kind == "todo" && taskAttributes.Get(agenda.Id) is { } attributes ? BuildTaskAttributesEditor(panel, attributes) : panel;
            Items.Children.Add(row);
        }
        UpdateSelectionUi();
    }
    StackPanel BuildTaskAttributesEditor(Grid row, TaskAttributes attributes)
    {
        var container = new StackPanel();
        container.Children.Add(row);
        var editor = new WrapPanel { Margin = new Thickness(36, 8, 0, 0) };
        var priority = new System.Windows.Controls.ComboBox { Width = 76, Height = 27, ItemsSource = Enum.GetValues<LifePriority>(), SelectedItem = attributes.Priority, Margin = new Thickness(0, 0, 5, 4) };
        var category = new TextBox { Width = 86, Height = 27, Text = attributes.Category ?? "", Margin = new Thickness(0, 0, 5, 4), ToolTip = "类别" };
        var minutes = new TextBox { Width = 70, Height = 27, Text = attributes.EstimatedMinutes?.ToString() ?? "", Margin = new Thickness(0, 0, 5, 4), ToolTip = "预计分钟" };
        var energy = new System.Windows.Controls.ComboBox { Width = 78, Height = 27, ItemsSource = new object?[] { null, EnergyLevel.Low, EnergyLevel.Medium, EnergyLevel.High }, SelectedItem = attributes.Energy, Margin = new Thickness(0, 0, 5, 4), ToolTip = "能量" };
        var save = new Button { Content = "保存属性", Height = 27, Style = (Style)FindResource("Action"), Background = new SolidColorBrush(Color.FromRgb(30, 82, 120)), Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 0, 4) };
        save.Click += (_, _) => SaveTaskAttributes(attributes, priority, category, minutes, energy);
        editor.Children.Add(new TextBlock { Text = "属性", Foreground = new SolidColorBrush(Color.FromRgb(152, 152, 157)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 4) });
        editor.Children.Add(priority);
        editor.Children.Add(category);
        editor.Children.Add(minutes);
        editor.Children.Add(energy);
        editor.Children.Add(save);
        var parent = string.IsNullOrWhiteSpace(attributes.ParentItemId) ? "无" : attributes.ParentItemId;
        var completed = attributes.CompletedAtUtc is null ? "未完成" : attributes.CompletedAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        container.Children.Add(new TextBlock
        {
            Text = $"父任务：{parent} · 完成：{completed} · 已延期 {attributes.DeferredCount} 次",
            Foreground = new SolidColorBrush(Color.FromRgb(152, 152, 157)), FontSize = 11, Margin = new Thickness(36, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis
        });
        container.Children.Add(editor);
        return container;
    }

    void SaveTaskAttributes(TaskAttributes original, System.Windows.Controls.ComboBox priorityBox, TextBox categoryBox, TextBox minutesBox, System.Windows.Controls.ComboBox energyBox)
    {
        if (priorityBox.SelectedItem is not LifePriority priority) return;
        int? minutes = null;
        if (!string.IsNullOrWhiteSpace(minutesBox.Text))
        {
            if (!int.TryParse(minutesBox.Text.Trim(), out var parsedMinutes))
            {
                Result.Foreground = new SolidColorBrush(Color.FromRgb(255, 120, 120));
                Result.Text = "预计时长必须是正整数分钟。";
                return;
            }
            minutes = parsedMinutes;
        }
        if (minutes is <= 0)
        {
            Result.Foreground = new SolidColorBrush(Color.FromRgb(255, 120, 120));
            Result.Text = "预计时长必须大于 0。";
            return;
        }
        var updated = original with
        {
            Priority = priority,
            Category = categoryBox.Text,
            EstimatedMinutes = minutes,
            Energy = energyBox.SelectedItem as EnergyLevel?
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
            var session = focus.Start(item.Id, 25);
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
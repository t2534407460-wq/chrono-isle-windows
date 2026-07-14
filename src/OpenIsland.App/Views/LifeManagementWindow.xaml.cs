using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenIsland.App.Services;

namespace OpenIsland.App.Views;

public partial class LifeManagementWindow : Window
{
    readonly LifeDataService data;
    readonly ReminderService reminders;
    readonly HashSet<string> selected = [];
    bool awaitingConfirmation;

    public LifeManagementWindow(LifeDataService data, ReminderService reminders)
    {
        InitializeComponent();
        this.data = data;
        this.reminders = reminders;
        Loaded += (_, _) => RefreshItems();
    }

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

            var remove = new Button { Content = "\u00D7", Width = 32, Height = 32, FontSize = 18, FontWeight = FontWeights.SemiBold, Style = (Style)FindResource("Action"), Background = new SolidColorBrush(Color.FromRgb(74, 37, 40)), Foreground = new SolidColorBrush(Color.FromRgb(255, 120, 120)), Tag = agenda, ToolTip = agenda.Kind == "recurring" ? "\u5220\u9664\u6574\u4E2A\u5468\u671F\u8BA1\u5212" : "\u5220\u9664" };
            remove.Click += (sender, _) => DeleteOne((AgendaItem)((FrameworkElement)sender).Tag);
            Grid.SetColumn(remove, 2);
            panel.Children.Add(remove);

            row.Child = panel;
            Items.Children.Add(row);
        }
        UpdateSelectionUi();
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
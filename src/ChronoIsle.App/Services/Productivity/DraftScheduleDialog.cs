using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;

namespace ChronoIsle.App.Services.Productivity;

public sealed record DraftScheduleSelection(IReadOnlySet<string> ItemIds,
    IReadOnlyDictionary<string, DateTimeOffset?> ScheduleOverrides);

/// <summary>Small confirmation dialog for choosing which decomposition tasks become todos or dated calendar work.</summary>
public static class DraftScheduleDialog
{
    public static DraftScheduleSelection? Show(CommandDraftView draft)
    {
        var rows = new List<Row>();
        var list = new StackPanel { Margin = new Thickness(16) };
        list.Children.Add(new TextBlock
        {
            Text = "选择要创建的子任务；时间留空则仅加入待办，不安排到日历。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        });
        foreach (var item in draft.Items)
        {
            var check = new CheckBox { Content = item.Proposal.Title, IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
            var time = new TextBox { Width = 150, Margin = new Thickness(9, 0, 0, 0), ToolTip = "例如 2026-07-22 09:00" };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            row.Children.Add(check);
            row.Children.Add(time);
            list.Children.Add(row);
            rows.Add(new(item.Id, check, time));
        }

        DraftScheduleSelection? selection = null;
        var dialog = new Window
        {
            Title = "安排拆解任务",
            Width = 510,
            MinHeight = 260,
            MaxHeight = 620,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive),
            Content = new ScrollViewer { Content = list }
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "返回", MinWidth = 75, Margin = new Thickness(0, 0, 8, 0) };
        var confirm = new Button { Content = "确认创建", MinWidth = 90 };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        list.Children.Add(buttons);
        cancel.Click += (_, _) => dialog.DialogResult = false;
        confirm.Click += (_, _) =>
        {
            var itemIds = new HashSet<string>(StringComparer.Ordinal);
            var schedules = new Dictionary<string, DateTimeOffset?>();
            foreach (var row in rows.Where(row => row.IsSelected))
            {
                itemIds.Add(row.Id);
                if (string.IsNullOrWhiteSpace(row.Schedule.Text)) continue;
                if (!TryParseLocal(row.Schedule.Text, out var due))
                {
                    MessageBox.Show(dialog, "时间请使用 yyyy-MM-dd HH:mm，例如 2026-07-22 09:00。", "时间格式", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                schedules[row.Id] = due;
            }
            if (itemIds.Count == 0)
            {
                MessageBox.Show(dialog, "请至少选择一项任务。", "未选择任务", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            selection = new(itemIds, schedules);
            dialog.DialogResult = true;
        };
        return dialog.ShowDialog() == true ? selection : null;
    }

    internal static bool TryParseLocal(string text, out DateTimeOffset value)
    {
        if (!DateTime.TryParseExact(text.Trim(), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var local))
        {
            value = default;
            return false;
        }
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        value = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        return true;
    }

    sealed record Row(string Id, CheckBox Check, TextBox Schedule)
    {
        public bool IsSelected => Check.IsChecked == true;
    }
}

using System.Globalization;
using ChronoIsle.App.Views;
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
        var list = new StackPanel();
        list.Children.Add(new TextBlock
        {
            Text = "选择要创建的子任务；时间留空则仅加入待办，不安排到日历。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        });
        foreach (var item in draft.Items)
        {
            var check = new CheckBox { Content = new TextBlock { Text = item.Proposal.Title, TextWrapping = TextWrapping.Wrap }, IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
            var time = new TextBox { Margin = new Thickness(28, 6, 0, 0), ToolTip = "例如 2026-07-22 09:00" };
            System.Windows.Automation.AutomationProperties.SetName(time, "安排时间：" + item.Proposal.Title);
            var row = new StackPanel { Margin = new Thickness(0, 6, 0, 12) };
            row.Children.Add(check);
            row.Children.Add(time);
            list.Children.Add(row);
            rows.Add(new(item.Id, check, time));
        }

        DraftScheduleSelection? selection = null;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "返回", MinWidth = 88, IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        cancel.SetResourceReference(FrameworkElement.StyleProperty, "Button.Secondary");
        var confirm = new Button { Content = "确认创建", MinWidth = 100 };
        confirm.SetResourceReference(FrameworkElement.StyleProperty, "Button.Primary");
        var dialog = DialogLayout.Create("安排拆解任务", Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive), list, buttons, 540, 520);
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
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

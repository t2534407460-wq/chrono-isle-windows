using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ChronoIsle.App.Services.Reporting;

namespace ChronoIsle.App.Views;

public partial class LifeIslandWindow
{
    Border? weeklyReportPanel;
    TextBlock? monthlyReportText;
    TextBlock? weeklyReportText;
    TextBlock? nextWeekPlanText;
    DispatcherTimer? weeklyReportTimer;

    /// <summary>Called by the application bootstrap once the island window exists.</summary>
    public void EnableWeeklyReports()
    {
        if (weeklyReportPanel is not null) return;

        weeklyReportText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(188, 233, 192)),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        };
        monthlyReportText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(188, 205, 233)),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 0)
        };
        nextWeekPlanText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(152, 196, 255)),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 0),
            Visibility = Visibility.Collapsed
        };
        var plan = new Button
        {
            Content = "生成下周计划",
            Style = (Style)FindResource("IslandType"),
            Padding = new Thickness(7, 2, 7, 2),
            FontSize = 10,
            Margin = new Thickness(8, 0, 0, 0)
        };
        plan.Click += (_, _) =>
        {
            nextWeekPlanVisible = true;
            RenderWeeklyReports();
        };
        var heading = new DockPanel();
        DockPanel.SetDock(plan, Dock.Right);
        heading.Children.Add(plan);
        heading.Children.Add(new TextBlock
        {
            Text = "本周复盘",
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12
        });
        var content = new StackPanel();
        content.Children.Add(heading);
        content.Children.Add(weeklyReportText);
        content.Children.Add(nextWeekPlanText);
        content.Children.Add(monthlyReportText);
        weeklyReportPanel = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(25, 30, 39)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(61, 79, 113)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(10),
            Margin = new Thickness(12, 0, 12, 8),
            Visibility = Visibility.Collapsed,
            Child = content
        };
        ExpandedContent.Children.Insert(Math.Min(3, ExpandedContent.Children.Count), weeklyReportPanel);
        weeklyReportTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        weeklyReportTimer.Tick += (_, _) => RenderWeeklyReports();
        weeklyReportTimer.Start();
        RenderWeeklyReports();
    }

    void RenderWeeklyReports()
    {
        if (weeklyReportText is null || monthlyReportText is null || nextWeekPlanText is null) return;
        var now = DateTimeOffset.Now;
        var today = DateOnly.FromDateTime(now.Date);
        var scheduler = weeklyReports ?? new WeeklyReportScheduler(reports);
        if (now.DayOfWeek == DayOfWeek.Sunday && automaticWeeklyReportDate != today)
        {
            scheduler.GenerateIfDue(now);
            automaticWeeklyReportDate = today;
        }

        var localStart = now.Date.AddDays(-((int)now.DayOfWeek + 6) % 7);
        var offset = TimeZoneInfo.Local.GetUtcOffset(localStart);
        var current = reports.Generate(new ReportPeriod(
            ReportPeriodKind.Weekly,
            new DateTimeOffset(localStart, offset),
            new DateTimeOffset(localStart.AddDays(7), offset)), "facts-v1");
        var history = reports.ListSnapshots(ReportPeriodKind.Weekly, 3);
        weeklyReportText.Text = $"完成 {current.Facts.CompletedCount} · 逾期 {current.Facts.OverdueCount} · 高优先级 {current.Facts.HighPriorityCount}" +
            (history.Count > 1 ? $"\n历史周报 {history.Count - 1} 份可查看" : "");

        var monthStart = new DateTime(now.Year, now.Month, 1);
        var monthOffset = TimeZoneInfo.Local.GetUtcOffset(monthStart);
        var monthly = reports.Generate(new ReportPeriod(
            ReportPeriodKind.Monthly,
            new DateTimeOffset(monthStart, monthOffset),
            new DateTimeOffset(monthStart.AddMonths(1), monthOffset)), "facts-v1");
        monthlyReportText.Text = $"本月复盘：完成 {monthly.Facts.CompletedCount} · 逾期 {monthly.Facts.OverdueCount} · 高优先级 {monthly.Facts.HighPriorityCount}";

        nextWeekPlanText.Visibility = nextWeekPlanVisible ? Visibility.Visible : Visibility.Collapsed;
        if (nextWeekPlanVisible)
            nextWeekPlanText.Text = string.Join("\n", WeeklyReportScheduler.BuildNextWeekPlan(current.Facts).Select((line, index) => $"{index + 1}. {line}"));
    }
}

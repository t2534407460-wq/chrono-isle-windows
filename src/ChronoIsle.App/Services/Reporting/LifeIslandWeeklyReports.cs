using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ChronoIsle.App.Services.Reporting;

namespace ChronoIsle.App.Views;

public partial class LifeIslandWindow
{
    Border? weeklyReportPanel;
    DockPanel? weeklyReportActions;
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
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 19
        };
        SetThemeResource(weeklyReportText, TextBlock.ForegroundProperty, "Brush.Success");
        monthlyReportText = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 19,
            Margin = new Thickness(12, 0, 0, 0)
        };
        SetThemeResource(monthlyReportText, TextBlock.ForegroundProperty, "Brush.TextSecondary");
        nextWeekPlanText = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 0),
            Visibility = Visibility.Collapsed
        };
        SetThemeResource(nextWeekPlanText, TextBlock.ForegroundProperty, "Brush.Accent");
        var plan = new Button
        {
            Content = "生成下周计划",
            Style = (Style)FindResource("IslandType"),
            Padding = new Thickness(7, 2, 7, 2),
            FontSize = 12,
            Margin = new Thickness(8, 0, 0, 0)
        };
        plan.Click += (_, _) =>
        {
            nextWeekPlanVisible = true;
            RenderWeeklyReports();
            ResizeExpandedToContent();
        };
        var content = new StackPanel();
        var periods = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
        periods.Children.Add(weeklyReportText);
        periods.Children.Add(monthlyReportText);
        content.Children.Add(periods);
        weeklyReportActions = new DockPanel { Margin = new Thickness(0, 5, 0, 0) };
        DockPanel.SetDock(plan, Dock.Right); weeklyReportActions.Children.Add(plan);
        content.Children.Add(weeklyReportActions);
        content.Children.Add(nextWeekPlanText);
        weeklyReportPanel = DashboardCard("reports", "报表", content,
            () => ReportsRequested?.Invoke(this, EventArgs.Empty));
        weeklyReportPanel.Margin = new Thickness(16, 0, 16, 10);
        weeklyReportPanel.Visibility = todayPanel?.Visibility ?? Visibility.Collapsed;
        SetThemeResource(weeklyReportPanel, Border.BackgroundProperty, "Brush.Card");
        SetThemeResource(weeklyReportPanel, Border.BorderBrushProperty, "Brush.StrokeSoft");
        ExpandedContent.Children.Insert(todayPanel is null ? Math.Min(3, ExpandedContent.Children.Count) : ExpandedContent.Children.IndexOf(todayPanel) + 1, weeklyReportPanel);
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
            new DateTimeOffset(localStart.AddDays(7), offset)), "facts-v2");
        weeklyReportText.Text = $"本周 · 完成 {current.Facts.CompletedCount}\n逾期 {current.Facts.OverdueCount} · 高优先级 {current.Facts.HighPriorityCount}";

        var monthStart = new DateTime(now.Year, now.Month, 1);
        var monthOffset = TimeZoneInfo.Local.GetUtcOffset(monthStart);
        var monthly = reports.Generate(new ReportPeriod(
            ReportPeriodKind.Monthly,
            new DateTimeOffset(monthStart, monthOffset),
            new DateTimeOffset(monthStart.AddMonths(1), monthOffset)), "facts-v2");
        monthlyReportText.Text = $"本月 · 完成 {monthly.Facts.CompletedCount}\n逾期 {monthly.Facts.OverdueCount} · 高优先级 {monthly.Facts.HighPriorityCount}";

        nextWeekPlanText.Visibility = nextWeekPlanVisible ? Visibility.Visible : Visibility.Collapsed;
        if (nextWeekPlanVisible)
            nextWeekPlanText.Text = string.Join("\n", WeeklyReportScheduler.BuildNextWeekPlan(current.Facts).Select((line, index) => $"{index + 1}. {line}"));
    }
}

using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ChronoIsle.App.Views;
using ChronoIsle.App.Services.Reporting;

namespace ChronoIsle.App.Views;

public partial class LifeIslandWindow
{
    TextBlock? weeklyHistoryText;
    DispatcherTimer? weeklyHistoryTimer;

    /// <summary>Adds the persisted weekly snapshot entry to the shared report card.</summary>
    public void EnableWeeklyReportHistory()
    {
        if (weeklyHistoryText is not null) return;
        EnableWeeklyReports();

        weeklyHistoryText = new TextBlock
        {
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        SetThemeResource(weeklyHistoryText, TextBlock.ForegroundProperty, "Brush.TextSecondary");

        var history = new Button { Content = weeklyHistoryText, Style = (Style)FindResource("TodayCardButton") };
        System.Windows.Automation.AutomationProperties.SetAutomationId(history, "TodayCardNavigate-weekly-history");
        history.Click += (_, _) => ReportsRequested?.Invoke(this, EventArgs.Empty);
        weeklyReportActions!.Children.Add(history);
        weeklyHistoryTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        weeklyHistoryTimer.Tick += (_, _) => RenderWeeklyReportHistory();
        weeklyHistoryTimer.Start();
        RenderWeeklyReportHistory();
    }

    void RenderWeeklyReportHistory()
    {
        if (weeklyHistoryText is null) return;
        var snapshots = reports.ListSnapshots(ReportPeriodKind.Weekly, 4);
        weeklyHistoryText.Text = snapshots.Count == 0 ? "历史周报 · 暂无记录  ›" : $"历史周报 · 最近 {snapshots.Count} 期  ›";
        weeklyHistoryText.ToolTip = snapshots.Count == 0 ? "周日会自动生成第一份周报" : string.Join("\n", snapshots.Select(snapshot =>
            $"{snapshot.Period.StartUtc.ToLocalTime():MM/dd}–{snapshot.Period.EndUtc.ToLocalTime().AddDays(-1):MM/dd} · 完成 {snapshot.Facts.CompletedCount} · 逾期 {snapshot.Facts.OverdueCount}"));
    }
}

internal static class LifeIslandWeeklyReportHistoryBootstrap
{
    static Timer? retryTimer;

    [ModuleInitializer]
    internal static void Initialize()
    {
        retryTimer = new Timer(_ => TryAttach(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1));
    }

    static void TryAttach()
    {
        var application = Application.Current;
        if (application?.Dispatcher.HasShutdownStarted != false) return;
        application.Dispatcher.BeginInvoke(() =>
        {
            var island = application.Windows.OfType<LifeIslandWindow>().FirstOrDefault();
            if (island is null) return;
            island.EnableWeeklyReportHistory();
            retryTimer?.Dispose();
            retryTimer = null;
        });
    }
}

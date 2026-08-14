using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ChronoIsle.App.Views;
using ChronoIsle.App.Services.Reporting;

namespace ChronoIsle.App.Views;

public partial class LifeIslandWindow
{
    Border? weeklyHistoryPanel;
    TextBlock? weeklyHistoryText;
    DispatcherTimer? weeklyHistoryTimer;

    /// <summary>Renders the persisted weekly snapshots separately from the current-week card.</summary>
    public void EnableWeeklyReportHistory()
    {
        if (weeklyHistoryPanel is not null) return;
        EnableWeeklyReports();

        weeklyHistoryText = new TextBlock
        {
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap
        };
        SetThemeResource(weeklyHistoryText, TextBlock.ForegroundProperty, "Brush.TextSecondary");

        var historyHeading = new TextBlock
        {
            Text = "历史周报",
            FontWeight = FontWeights.SemiBold,
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 4)
        };
        SetThemeResource(historyHeading, TextBlock.ForegroundProperty, "Brush.TextPrimary");

        weeklyHistoryPanel = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(9),
            Margin = new Thickness(12, 0, 12, 8),
            Visibility = Visibility.Collapsed,
            Child = new StackPanel
            {
                Children =
                {
                    historyHeading,
                    weeklyHistoryText
                }
            }
        };
        SetThemeResource(weeklyHistoryPanel, Border.BackgroundProperty, "Brush.Card");
        SetThemeResource(weeklyHistoryPanel, Border.BorderBrushProperty, "Brush.Stroke");
        ExpandedContent.Children.Insert(Math.Min(4, ExpandedContent.Children.Count), weeklyHistoryPanel);
        weeklyHistoryTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        weeklyHistoryTimer.Tick += (_, _) => RenderWeeklyReportHistory();
        weeklyHistoryTimer.Start();
        RenderWeeklyReportHistory();
    }

    void RenderWeeklyReportHistory()
    {
        if (weeklyHistoryText is null) return;
        var snapshots = reports.ListSnapshots(ReportPeriodKind.Weekly, 4);
        weeklyHistoryText.Text = snapshots.Count == 0
            ? "周日会自动生成第一份周报"
            : string.Join("\n", snapshots.Select(snapshot =>
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

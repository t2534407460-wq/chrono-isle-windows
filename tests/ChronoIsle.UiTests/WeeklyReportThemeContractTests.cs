using System.IO;

namespace ChronoIsle.UiTests;

public sealed class WeeklyReportThemeContractTests
{
    [Fact]
    public void Weekly_report_cards_use_dynamic_theme_resources()
    {
        var root = FindRepositoryRoot();
        var weeklyReport = File.ReadAllText(Path.Combine(
            root, "src", "ChronoIsle.App", "Services", "Reporting", "LifeIslandWeeklyReports.cs"));
        var weeklyHistory = File.ReadAllText(Path.Combine(
            root, "src", "ChronoIsle.App", "Services", "Reporting", "LifeIslandWeeklyReportHistory.cs"));

        Assert.Contains(
            "SetThemeResource(weeklyReportPanel, Border.BackgroundProperty, \"Brush.Card\");",
            weeklyReport,
            StringComparison.Ordinal);
        Assert.Contains(
            "SetThemeResource(weeklyReportPanel, Border.BorderBrushProperty, \"Brush.Stroke\");",
            weeklyReport,
            StringComparison.Ordinal);
        Assert.Contains(
            "SetThemeResource(weeklyReportText, TextBlock.ForegroundProperty, \"Brush.Success\");",
            weeklyReport,
            StringComparison.Ordinal);
        Assert.Contains(
            "SetThemeResource(monthlyReportText, TextBlock.ForegroundProperty, \"Brush.TextSecondary\");",
            weeklyReport,
            StringComparison.Ordinal);
        Assert.Contains(
            "SetThemeResource(nextWeekPlanText, TextBlock.ForegroundProperty, \"Brush.Accent\");",
            weeklyReport,
            StringComparison.Ordinal);
        Assert.Contains(
            "SetThemeResource(headingText, TextBlock.ForegroundProperty, \"Brush.TextPrimary\");",
            weeklyReport,
            StringComparison.Ordinal);
        Assert.Contains(
            "SetThemeResource(weeklyHistoryPanel, Border.BackgroundProperty, \"Brush.Card\");",
            weeklyHistory,
            StringComparison.Ordinal);
        Assert.Contains(
            "SetThemeResource(weeklyHistoryPanel, Border.BorderBrushProperty, \"Brush.Stroke\");",
            weeklyHistory,
            StringComparison.Ordinal);
        Assert.Contains(
            "SetThemeResource(weeklyHistoryText, TextBlock.ForegroundProperty, \"Brush.TextSecondary\");",
            weeklyHistory,
            StringComparison.Ordinal);
        Assert.Contains(
            "SetThemeResource(historyHeading, TextBlock.ForegroundProperty, \"Brush.TextPrimary\");",
            weeklyHistory,
            StringComparison.Ordinal);

        Assert.DoesNotContain("new SolidColorBrush", weeklyReport, StringComparison.Ordinal);
        Assert.DoesNotContain("Brushes.White", weeklyReport, StringComparison.Ordinal);
        Assert.DoesNotContain("new SolidColorBrush", weeklyHistory, StringComparison.Ordinal);
        Assert.DoesNotContain("Brushes.White", weeklyHistory, StringComparison.Ordinal);
    }

    static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ChronoIsle.sln")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}

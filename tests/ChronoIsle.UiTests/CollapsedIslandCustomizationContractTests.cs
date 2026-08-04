using System.IO;

namespace ChronoIsle.UiTests;

public sealed class CollapsedIslandCustomizationContractTests
{
    [Fact]
    public void CollapsedIsland_ExposesIndependentlyHideableHeaderElements()
    {
        var (xaml, source) = IslandFiles();

        Assert.Contains("x:Name=\"MascotArea\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ExpandIndicator\"", xaml, StringComparison.Ordinal);
        Assert.Contains("void ApplyCollapsedPreferences(LifePreferences currentPreferences)", source, StringComparison.Ordinal);

        foreach (var preference in new[]
                 {
                     "IslandShowMascot", "IslandShowStatusLight", "IslandShowAgendaSummary",
                     "IslandShowNetworkSpeed", "IslandShowCpuUsage", "IslandShowMemoryUsage",
                     "IslandShowNetworkStatus", "IslandShowClock",
                     "IslandShowExpandIndicator"
                 })
            Assert.Contains(preference, source, StringComparison.Ordinal);
    }

    [Fact]
    public void CollapsedIsland_ShowsEnabledSummaryWidgetsInParallelAndUsesContentWidth()
    {
        var (xaml, source) = IslandFiles();

        Assert.Contains("x:Name=\"SummaryWidgets\" Orientation=\"Horizontal\"", xaml, StringComparison.Ordinal);
        foreach (var widget in new[] { "Summary", "NetworkSpeedSummary", "CpuUsageSummary", "MemoryUsageSummary" })
            Assert.Contains($"x:Name=\"{widget}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"NetworkSpeedSummary\" Width=\"162\"", xaml, StringComparison.Ordinal);
        Assert.Contains("void SetIdleSummaryWidgetVisibility(LifePreferences currentPreferences)", source, StringComparison.Ordinal);
        Assert.Contains("NetworkSpeedSummary.Visibility = VisibilityFor", source, StringComparison.Ordinal);
        Assert.Contains("CpuUsageSummary.Visibility = VisibilityFor", source, StringComparison.Ordinal);
        Assert.Contains("MemoryUsageSummary.Visibility = VisibilityFor", source, StringComparison.Ordinal);
        Assert.Contains("return Math.Max(CollapsedIslandDisplayPolicy.MinimumWidth, compactWidth);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CollapsedIslandDisplayPolicy.SelectSummary(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RotatingSummaryWidth", source, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateCollapsedSummaryWidth", source, StringComparison.Ordinal);
        Assert.Contains("ApplyCollapsedPreferences(currentPreferences);", source, StringComparison.Ordinal);
        Assert.Contains("Refresh();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateCollapsedMediaView(snapshot, preferences.Load())", source, StringComparison.Ordinal);
    }

    static (string Xaml, string Source) IslandFiles()
    {
        var workspace = FindWorkspace();
        return (
            File.ReadAllText(Path.Combine(
                workspace, "src", "ChronoIsle.App", "Views", "LifeIslandWindow.xaml")),
            File.ReadAllText(Path.Combine(
                workspace, "src", "ChronoIsle.App", "Views", "LifeIslandWindow.xaml.cs")));
    }

    static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ChronoIsle.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("ChronoIsle.sln was not found from the UI test host.");
    }
}

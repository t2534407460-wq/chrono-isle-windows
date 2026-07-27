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
    public void CollapsedIsland_UsesFiveSecondSummaryRotationAndContentWidthBounds()
    {
        var (_, source) = IslandFiles();

        Assert.Contains("CollapsedIslandDisplayPolicy.SelectSummary(", source, StringComparison.Ordinal);
        Assert.Contains("CollapsedSummaryKind.CpuUsage", source, StringComparison.Ordinal);
        Assert.Contains("CollapsedSummaryKind.MemoryUsage", source, StringComparison.Ordinal);
        Assert.Contains("telemetry.Current.CpuPercent", source, StringComparison.Ordinal);
        Assert.Contains("telemetry.Current.MemoryPercent", source, StringComparison.Ordinal);
        Assert.Contains("DateTimeOffset.UtcNow.ToUnixTimeSeconds()", source, StringComparison.Ordinal);
        Assert.Contains("CollapsedIslandDisplayPolicy.SelectWidth(", source, StringComparison.Ordinal);
        Assert.Contains("const double RotatingSummaryWidth = 112;", source, StringComparison.Ordinal);
        Assert.Contains(
            "Summary.Width = rotatingSummary && !pointerHover ? RotatingSummaryWidth : double.NaN;",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "MainBorder.BorderThickness.Left + MainBorder.BorderThickness.Right",
            source,
            StringComparison.Ordinal);
        Assert.Contains("ApplyCollapsedPreferences(currentPreferences);", source, StringComparison.Ordinal);
        Assert.Contains("Refresh();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateCollapsedMediaView(snapshot, preferences.Load())", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CollapsedIsland_HoverTemporarilyShowsTheFullSummary()
    {
        var (_, source) = IslandFiles();
        var mouseEnter = Handler(source, "void Island_MouseEnter", "void Island_MouseLeave");
        var mouseLeave = Handler(source, "void Island_MouseLeave", "void ConfirmTopDockHoverExit");
        var confirmedTopExit = Handler(source, "void ConfirmTopDockHoverExit", "void SetTopDockFolded");

        Assert.Contains(
            "void UpdateCollapsedSummaryWidth(LifePreferences currentPreferences)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("pointerHover = true;", mouseEnter, StringComparison.Ordinal);
        Assert.Contains("UpdateCollapsedSummaryWidth(collapsedPreferences);", mouseEnter, StringComparison.Ordinal);
        Assert.Contains("pointerHover = false;", mouseLeave, StringComparison.Ordinal);
        Assert.Contains("UpdateCollapsedSummaryWidth(collapsedPreferences);", mouseLeave, StringComparison.Ordinal);
        Assert.Contains("pointerHover = false;", confirmedTopExit, StringComparison.Ordinal);
        Assert.Contains("UpdateCollapsedSummaryWidth(collapsedPreferences);", confirmedTopExit, StringComparison.Ordinal);
        Assert.Contains("double FullCollapsedContentWidth()", source, StringComparison.Ordinal);
        Assert.Contains(
            "Summary.Measure(new System.Windows.Size(double.PositiveInfinity, Header.Height));",
            source,
            StringComparison.Ordinal);
        Assert.Contains("CollapsedIslandDisplayPolicy.SelectWidth(", source, StringComparison.Ordinal);
    }

    static string Handler(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        return source[start..end];
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

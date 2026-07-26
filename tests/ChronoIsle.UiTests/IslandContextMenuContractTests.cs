using System.IO;

namespace ChronoIsle.UiTests;

public sealed class IslandContextMenuContractTests
{
    [Fact]
    public void ContextMenu_NearTaskbarUsesAbovePlacementAndIslandVisuals()
    {
        var workspace = FindWorkspace();
        var code = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "Views",
            "LifeIslandWindow.xaml.cs"));
        var xaml = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "Views",
            "LifeIslandWindow.xaml"));

        Assert.Contains("ShouldPlaceQuickActionMenuAboveTaskbar()", code, StringComparison.Ordinal);
        Assert.Contains("PlacementMode.Custom", code, StringComparison.Ordinal);
        Assert.Contains("new System.Windows.Point(left, -popupSize.Height - 6)", code, StringComparison.Ordinal);
        Assert.Contains("ContextMenuTaskbarProximity", code, StringComparison.Ordinal);
        Assert.Contains("StartsQuickActionGroup(action)", code, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IslandContextMenu\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IslandContextMenuItem\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"{DynamicResource Brush.Island}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("BorderBrush=\"{DynamicResource Brush.Stroke}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Value=\"{DynamicResource Brush.Control}\"", xaml, StringComparison.Ordinal);
    }

    static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ChronoIsle.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("ChronoIsle.sln was not found from the UI test host.");
    }
}

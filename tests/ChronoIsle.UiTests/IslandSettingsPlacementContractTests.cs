using System.IO;

namespace ChronoIsle.UiTests;

public sealed class IslandSettingsPlacementContractTests
{
    [Fact]
    public void SettingsButton_IsRightAlignedInExpandedDashboardToolbar()
    {
        var workspace = FindWorkspace();
        var xaml = File.ReadAllText(Path.Combine(
            workspace, "src", "ChronoIsle.App", "Views", "LifeIslandWindow.xaml"));
        var source = File.ReadAllText(Path.Combine(
            workspace, "src", "ChronoIsle.App", "Views", "LifeIslandWindow.xaml.cs"));

        Assert.Contains("<Grid x:Name=\"DashboardTabs\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"IslandSettingsButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Grid.Column=\"1\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"⚙ 设置\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"Settings_Click\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "IslandQuickAction.Naming, IslandQuickAction.Settings",
            source,
            StringComparison.Ordinal);
    }

    static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ChronoIsle.sln")))
                return directory.FullName;
        throw new DirectoryNotFoundException("ChronoIsle.sln was not found from the UI test host.");
    }
}

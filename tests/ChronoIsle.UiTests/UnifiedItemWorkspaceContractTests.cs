using System.IO;

namespace ChronoIsle.UiTests;

public sealed class UnifiedItemWorkspaceContractTests
{
    [Fact]
    public void Item_workspace_keeps_recommendations_as_a_contextual_task_view()
    {
        var root = FindRepositoryRoot();
        var workspaceXaml = File.ReadAllText(Path.Combine(root, "src", "ChronoIsle.App", "Views", "LifeManagementWindow.xaml"));
        var workspaceCode = File.ReadAllText(Path.Combine(root, "src", "ChronoIsle.App", "Views", "LifeManagementWindow.xaml.cs"));
        var islandCode = File.ReadAllText(Path.Combine(root, "src", "ChronoIsle.App", "Views", "LifeIslandWindow.xaml.cs"));

        Assert.Contains("x:Name=\"NowTab\"", workspaceXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"NowTab_Click\"", workspaceXaml, StringComparison.Ordinal);
        Assert.Contains("taskAttributes.Recommend", workspaceCode, StringComparison.Ordinal);
        Assert.Contains("IslandQuickAction.ManageItems", islandCode, StringComparison.Ordinal);
        Assert.DoesNotContain("可执行推荐", islandCode, StringComparison.Ordinal);
        Assert.Contains("WindowStyle = WindowStyle.None", islandCode, StringComparison.Ordinal);
        Assert.Contains("AllowsTransparency = true", islandCode, StringComparison.Ordinal);
        Assert.Contains("Brush.Window", islandCode, StringComparison.Ordinal);
    }

    static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "ChronoIsle.App"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}

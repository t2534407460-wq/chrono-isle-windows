using System.IO;

namespace OpenIsland.UiTests;

public sealed class TaskbarExpansionAnimationContractTests
{
    [Fact]
    public void TaskbarExpansion_AnchorsHeaderOnEveryLayoutFrame()
    {
        var workspace = FindWorkspace();
        var source = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "OpenIsland.App",
            "Views",
            "LifeIslandWindow.xaml.cs"));

        Assert.Contains("IslandLayout.LayoutUpdated += (_, _) => MaintainTaskbarHeaderAnchor();", source, StringComparison.Ordinal);
        Assert.Contains("taskbarHeightAnimationActive", source, StringComparison.Ordinal);
        Assert.Contains("MaintainTaskbarHeaderAnchor();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ExpandedScrollViewer.MaxHeight = double.PositiveInfinity", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AnimateWindowProperty(TopProperty", source, StringComparison.Ordinal);
    }

    static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "OpenIsland.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("OpenIsland.sln was not found from the UI test host.");
    }
}

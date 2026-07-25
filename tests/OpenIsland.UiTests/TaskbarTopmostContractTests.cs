using System.IO;

namespace OpenIsland.UiTests;

public sealed class TaskbarTopmostContractTests
{
    [Fact]
    public void TaskbarPlacement_ReassertsHighestTopmostWithoutTakingFocus()
    {
        var workspace = FindWorkspace();
        var source = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "OpenIsland.App",
            "Views",
            "LifeIslandWindow.xaml.cs"));

        Assert.Contains("static extern bool SetWindowPos(", source, StringComparison.Ordinal);
        Assert.Contains("void EnsureTaskbarTopmost()", source, StringComparison.Ordinal);
        Assert.Contains("placement != IslandPlacement.Taskbar", source, StringComparison.Ordinal);
        Assert.Contains("HwndTopmost", source, StringComparison.Ordinal);
        Assert.Contains("SwpNoActivate", source, StringComparison.Ordinal);
        Assert.Contains("taskbarTopmostTimer.Tick += (_, _) => EnsureTaskbarTopmost();", source, StringComparison.Ordinal);
        Assert.Contains("taskbarTopmostTimer.Stop();", source, StringComparison.Ordinal);
    }

    static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "OpenIsland.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("OpenIsland.sln was not found from the UI test host.");
    }
}

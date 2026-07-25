using System.IO;

namespace ChronoIsle.UiTests;

public sealed class ExternalFocusCollapseContractTests
{
    [Fact]
    public void ExpandedIsland_CollapsesWhenForegroundMovesToAnotherProcess()
    {
        var source = ReadIslandSource();

        Assert.Contains("Deactivated += (_, _) => Dispatcher.BeginInvoke(", source, StringComparison.Ordinal);
        Assert.Contains("new Action(CollapseWhenForegroundMovesToAnotherProcess)", source, StringComparison.Ordinal);
        Assert.Contains("var foregroundWindow = GetForegroundWindow();", source, StringComparison.Ordinal);
        Assert.Contains("GetWindowThreadProcessId(foregroundWindow, out var foregroundProcessId);", source, StringComparison.Ordinal);
        Assert.Contains("foregroundProcessId == (uint)Environment.ProcessId", source, StringComparison.Ordinal);
        Assert.Contains("headerSingleClickTimer.Stop();", source, StringComparison.Ordinal);
        Assert.Contains("if (expanded) Collapse();", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingFiveSecondMouseLeaveCollapse_RemainsEnabled()
    {
        var source = ReadIslandSource();

        Assert.Contains("Interval = TimeSpan.FromSeconds(5)", source, StringComparison.Ordinal);
        Assert.Contains("void Island_MouseLeave(", source, StringComparison.Ordinal);
        Assert.Contains("collapseTimer.Start();", source, StringComparison.Ordinal);
    }

    static string ReadIslandSource()
    {
        var workspace = FindWorkspace();
        return File.ReadAllText(Path.Combine(workspace, "src", "ChronoIsle.App", "Views", "LifeIslandWindow.xaml.cs"));
    }

    static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ChronoIsle.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("ChronoIsle.sln was not found from the UI test host.");
    }
}

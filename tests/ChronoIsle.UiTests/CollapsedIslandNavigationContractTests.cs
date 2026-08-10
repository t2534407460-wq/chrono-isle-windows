using System.IO;

namespace ChronoIsle.UiTests;

public sealed class CollapsedIslandNavigationContractTests
{
    [Theory]
    [InlineData("IslandNetworkStatusButton")]
    [InlineData("IslandClockButton")]
    [InlineData("IslandAgendaButton")]
    [InlineData("IslandExpandButton")]
    public void CollapsedControl_UsesHeaderGesturePipeline(string automationId)
    {
        var workspace = FindWorkspace();
        var xaml = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "Views",
            "LifeIslandWindow.xaml"));

        Assert.Contains($"AutomationProperties.AutomationId=\"{automationId}\"", xaml, StringComparison.Ordinal);
        var elementStart = xaml.IndexOf($"AutomationProperties.AutomationId=\"{automationId}\"", StringComparison.Ordinal);
        var elementEnd = xaml.IndexOf('>', elementStart);
        Assert.True(elementStart >= 0 && elementEnd > elementStart);
        Assert.DoesNotContain("Click=", xaml[elementStart..elementEnd], StringComparison.Ordinal);
        Assert.Contains("PreviewMouseLeftButtonDown=\"Header_MouseDown\"", xaml, StringComparison.Ordinal);
        Assert.Contains("PreviewMouseLeftButtonUp=\"Header_MouseUp\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void CollapsedControl_SingleClickRestoresTheLastVisibleDashboard()
    {
        var workspace = FindWorkspace();
        var source = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "Views",
            "LifeIslandWindow.xaml.cs"));

        Assert.Contains("ScheduleHeaderSingleClick();", source, StringComparison.Ordinal);
        Assert.Contains("headerSingleClickTimer.Tick += (_, _) =>", source, StringComparison.Ordinal);
        Assert.Contains("ToggleExpanded();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CollapsedHeaderTarget", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ToggleCollapsedHeaderTarget", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CollapsedControl_DoesNotKeepLocalDashboardHitTargets()
    {
        var workspace = FindWorkspace();
        var xaml = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "Views",
            "LifeIslandWindow.xaml"));

        foreach (var name in new[] { "MascotButton", "AgendaSummaryButton", "NetworkStatusLight", "ClockButton" })
        {
            var start = xaml.IndexOf($"x:Name=\"{name}\"", StringComparison.Ordinal);
            var end = xaml.IndexOf('>', start);
            Assert.True(start >= 0 && end > start, $"The collapsed header control {name} is missing.");
            Assert.Contains("IsHitTestVisible=\"False\"", xaml[start..end], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MascotStatusLight_DoesNotInterceptMascotClick()
    {
        var workspace = FindWorkspace();
        var xaml = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "Views",
            "LifeIslandWindow.xaml"));

        Assert.Contains(
            "x:Name=\"StatusLight\" Width=\"8\" Height=\"8\" IsHitTestVisible=\"False\"",
            xaml,
            StringComparison.Ordinal);
    }

    static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ChronoIsle.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("ChronoIsle.sln was not found from the UI test host.");
    }
}

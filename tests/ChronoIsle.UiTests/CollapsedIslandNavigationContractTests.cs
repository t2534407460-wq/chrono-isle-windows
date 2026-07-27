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
    public void CollapsedControl_SingleClickRoutesToExpectedDashboard()
    {
        var workspace = FindWorkspace();
        var source = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "Views",
            "LifeIslandWindow.xaml.cs"));

        Assert.Contains("CollapsedHeaderTargetFor(e.OriginalSource as DependencyObject)", source, StringComparison.Ordinal);
        Assert.Contains("ScheduleHeaderSingleClick(pressedCollapsedHeaderTarget);", source, StringComparison.Ordinal);
        Assert.Contains("ToggleCollapsedHeaderTarget(target);", source, StringComparison.Ordinal);
        Assert.Contains("CollapsedHeaderTarget.QuickAsk => QuickAskPanel.Visibility == Visibility.Visible", source, StringComparison.Ordinal);
        Assert.Contains("CollapsedHeaderTarget.Calendar => CalendarPanel.Visibility == Visibility.Visible", source, StringComparison.Ordinal);
        Assert.Contains("CollapsedHeaderTarget.Telemetry => TelemetryPanel.Visibility == Visibility.Visible", source, StringComparison.Ordinal);
        Assert.Contains("CollapsedHeaderTarget.Today => todayPanel?.Visibility == Visibility.Visible", source, StringComparison.Ordinal);
        Assert.Contains("ShowQuickAskDashboard();", source, StringComparison.Ordinal);
        Assert.Contains("ShowCalendarDashboard();", source, StringComparison.Ordinal);
        Assert.Contains("ShowTelemetryDashboard();", source, StringComparison.Ordinal);
        Assert.Contains("ShowTodayDashboard();", source, StringComparison.Ordinal);
        Assert.Contains("Dispatcher.BeginInvoke(QuickAskInput.Focus);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CollapsedControl_WhenSelectedAndExpanded_CollapsesAgain()
    {
        var workspace = FindWorkspace();
        var source = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "Views",
            "LifeIslandWindow.xaml.cs"));

        Assert.Contains("if (expanded && IsCollapsedHeaderTargetSelected(target))", source, StringComparison.Ordinal);
        Assert.Contains("Collapse();", source, StringComparison.Ordinal);
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

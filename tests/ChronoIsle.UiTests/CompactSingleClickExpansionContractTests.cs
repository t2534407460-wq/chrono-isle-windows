using System.IO;

namespace ChronoIsle.UiTests;

public sealed class CompactSingleClickExpansionContractTests
{
    [Fact]
    public void SingleClickExpansion_ShowsNavigationAndSizesToContent()
    {
        var workspace = FindWorkspace();
        var source = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "Views",
            "LifeIslandWindow.xaml.cs"));

        Assert.Contains(
            "if (target == CollapsedHeaderTarget.None) ToggleExpanded();",
            source,
            StringComparison.Ordinal);
        Assert.Contains("else ToggleCollapsedHeaderTarget(target);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowMediaDashboard", source, StringComparison.Ordinal);
        Assert.Contains("Expand();", source, StringComparison.Ordinal);
        Assert.Contains("void Expand()", source, StringComparison.Ordinal);
        Assert.Contains("DashboardTabs.Visibility = Visibility.Visible;", source, StringComparison.Ordinal);
        Assert.Contains("ExpandedScrollViewer.ScrollToTop();", source, StringComparison.Ordinal);
        Assert.Contains(
            "targetHeight = Math.Min(ExpandedContent.DesiredSize.Height, contentMaxHeight);",
            source,
            StringComparison.Ordinal);
        Assert.Contains("void ResizeExpandedToContent()", source, StringComparison.Ordinal);
        Assert.Contains("ResizeExpandedToContent();", source, StringComparison.Ordinal);
        Assert.Contains("AvailableExpandedContentHeight(hasGeometry, geometry, HeaderScreenRect())", source, StringComparison.Ordinal);
        Assert.DoesNotContain("contentMinHeight", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TimedRefresh_DoesNotReplaceFocusedDashboardInput()
    {
        var workspace = FindWorkspace();
        var source = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "Views",
            "LifeIslandWindow.xaml.cs"));

        Assert.Contains(
            "todayDashboardContent?.IsKeyboardFocusWithin != true",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "quickInput.TextChanged += (_, _) => Touch();",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MusicHeader_DoesNotShowTheDefaultHeaderStatusLight()
    {
        var workspace = FindWorkspace();
        var xaml = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "Views",
            "LifeIslandWindow.xaml"));

        Assert.DoesNotContain("x:Name=\"CollapsedMediaStatusLight\"", xaml, StringComparison.Ordinal);
    }

    static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ChronoIsle.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("ChronoIsle.sln was not found from the UI test host.");
    }
}

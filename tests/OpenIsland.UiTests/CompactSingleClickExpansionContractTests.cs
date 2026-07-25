using System.IO;

namespace OpenIsland.UiTests;

public sealed class CompactSingleClickExpansionContractTests
{
    [Fact]
    public void SingleClickExpansion_ShowsNavigationAndIncludesItInHeight()
    {
        var workspace = FindWorkspace();
        var source = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "OpenIsland.App",
            "Views",
            "LifeIslandWindow.xaml.cs"));

        Assert.Contains("else Expand();", source, StringComparison.Ordinal);
        Assert.Contains("void Expand()", source, StringComparison.Ordinal);
        Assert.Contains("DashboardTabs.Visibility = Visibility.Visible;", source, StringComparison.Ordinal);
        Assert.Contains("ExpandedScrollViewer.ScrollToTop();", source, StringComparison.Ordinal);
        Assert.Contains("const double contentMinHeight = 620;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("compactContentMinHeight", source, StringComparison.Ordinal);
    }

    static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "OpenIsland.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("OpenIsland.sln was not found from the UI test host.");
    }
}

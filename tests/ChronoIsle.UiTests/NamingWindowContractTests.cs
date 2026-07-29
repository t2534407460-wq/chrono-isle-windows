using System.IO;

namespace ChronoIsle.UiTests;

public sealed class NamingWindowContractTests
{
    [Fact]
    public void Naming_window_uses_custom_chrome_and_exposes_critical_controls()
    {
        var workspace = FindWorkspace();
        var xaml = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "Views",
            "NamingWindow.xaml"));

        Assert.Contains("WindowStyle=\"None\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AllowsTransparency=\"True\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<ProgressBar", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("MessageBox", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ContentScroller\" Grid.Row=\"2\"", xaml, StringComparison.Ordinal);

        foreach (var automationId in new[]
                 {
                     "NamingWindow",
                     "NamingMeaningInput",
                     "NamingKindSelector",
                     "NamingGenerateButton",
                     "NamingLoadingState",
                     "NamingStatusNotice",
                     "NamingEmptyState",
                     "NamingResultCard",
                     "NamingRecommendedCard",
                     "NamingCopyRecommendedButton",
                     "NamingPreviousButton",
                     "NamingNextButton",
                     "NamingFormatRows"
                 })
            Assert.Contains(
                $"AutomationProperties.AutomationId=\"{automationId}\"",
                xaml,
                StringComparison.Ordinal);
    }

    [Fact]
    public void Island_exposes_a_text_only_tools_tab_with_naming_as_its_first_tool()
    {
        var workspace = FindWorkspace();
        var xaml = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "Views",
            "LifeIslandWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "Views",
            "LifeIslandWindow.xaml.cs"));

        Assert.Contains(
            "x:Name=\"ToolsDashboardTab\" Content=\"工具\" Click=\"ToolsTab_Click\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"✎ 工具\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ToolsPanel\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NamingToolTab\" Content=\"取名\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.AutomationId=\"IslandNamingButton\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("void ShowToolsDashboard()", code, StringComparison.Ordinal);
        Assert.Contains("SelectDashboardTab(ToolsDashboardTab);", code, StringComparison.Ordinal);
        Assert.Contains("void NamingTool_Click", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Today_dashboard_does_not_repeat_quick_ask_or_naming()
    {
        var workspace = FindWorkspace();
        var code = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "Views",
            "LifeIslandWindow.xaml.cs"));

        Assert.Contains(
            "new[] { IslandQuickAction.AddTodo, IslandQuickAction.StartFocus, IslandQuickAction.ManageItems }",
            code,
            StringComparison.Ordinal);
        Assert.DoesNotContain("IslandQuickAction.QuickAsk", code, StringComparison.Ordinal);
        Assert.DoesNotContain("IslandQuickAction.Naming", code, StringComparison.Ordinal);
    }

    static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ChronoIsle.sln")))
                return directory.FullName;
        throw new DirectoryNotFoundException(
            "ChronoIsle.sln was not found from the UI test host.");
    }
}

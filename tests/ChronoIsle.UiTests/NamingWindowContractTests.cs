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
    public void Island_naming_action_is_automated_and_not_added_to_context_menu()
    {
        var workspace = FindWorkspace();
        var code = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "Views",
            "LifeIslandWindow.xaml.cs"));

        Assert.Contains(
            "AutomationProperties.SetAutomationId(button, \"IslandNamingButton\")",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "Where(action => action != IslandQuickAction.Naming)",
            code,
            StringComparison.Ordinal);
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

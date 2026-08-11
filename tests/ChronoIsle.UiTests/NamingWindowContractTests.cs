using System.IO;
using System.Xml.Linq;

namespace ChronoIsle.UiTests;

public sealed class NamingWindowContractTests
{
    [Fact]
    public void Island_embeds_the_full_naming_tool_and_has_no_standalone_naming_window()
    {
        var workspace = FindWorkspace();
        var xaml = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "Views",
            "LifeIslandWindow.xaml"));
        var project = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "ChronoIsle.App.csproj"));
        var app = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "App.xaml.cs"));

        var document = XDocument.Parse(xaml);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var panel = document.Descendants()
            .FirstOrDefault(element => (string?)element.Attribute(x + "Name") == "NamingToolPanel");
        Assert.True(panel is not null, "NamingToolPanel was not found in LifeIslandWindow.xaml.");
        var panelXaml = panel!.ToString();
        foreach (var automationId in new[]
                 {
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
                panelXaml,
                StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "Views",
            "NamingWindow.xaml")));
        Assert.False(File.Exists(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "Views",
            "NamingWindow.xaml.cs")));
        Assert.DoesNotContain("NamingWindow", project, StringComparison.Ordinal);
        Assert.DoesNotContain("NamingWindow", app, StringComparison.Ordinal);
    }

    [Fact]
    public void Naming_entries_open_the_island_naming_tab_instead_of_a_standalone_window()
    {
        var workspace = FindWorkspace();
        var app = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "App.xaml.cs"));
        var island = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "Views",
            "LifeIslandWindow.xaml.cs"));

        var openNamingTool = ExtractMethodBody(island, "public void OpenNamingTool()");

        Assert.Contains("Show();", openNamingTool, StringComparison.Ordinal);
        Assert.Contains("Expand();", openNamingTool, StringComparison.Ordinal);
        Assert.Contains("ShowToolsDashboard();", openNamingTool, StringComparison.Ordinal);
        Assert.Contains("ShowNamingTool();", openNamingTool, StringComparison.Ordinal);
        Assert.Contains("NamingContentScroller.ScrollToTop();", openNamingTool, StringComparison.Ordinal);
        Assert.Contains("MeaningInput.Focus();", openNamingTool, StringComparison.Ordinal);
        Assert.Contains("Touch();", openNamingTool, StringComparison.Ordinal);
        Assert.Contains("OpenNamingTool();", app, StringComparison.Ordinal);
    }

    [Fact]
    public void Today_dashboard_does_not_repeat_toolbar_or_tools_actions()
    {
        var workspace = FindWorkspace();
        var code = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "Views",
            "LifeIslandWindow.xaml.cs"));

        Assert.Contains(
            "new[] { IslandQuickAction.AddTodo, IslandQuickAction.StartFocus }",
            code,
            StringComparison.Ordinal);
        Assert.DoesNotContain("IslandQuickAction.ManageItems }", code, StringComparison.Ordinal);
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

    static string ExtractMethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Method signature was not found: {signature}.");
        var openingBrace = source.IndexOf('{', start + signature.Length);
        Assert.True(openingBrace >= 0, $"Opening brace was not found for {signature}.");

        var depth = 0;
        for (var index = openingBrace; index < source.Length; index++)
        {
            if (source[index] == '{') depth++;
            if (source[index] != '}') continue;
            if (--depth == 0) return source[start..(index + 1)];
        }

        throw new InvalidOperationException($"Closing brace was not found for {signature}.");
    }
}

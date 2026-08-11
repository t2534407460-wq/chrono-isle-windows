using System.IO;

namespace ChronoIsle.UiTests;

public sealed class TaskbarTopmostContractTests
{
    [Fact]
    public void Expanded_pin_controls_when_taskbar_placement_is_topmost()
    {
        var workspace = FindWorkspace();
        var xaml = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "Views",
            "LifeIslandWindow.xaml"));
        var source = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "Views",
            "LifeIslandWindow.xaml.cs"));

        Assert.DoesNotContain("Topmost=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ExpandedPinButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ExpandedPinOutline", xaml, StringComparison.Ordinal);
        Assert.Contains("ExpandedPinSolid", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"ExpandedPin_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("enum ExpandedPinState", source, StringComparison.Ordinal);
        var expandedPinClick = ExtractMethodBody(source, "void ExpandedPin_Click(");
        var setExpandedPinState = ExtractMethodBody(source, "void SetExpandedPinState(");
        var ensureTaskbarTopmost = ExtractMethodBody(source, "void EnsureTaskbarTopmost()");
        var collapse = ExtractMethodBody(source, "void Collapse()");

        Assert.Contains(
            "ExpandedPinState.Normal => ExpandedPinState.KeepExpanded",
            expandedPinClick,
            StringComparison.Ordinal);
        Assert.Contains(
            "ExpandedPinState.KeepExpanded => ExpandedPinState.Topmost",
            expandedPinClick,
            StringComparison.Ordinal);
        Assert.Contains("_ => ExpandedPinState.Normal", expandedPinClick, StringComparison.Ordinal);
        Assert.Contains("Topmost = state == ExpandedPinState.Topmost", setExpandedPinState, StringComparison.Ordinal);
        Assert.Contains("ExpandedPinSolid.Visibility", setExpandedPinState, StringComparison.Ordinal);
        Assert.Contains(
            "placement != IslandPlacement.Taskbar || expandedPinState != ExpandedPinState.Topmost",
            ensureTaskbarTopmost,
            StringComparison.Ordinal);
        Assert.Contains("SetWindowPos(", ensureTaskbarTopmost, StringComparison.Ordinal);
        Assert.Contains("SetExpandedPinState(ExpandedPinState.Normal)", collapse, StringComparison.Ordinal);
    }

    [Fact]
    public void TaskbarPlacement_ReassertsHighestTopmostWithoutTakingFocus()
    {
        var workspace = FindWorkspace();
        var source = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
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
            if (File.Exists(Path.Combine(directory.FullName, "ChronoIsle.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("ChronoIsle.sln was not found from the UI test host.");
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

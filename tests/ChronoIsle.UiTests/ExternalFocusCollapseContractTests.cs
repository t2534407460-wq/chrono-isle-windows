using System.IO;

namespace ChronoIsle.UiTests;

public sealed class ExternalFocusCollapseContractTests
{
    [Fact]
    public void Auto_collapse_only_applies_when_the_expanded_pin_is_normal()
    {
        var source = ReadIslandSource();
        Assert.Contains("collapseTimer.Tick += (_, _) => AutoCollapse();", source, StringComparison.Ordinal);
        var autoCollapse = ExtractMethodBody(source, "void AutoCollapse()");
        var externalFocusCollapse = ExtractMethodBody(
            source,
            "void CollapseWhenForegroundMovesToAnotherProcess()");

        Assert.Contains(
            "if (expandedPinState == ExpandedPinState.Normal) Collapse();",
            autoCollapse,
            StringComparison.Ordinal);
        Assert.Contains("if (expanded) AutoCollapse();", externalFocusCollapse, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpandedIsland_CollapsesWhenForegroundMovesToAnotherProcess()
    {
        var source = ReadIslandSource();

        Assert.Contains("if (HasOpenDropDown())", source, StringComparison.Ordinal);
        Assert.Contains("Deactivated += (_, _) => Dispatcher.BeginInvoke(", source, StringComparison.Ordinal);
        Assert.Contains("new Action(CollapseWhenForegroundMovesToAnotherProcess)", source, StringComparison.Ordinal);
        Assert.Contains("var foregroundWindow = GetForegroundWindow();", source, StringComparison.Ordinal);
        Assert.Contains("GetWindowThreadProcessId(foregroundWindow, out var foregroundProcessId);", source, StringComparison.Ordinal);
        Assert.Contains("foregroundProcessId == (uint)Environment.ProcessId", source, StringComparison.Ordinal);
        Assert.Contains("headerSingleClickTimer.Stop();", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingFiveSecondMouseLeaveCollapse_RemainsEnabled()
    {
        var source = ReadIslandSource();

        Assert.Contains("Interval = TimeSpan.FromSeconds(5)", source, StringComparison.Ordinal);
        Assert.Contains("void Island_MouseLeave(", source, StringComparison.Ordinal);
        Assert.Contains("ScheduleMouseLeaveCollapse();", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TimeSelectorDropDown_DoesNotTriggerExternalFocusCollapse()
    {
        var source = ReadIslandSource();
        var xaml = ReadIslandXaml();

        Assert.Contains("DropDownOpened=\"TimeSelector_DropDownOpened\"", xaml, StringComparison.Ordinal);
        Assert.Contains("DropDownClosed=\"TimeSelector_DropDownClosed\"", xaml, StringComparison.Ordinal);
        Assert.Contains("void TimeSelector_DropDownOpened(", source, StringComparison.Ordinal);
        Assert.Contains("void TimeSelector_DropDownClosed(", source, StringComparison.Ordinal);
        Assert.Contains("if (!pointerHover && expanded)", source, StringComparison.Ordinal);
    }

    static string ReadIslandSource()
    {
        var workspace = FindWorkspace();
        return File.ReadAllText(Path.Combine(workspace, "src", "ChronoIsle.App", "Views", "LifeIslandWindow.xaml.cs"));
    }

    static string ReadIslandXaml()
    {
        var workspace = FindWorkspace();
        return File.ReadAllText(Path.Combine(workspace, "src", "ChronoIsle.App", "Views", "LifeIslandWindow.xaml"));
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

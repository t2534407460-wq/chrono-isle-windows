using System.IO;

namespace ChronoIsle.UiTests;

public sealed class TaskbarTopmostContractTests
{
    [Fact]
    public void Island_is_always_topmost_and_the_expanded_pin_has_only_two_states()
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
        var click = ExtractMethodBody(source, "void ExpandedPin_Click(");
        var setState = ExtractMethodBody(source, "void SetExpandedPinState(");

        Assert.Contains("Topmost=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("enum ExpandedPinState { Normal, KeepExpanded }", source, StringComparison.Ordinal);
        Assert.Contains("expandedPinState == ExpandedPinState.Normal", click, StringComparison.Ordinal);
        Assert.Contains("? ExpandedPinState.KeepExpanded", click, StringComparison.Ordinal);
        Assert.Contains(": ExpandedPinState.Normal", click, StringComparison.Ordinal);
        Assert.Contains("ExpandedPinSolid.Visibility", setState, StringComparison.Ordinal);
        Assert.DoesNotContain("ExpandedPinState.Topmost", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Topmost = state", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Island_topmost_does_not_depend_on_taskbar_or_periodic_native_reassertion()
    {
        var workspace = FindWorkspace();
        var source = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "Views",
            "LifeIslandWindow.xaml.cs"));

        Assert.DoesNotContain("taskbarTopmostTimer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureTaskbarTopmost", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SetWindowPos(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("HwndTopmost", source, StringComparison.Ordinal);
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

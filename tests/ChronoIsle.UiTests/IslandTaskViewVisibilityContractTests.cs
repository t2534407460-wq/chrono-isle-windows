using System.IO;

namespace ChronoIsle.UiTests;

public sealed class IslandTaskViewVisibilityContractTests
{
    [Theory]
    [InlineData("LifeIslandWindow.xaml.cs")]
    [InlineData("DynamicIslandWindow.xaml.cs")]
    public void Island_window_applies_tool_window_style_after_source_initialization(string fileName)
    {
        var source = ReadViewSource(fileName);

        Assert.Contains("SourceInitialized +=", source, StringComparison.Ordinal);
        Assert.Contains("IslandWindowStyles.HideFromTaskView(this);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Tool_window_style_preserves_existing_extended_styles()
    {
        var source = ReadViewSource("IslandWindowStyles.cs");

        Assert.Contains("WsExToolWindow", source, StringComparison.Ordinal);
        Assert.Contains("currentStyle | WsExToolWindow", source, StringComparison.Ordinal);
        Assert.Contains("GetWindowLongPtr", source, StringComparison.Ordinal);
        Assert.Contains("SetWindowLongPtr", source, StringComparison.Ordinal);
    }

    static string ReadViewSource(string fileName)
    {
        var workspace = FindWorkspace();
        return File.ReadAllText(Path.Combine(
            workspace, "src", "ChronoIsle.App", "Views", fileName));
    }

    static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ChronoIsle.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("ChronoIsle.sln was not found from the UI test host.");
    }
}

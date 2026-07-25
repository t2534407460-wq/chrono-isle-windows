using System.IO;

namespace OpenIsland.UiTests;

public sealed class TaskbarGapJumpAnimationContractTests
{
    [Fact]
    public void TaskbarDrag_UsesPointerDrivenAnimatedGapTransitions()
    {
        var workspace = FindWorkspace();
        var source = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "OpenIsland.App",
            "Views",
            "LifeIslandWindow.xaml.cs"));

        Assert.Contains("taskbarDragPointerStartPixels", source, StringComparison.Ordinal);
        Assert.Contains("PlaceAvoidingOccupiedRanges(", source, StringComparison.Ordinal);
        Assert.Contains("AnimateTaskbarGapJump(", source, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMilliseconds(160)", source, StringComparison.Ordinal);
        Assert.Contains("EasingMode.EaseOut", source, StringComparison.Ordinal);
    }

    static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "OpenIsland.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("OpenIsland.sln was not found from the UI test host.");
    }
}

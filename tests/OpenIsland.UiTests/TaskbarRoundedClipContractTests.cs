using System.IO;

namespace OpenIsland.UiTests;

public sealed class TaskbarRoundedClipContractTests
{
    [Fact]
    public void TaskbarMode_ClipsAnimatedSurfaceToRoundedBounds()
    {
        var workspace = FindWorkspace();
        var source = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "OpenIsland.App",
            "Views",
            "LifeIslandWindow.xaml.cs"));

        Assert.Contains("MainBorder.SizeChanged += (_, _) => UpdateTaskbarClip();", source, StringComparison.Ordinal);
        Assert.Contains("if (placement != IslandPlacement.Taskbar ||", source, StringComparison.Ordinal);
        Assert.Contains("taskbarClipGeometry.Rect = new Rect(", source, StringComparison.Ordinal);
        Assert.Contains("taskbarClipGeometry.RadiusX = radius;", source, StringComparison.Ordinal);
        Assert.Contains("taskbarClipGeometry.RadiusY = radius;", source, StringComparison.Ordinal);
        Assert.Contains("MainBorder.Clip = taskbarClipGeometry;", source, StringComparison.Ordinal);
        Assert.Contains("MainBorder.Clip = null;", source, StringComparison.Ordinal);
    }

    static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "OpenIsland.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("OpenIsland.sln was not found from the UI test host.");
    }
}

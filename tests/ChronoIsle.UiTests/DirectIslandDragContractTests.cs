using System.IO;

namespace ChronoIsle.UiTests;

public sealed class DirectIslandDragContractTests
{
    [Fact]
    public void HeaderDrag_MovesTheWindowBodyWithoutNativeOutlineDrag()
    {
        var workspace = FindWorkspace();
        var source = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "Views",
            "LifeIslandWindow.xaml.cs"));

        Assert.DoesNotContain("DragMove();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ContinueTaskbarDragWithNativeMove", source, StringComparison.Ordinal);
        Assert.Contains("void UpdateFreeCustomDrag()", source, StringComparison.Ordinal);
        Assert.Contains("BeginDirectDrag();", source, StringComparison.Ordinal);
        Assert.Contains("freeCustomDragging = true;", source, StringComparison.Ordinal);
        Assert.Contains("Left = constrained.Left;", source, StringComparison.Ordinal);
        Assert.Contains("Top = constrained.Top;", source, StringComparison.Ordinal);
    }

    static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ChronoIsle.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("ChronoIsle.sln was not found from the UI test host.");
    }
}

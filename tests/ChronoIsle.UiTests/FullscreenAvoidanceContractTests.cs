using System.IO;

namespace ChronoIsle.UiTests;

public sealed class FullscreenAvoidanceContractTests
{
    [Fact]
    public void FullscreenReturn_RestoresFoldedTopDockAtItsUnfoldedAnchor()
    {
        var workspace = FindWorkspace();
        var source = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "Views",
            "LifeIslandWindow.xaml.cs"));

        Assert.Contains("bool fullscreenOriginalTopDockFolded;", source, StringComparison.Ordinal);
        Assert.Contains("double fullscreenOriginalTopDockUnfoldedTop;", source, StringComparison.Ordinal);
        Assert.Contains("fullscreenOriginalTopDockFolded = topDockFolded;", source, StringComparison.Ordinal);
        Assert.Contains("fullscreenOriginalTopDockUnfoldedTop = topDockUnfoldedTop;", source, StringComparison.Ordinal);
        Assert.Contains("RestoreTopDockFoldAfterFullscreen();", source, StringComparison.Ordinal);
        Assert.Contains("void RestoreTopDockFoldAfterFullscreen()", source, StringComparison.Ordinal);
        Assert.Contains("Top = double.IsFinite(fullscreenOriginalTopDockUnfoldedTop)", source, StringComparison.Ordinal);
        Assert.Contains("SetTopDockFolded(true);", source, StringComparison.Ordinal);
    }

    static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ChronoIsle.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("ChronoIsle.sln was not found from the UI test host.");
    }
}

using System.IO;

namespace ChronoIsle.UiTests;

public sealed class MusicLineLyricsContractTests
{
    [Fact]
    public void MusicLyrics_HighlightWholeLineWithoutWordLevelRendering()
    {
        var workspace = FindWorkspace();
        var island = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "Views",
            "LifeIslandWindow.xaml.cs"));

        Assert.Contains("LyricCurrent.Text = line.Text;", island, StringComparison.Ordinal);
        Assert.Contains("CollapsedMediaArtist.Text = line.Text;", island, StringComparison.Ordinal);
        Assert.DoesNotContain("SetKaraokeText", island, StringComparison.Ordinal);
        Assert.DoesNotContain("LineProgress(", island, StringComparison.Ordinal);
    }

    [Fact]
    public void MusicProgress_PrefersSampledSystemTimelineBeforeOcrFallback()
    {
        var workspace = FindWorkspace();
        var island = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "Views",
            "LifeIslandWindow.xaml.cs"));

        var systemTimeline = island.IndexOf(
            "if (snapshot.Duration > TimeSpan.Zero)",
            StringComparison.Ordinal);
        var ocrFallback = island.IndexOf(
            "var sourcePosition = sourceLyricTimeline.Update(",
            StringComparison.Ordinal);

        Assert.True(systemTimeline >= 0);
        Assert.True(ocrFallback > systemTimeline);
        Assert.Contains("snapshot.ProgressSampledAtUtc", island, StringComparison.Ordinal);
    }
    static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ChronoIsle.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("ChronoIsle.sln was not found from the UI test host.");
    }
}

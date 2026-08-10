using System.IO;

namespace ChronoIsle.UiTests;

public sealed class MusicLineLyricsContractTests
{
    [Fact]
    public void CollapsedIsland_RetainsMusicModeWithoutLyricsSettings()
    {
        var workspace = FindWorkspace();
        var island = File.ReadAllText(Path.Combine(
            workspace, "src", "ChronoIsle.App", "Views", "LifeIslandWindow.xaml.cs"));
        var app = File.ReadAllText(Path.Combine(
            workspace, "src", "ChronoIsle.App", "App.xaml.cs"));

        Assert.Contains("currentPreferences.IslandShowMusicMode &&", island, StringComparison.Ordinal);
        Assert.Contains("ToggleMusicMode", island, StringComparison.Ordinal);
        Assert.Contains("UpdateCollapsedMediaView(collapsedMedia, currentPreferences);", island, StringComparison.Ordinal);
        Assert.Contains("media.StartAsync()", app, StringComparison.Ordinal);
        Assert.Contains("AudioSpectrumService>().Start();", app, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_ExposesMusicTakeoverButNotLyricsControls()
    {
        var workspace = FindWorkspace();
        var settings = File.ReadAllText(Path.Combine(
            workspace, "src", "ChronoIsle.App", "Views", "LifeSettingsWindow.xaml"));
        var settingsSource = File.ReadAllText(Path.Combine(
            workspace, "src", "ChronoIsle.App", "Views", "LifeSettingsWindow.xaml.cs"));

        Assert.Contains("x:Name=\"MediaAutoTakeover\"", settings, StringComparison.Ordinal);
        Assert.Contains("MediaAutoTakeover.IsChecked", settingsSource, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"LyricsEnabled\"", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"LyricsOffsetMs\"", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("LyricsEnabled", settingsSource, StringComparison.Ordinal);
        Assert.DoesNotContain("LyricsOffsetMs", settingsSource, StringComparison.Ordinal);
    }

    static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ChronoIsle.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("ChronoIsle.sln was not found from the UI test host.");
    }
}

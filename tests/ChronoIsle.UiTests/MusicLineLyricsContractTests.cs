using System.IO;

namespace ChronoIsle.UiTests;

public sealed class MusicLineLyricsContractTests
{
    [Fact]
    public void CollapsedIsland_DoesNotTakeOverForMusic()
    {
        var workspace = FindWorkspace();
        var island = File.ReadAllText(Path.Combine(
            workspace, "src", "ChronoIsle.App", "Views", "LifeIslandWindow.xaml.cs"));
        var app = File.ReadAllText(Path.Combine(
            workspace, "src", "ChronoIsle.App", "App.xaml.cs"));

        Assert.DoesNotContain("currentPreferences.IslandShowMusicMode &&", island, StringComparison.Ordinal);
        Assert.DoesNotContain("ToggleMusicMode", island, StringComparison.Ordinal);
        Assert.Contains("UpdateCollapsedMediaView(null, currentPreferences);", island, StringComparison.Ordinal);
        Assert.DoesNotContain("media.StartAsync()", app, StringComparison.Ordinal);
        Assert.DoesNotContain("AudioSpectrumService>().Start();", app, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_DoesNotExposeRetiredMusicTakeover()
    {
        var workspace = FindWorkspace();
        var settings = File.ReadAllText(Path.Combine(
            workspace, "src", "ChronoIsle.App", "Views", "LifeSettingsWindow.xaml"));
        var settingsSource = File.ReadAllText(Path.Combine(
            workspace, "src", "ChronoIsle.App", "Views", "LifeSettingsWindow.xaml.cs"));

        Assert.DoesNotContain("x:Name=\"MediaAutoTakeover\"", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("MediaAutoTakeover.IsChecked", settingsSource, StringComparison.Ordinal);
    }

    static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ChronoIsle.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("ChronoIsle.sln was not found from the UI test host.");
    }
}

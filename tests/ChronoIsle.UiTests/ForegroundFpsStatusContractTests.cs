using System.IO;

namespace ChronoIsle.UiTests;

public sealed class ForegroundFpsStatusContractTests
{
    [Fact]
    public void Island_ShowsForegroundFpsBetweenCpuAndMemory()
    {
        var workspace = FindWorkspace();
        var xaml = File.ReadAllText(Path.Combine(
            workspace, "src", "ChronoIsle.App", "Views", "LifeIslandWindow.xaml"));
        var island = File.ReadAllText(Path.Combine(
            workspace, "src", "ChronoIsle.App", "Views", "LifeIslandWindow.xaml.cs"));
        var settings = File.ReadAllText(Path.Combine(
            workspace, "src", "ChronoIsle.App", "Views", "LifeSettingsWindow.xaml"));
        var app = File.ReadAllText(Path.Combine(
            workspace, "src", "ChronoIsle.App", "App.xaml.cs"));

        Assert.Contains("x:Name=\"FpsSummary\"", xaml, StringComparison.Ordinal);
        Assert.True(xaml.IndexOf("x:Name=\"FpsSummary\"", StringComparison.Ordinal) <
                    xaml.IndexOf("x:Name=\"MemoryUsageSummary\"", StringComparison.Ordinal));
        Assert.True(settings.IndexOf("x:Name=\"IslandShowMemoryUsage\"", StringComparison.Ordinal) <
                    settings.IndexOf("x:Name=\"IslandShowFps\"", StringComparison.Ordinal));
        Assert.Contains("collection.AddSingleton<ForegroundFpsService>();", app, StringComparison.Ordinal);
        Assert.Contains("foregroundFps.Start();", app, StringComparison.Ordinal);
        Assert.Contains("foregroundFps.Stop();", app, StringComparison.Ordinal);
        Assert.Contains("currentPreferences.TelemetryEnabled && currentPreferences.IslandShowFps", island, StringComparison.Ordinal);
        Assert.Contains("FpsSummary.Text = snapshot.FramesPerSecond is { } fps ? $\"FPS {fps:0}\" : \"FPS --\";", island, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"IslandShowFps\"", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsAndIsland_RetireNotificationsButKeepMusicMode()
    {
        var workspace = FindWorkspace();
        var settingsXaml = File.ReadAllText(Path.Combine(
            workspace, "src", "ChronoIsle.App", "Views", "LifeSettingsWindow.xaml"));
        var island = File.ReadAllText(Path.Combine(
            workspace, "src", "ChronoIsle.App", "Views", "LifeIslandWindow.xaml.cs"));
        var app = File.ReadAllText(Path.Combine(
            workspace, "src", "ChronoIsle.App", "App.xaml.cs"));

        Assert.DoesNotContain("x:Name=\"ToastInboxEnabled\"", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("StartToastInboxAsync", app, StringComparison.Ordinal);
        Assert.DoesNotContain("SystemToastInboxService", app, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"MediaAutoTakeover\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("ToggleMusicMode", island, StringComparison.Ordinal);
        Assert.Contains("currentPreferences.IslandShowMusicMode &&", island, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_UsesEtwPresentEventsWithoutWindowCapture()
    {
        var workspace = FindWorkspace();
        var service = File.ReadAllText(Path.Combine(
            workspace, "src", "ChronoIsle.App", "Services", "ForegroundFpsService.cs"));

        Assert.Contains("TraceEventSession", service, StringComparison.Ordinal);
        Assert.Contains("Microsoft-Windows-DxgKrnl", service, StringComparison.Ordinal);
        Assert.Contains("DxgKrnlPresentKeyword", service, StringComparison.Ordinal);
        Assert.DoesNotContain("Windows.Graphics.Capture", service, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessStartInfo", service, StringComparison.Ordinal);
    }

    static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ChronoIsle.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("ChronoIsle.sln was not found from the UI test host.");
    }
}

using System.IO;

namespace ChronoIsle.UiTests;

public sealed class SettingsWindowDependencyContractTests
{
    [Fact]
    public void SettingsWindow_RegistersItsLyricsServiceDependency()
    {
        var workspace = FindWorkspace();
        var app = File.ReadAllText(Path.Combine(workspace, "src", "ChronoIsle.App", "App.xaml.cs"));
        var settings = File.ReadAllText(Path.Combine(workspace, "src", "ChronoIsle.App", "Views", "LifeSettingsWindow.xaml.cs"));

        Assert.Contains("LyricsService lyrics", settings, StringComparison.Ordinal);
        Assert.Contains("collection.AddSingleton<LyricsService>();", app, StringComparison.Ordinal);
    }

    static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ChronoIsle.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("ChronoIsle.sln was not found from the UI test host.");
    }
}

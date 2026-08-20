using System.IO;

namespace ChronoIsle.UiTests;

public sealed class IslandRefreshResilienceContractTests
{
    [Fact]
    public void Island_ClockTickKeepsTimeMovingWhenStateStoreReadUsesDisposedHandle()
    {
        var source = File.ReadAllText(Path.Combine(FindWorkspace(), "src", "ChronoIsle.App", "Views", "LifeIslandWindow.xaml.cs"));

        Assert.Contains("clockTimer.Tick += (_, _) => RefreshClockAndState();", source, StringComparison.Ordinal);
        Assert.Contains("void RefreshClockAndState()", source, StringComparison.Ordinal);
        var refresh = source[source.IndexOf("void RefreshClockAndState()", StringComparison.Ordinal)..source.IndexOf("void Refresh()", StringComparison.Ordinal)];
        Assert.Contains("catch (ObjectDisposedException exception)", refresh, StringComparison.Ordinal);
        Assert.True(
            refresh.IndexOf("Clock.Text = DateTime.Now.ToString(\"HH:mm:ss\");", StringComparison.Ordinal) <
            refresh.IndexOf("try", StringComparison.Ordinal));
    }

    static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ChronoIsle.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("ChronoIsle.sln was not found from the UI test host.");
    }
}

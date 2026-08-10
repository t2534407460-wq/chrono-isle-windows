using System.IO;

namespace ChronoIsle.UiTests;

public sealed class NetworkSpeedTestUiContractTests
{
    [Fact]
    public void Tools_ProvidesThemedNetworkSpeedTestStructure()
    {
        var workspace = FindWorkspace();
        var island = File.ReadAllText(Path.Combine(
            workspace, "src", "ChronoIsle.App", "Views", "LifeIslandWindow.xaml"));
        var controls = File.ReadAllText(Path.Combine(
            workspace, "src", "ChronoIsle.App", "Resources", "Controls.xaml"));
        var tab = Element(island, "x:Name=\"NetworkSpeedTestToolTab\"");
        var namingPanel = Element(island, "x:Name=\"NamingToolPanel\"");
        var panel = NetworkSpeedTestPanel(island);
        var button = Element(panel, "x:Name=\"NetworkSpeedTestStartButton\"");
        var style = Element(island, "x:Key=\"IslandNetworkSpeedTestPrimary\"", "</Style>");

        Assert.Contains("x:Name=\"NetworkSpeedTestToolTab\" Content=\"网络测速\"", tab, StringComparison.Ordinal);
        Assert.DoesNotContain("Click=", tab, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NetworkSpeedTestToolPanel\" Visibility=\"Collapsed\"", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("Visibility=", namingPanel, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NetworkSpeedTestGaugeNeedleRotation\"", panel, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NetworkSpeedTestSpinnerRotation\"", panel, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NetworkSpeedTestStartButton\" Content=\"开始测速\" Style=\"{StaticResource IslandNetworkSpeedTestPrimary}\"", button, StringComparison.Ordinal);
        Assert.DoesNotContain("Click=", button, StringComparison.Ordinal);
        Assert.Contains("{DynamicResource Brush.Accent}", panel, StringComparison.Ordinal);
        Assert.Contains("{DynamicResource Brush.AccentSoft}", panel, StringComparison.Ordinal);
        Assert.Contains("BasedOn=\"{StaticResource IslandPrimary}\"", style, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Background\" Value=\"{DynamicResource Brush.Accent}\"/>", style, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"BorderBrush\" Value=\"{DynamicResource Brush.Accent}\"/>", style, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Foreground\" Value=\"{DynamicResource Brush.Island}\"/>", style, StringComparison.Ordinal);

        foreach (var name in new[]
        {
            "NetworkSpeedTestGaugeValue",
            "NetworkSpeedTestGaugeUnit",
            "NetworkSpeedTestPhaseText",
            "NetworkSpeedTestDownloadValue",
            "NetworkSpeedTestUploadValue",
            "NetworkSpeedTestNodeLatencyValue",
            "NetworkSpeedTestLeagueLatency",
            "NetworkSpeedTestDouyinLatency",
            "NetworkSpeedTestJdLatency",
            "NetworkSpeedTestCtripLatency",
            "NetworkSpeedTestToutiaoLatency"
        })
            Assert.Contains($"x:Name=\"{name}\"", panel, StringComparison.Ordinal);

        AssertPlatformCard(panel, "Icon.PlatformLeague", "英雄联盟", "NetworkSpeedTestLeagueLatency");
        AssertPlatformCard(panel, "Icon.PlatformDouyin", "抖音", "NetworkSpeedTestDouyinLatency");
        AssertPlatformCard(panel, "Icon.PlatformJd", "京东", "NetworkSpeedTestJdLatency");
        AssertPlatformCard(panel, "Icon.PlatformCtrip", "携程", "NetworkSpeedTestCtripLatency");
        AssertPlatformCard(panel, "Icon.PlatformToutiao", "今日头条", "NetworkSpeedTestToutiaoLatency");

        foreach (var key in new[]
        {
            "Icon.PlatformLeague",
            "Icon.PlatformDouyin",
            "Icon.PlatformJd",
            "Icon.PlatformCtrip",
            "Icon.PlatformToutiao"
        })
            Assert.Contains($"x:Key=\"{key}\"", controls, StringComparison.Ordinal);
    }

    static void AssertPlatformCard(string panel, string icon, string platform, string latencyName)
    {
        Assert.Contains($"Data=\"{{StaticResource {icon}}}\"", panel, StringComparison.Ordinal);
        Assert.Contains($"Text=\"{platform}\"", panel, StringComparison.Ordinal);
        Assert.Contains($"x:Name=\"{latencyName}\" Text=\"未测得\"", panel, StringComparison.Ordinal);
    }

    static string NetworkSpeedTestPanel(string island)
    {
        var panelStart = island.IndexOf("x:Name=\"NetworkSpeedTestToolPanel\"", StringComparison.Ordinal);
        Assert.True(panelStart >= 0, "The network-speed test panel is missing.");
        var buttonStart = island.IndexOf("x:Name=\"NetworkSpeedTestStartButton\"", panelStart, StringComparison.Ordinal);
        Assert.True(buttonStart >= 0, "The network-speed test button is missing.");
        var buttonEnd = island.IndexOf("/>", buttonStart, StringComparison.Ordinal);
        Assert.True(buttonEnd >= 0, "The network-speed test button is not a complete element.");
        return island[panelStart..(buttonEnd + 2)];
    }

    static string Element(string source, string marker, string endMarker = "/>")
    {
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"The element containing {marker} is missing.");
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"The element containing {marker} is incomplete.");
        return source[start..(end + endMarker.Length)];
    }

    static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ChronoIsle.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("ChronoIsle.sln was not found from the UI test host.");
    }
}

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
        Assert.Contains("Click=\"NamingToolTab_Click\"", Element(island, "x:Name=\"NamingToolTab\""), StringComparison.Ordinal);
        Assert.Contains("Click=\"NetworkSpeedTestToolTab_Click\"", tab, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NetworkSpeedTestToolPanel\" Visibility=\"Collapsed\"", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("Visibility=", namingPanel, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NetworkSpeedTestGaugeNeedleRotation\"", panel, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NetworkSpeedTestSpinnerRotation\"", panel, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NetworkSpeedTestStartButton\" Content=\"开始测速\" Style=\"{StaticResource IslandNetworkSpeedTestPrimary}\"", button, StringComparison.Ordinal);
        Assert.Contains("Click=\"NetworkSpeedTestStartButton_Click\"", button, StringComparison.Ordinal);
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

    [Fact]
    public void NetworkSpeedTest_WiresServiceTabsCancellationAndAnimations()
    {
        var workspace = FindWorkspace();
        var app = File.ReadAllText(Path.Combine(workspace, "src", "ChronoIsle.App", "App.xaml.cs"));
        var island = File.ReadAllText(Path.Combine(workspace, "src", "ChronoIsle.App", "Views", "LifeIslandWindow.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(workspace, "src", "ChronoIsle.App", "Views", "LifeIslandWindow.xaml.cs"));
        var closedHandlerStart = codeBehind.IndexOf("Closed += (_, _) =>", StringComparison.Ordinal);
        var closedHandlerEnd = codeBehind.IndexOf("        };", closedHandlerStart, StringComparison.Ordinal);
        var snapshotHandlerStart = codeBehind.IndexOf("networkSpeedTestSnapshotChanged = snapshot =>", StringComparison.Ordinal);
        var snapshotHandlerEnd = codeBehind.IndexOf("networkSpeedTest.SnapshotChanged +=", snapshotHandlerStart, StringComparison.Ordinal);

        Assert.Contains("collection.AddSingleton<NetworkSpeedTestService>();", app, StringComparison.Ordinal);
        Assert.Contains("networkSpeedTest.SnapshotChanged += networkSpeedTestSnapshotChanged;", codeBehind, StringComparison.Ordinal);
        Assert.Contains("networkSpeedTest.SnapshotChanged -= networkSpeedTestSnapshotChanged;", codeBehind, StringComparison.Ordinal);
        Assert.Contains("networkSpeedTest.Cancel();", codeBehind, StringComparison.Ordinal);
        Assert.True(closedHandlerStart >= 0 && closedHandlerEnd > closedHandlerStart, "The Closed handler is missing.");
        var isClosedInClosedHandler = codeBehind.IndexOf("isClosed = true;", closedHandlerStart, StringComparison.Ordinal);
        var cancelInClosedHandler = codeBehind.IndexOf("networkSpeedTest.Cancel();", closedHandlerStart, StringComparison.Ordinal);
        var unsubscribeInClosedHandler = codeBehind.IndexOf("networkSpeedTest.SnapshotChanged -= networkSpeedTestSnapshotChanged;", closedHandlerStart, StringComparison.Ordinal);
        Assert.True(cancelInClosedHandler >= closedHandlerStart && cancelInClosedHandler < closedHandlerEnd, "The Closed handler must cancel the speed test.");
        Assert.True(unsubscribeInClosedHandler >= closedHandlerStart && unsubscribeInClosedHandler < cancelInClosedHandler, "The Closed handler must unsubscribe before cancellation.");
        Assert.True(
            isClosedInClosedHandler >= closedHandlerStart &&
            isClosedInClosedHandler < unsubscribeInClosedHandler &&
            unsubscribeInClosedHandler < cancelInClosedHandler &&
            cancelInClosedHandler < closedHandlerEnd,
            "The Closed handler must mark itself closed before unsubscribing and cancelling.");
        Assert.True(snapshotHandlerStart >= 0 && snapshotHandlerEnd > snapshotHandlerStart, "The speed-test snapshot handler is missing.");
        var snapshotHandler = codeBehind[snapshotHandlerStart..snapshotHandlerEnd];
        var dispatchStart = snapshotHandler.IndexOf("Dispatcher.BeginInvoke", StringComparison.Ordinal);
        var firstClosedGuard = snapshotHandler.IndexOf("if (isClosed || Dispatcher.HasShutdownStarted) return;", StringComparison.Ordinal);
        var secondClosedGuard = snapshotHandler.IndexOf("if (isClosed || Dispatcher.HasShutdownStarted) return;", dispatchStart, StringComparison.Ordinal);
        var updateStart = snapshotHandler.IndexOf("UpdateNetworkSpeedTestView(snapshot);", StringComparison.Ordinal);
        Assert.True(firstClosedGuard >= 0 && firstClosedGuard < dispatchStart, "The snapshot handler must short-circuit before dispatching.");
        Assert.True(dispatchStart < secondClosedGuard && secondClosedGuard < updateStart, "The dispatched update must short-circuit after closing.");
        Assert.Contains("async void NetworkSpeedTestStartButton_Click", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ShowNetworkSpeedTestTool", codeBehind, StringComparison.Ordinal);
        Assert.Contains("UpdateNetworkSpeedTestView", codeBehind, StringComparison.Ordinal);
        Assert.Contains("NetworkSpeedTestGaugeNeedleRotation.BeginAnimation", codeBehind, StringComparison.Ordinal);
        Assert.Contains("NetworkSpeedTestSpinnerRotation.BeginAnimation", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("#39C98B", island, StringComparison.Ordinal);
        Assert.DoesNotContain("#39C98B", codeBehind, StringComparison.Ordinal);
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

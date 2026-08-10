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
        var project = File.ReadAllText(Path.Combine(
            workspace, "src", "ChronoIsle.App", "ChronoIsle.App.csproj"));
        var tab = Element(island, "x:Name=\"NetworkSpeedTestToolTab\"");
        var namingPanel = Element(island, "x:Name=\"NamingToolPanel\"");
        var panel = NetworkSpeedTestPanel(island);
        var button = Element(panel, "x:Name=\"NetworkSpeedTestStartButton\"");
        var spinner = Element(panel, "x:Name=\"NetworkSpeedTestSpinnerPath\"");
        var progressArc = Element(panel, "x:Name=\"NetworkSpeedTestGaugeProgressArc\"", "</Path>");
        var style = Element(island, "x:Key=\"IslandNetworkSpeedTestPrimary\"", "</Style>");

        Assert.Contains("x:Name=\"NetworkSpeedTestToolTab\" Content=\"网络测速\"", tab, StringComparison.Ordinal);
        Assert.Contains("Click=\"NamingToolTab_Click\"", Element(island, "x:Name=\"NamingToolTab\""), StringComparison.Ordinal);
        Assert.Contains("Click=\"NetworkSpeedTestToolTab_Click\"", tab, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NetworkSpeedTestToolPanel\" Visibility=\"Collapsed\"", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("Visibility=", namingPanel, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NetworkSpeedTestGaugeNeedleRotation\"", panel, StringComparison.Ordinal);
        Assert.Contains("Data=\"M 20,122 A 98,98 0 0 1 216,122\"", spinner, StringComparison.Ordinal);
        Assert.Contains("StrokeDashArray=\"2 5\"", spinner, StringComparison.Ordinal);
        Assert.Contains("Stroke=\"{DynamicResource Brush.Accent}\"", spinner, StringComparison.Ordinal);
        Assert.Contains("StrokeStartLineCap=\"Round\"", spinner, StringComparison.Ordinal);
        Assert.Contains("StrokeEndLineCap=\"Round\"", spinner, StringComparison.Ordinal);
        Assert.DoesNotContain("NetworkSpeedTestSpinnerRotation", panel, StringComparison.Ordinal);
        Assert.Contains("Data=\"M 118,122 L 118,48\"", panel, StringComparison.Ordinal);
        Assert.Contains("CenterX=\"118\" CenterY=\"122\"", panel, StringComparison.Ordinal);
        Assert.Contains("Canvas.Left=\"112\" Canvas.Top=\"116\"", panel, StringComparison.Ordinal);
        Assert.Contains("<PathFigure StartPoint=\"20,122\">", panel, StringComparison.Ordinal);
        Assert.Contains("Point=\"20,122\"", progressArc, StringComparison.Ordinal);
        Assert.Contains("Size=\"98,98\"", progressArc, StringComparison.Ordinal);
        Assert.Contains("SweepDirection=\"Clockwise\"", progressArc, StringComparison.Ordinal);
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

        foreach (var (asset, icon) in new[]
        {
            ("league", "Icon.PlatformLeague"),
            ("douyin", "Icon.PlatformDouyin"),
            ("jd", "Icon.PlatformJd"),
            ("ctrip", "Icon.PlatformCtrip"),
            ("toutiao", "Icon.PlatformToutiao")
        })
        {
            Assert.Contains($"pack://application:,,,/ChronoIsle;component/Assets/platform-{asset}.ico", island, StringComparison.Ordinal);
            Assert.Contains($"<Resource Include=\"Assets\\platform-{asset}.ico\" />", project, StringComparison.Ordinal);
            Assert.DoesNotContain(icon, panel, StringComparison.Ordinal);
            Assert.DoesNotContain(icon, controls, StringComparison.Ordinal);

            var assetPath = Path.Combine(workspace, "src", "ChronoIsle.App", "Assets", $"platform-{asset}.ico");
            Assert.True(File.Exists(assetPath), $"The {asset} icon file is missing.");
            var bytes = File.ReadAllBytes(assetPath);
            Assert.True(bytes.Length > 0, $"The {asset} icon file is empty.");
            Assert.True(bytes.Length >= 4, $"The {asset} icon file is too short to contain an ICO signature.");
            Assert.Equal(new byte[] { 0, 0, 1, 0 }, bytes[..4]);
        }
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
        Assert.Contains("NetworkSpeedTestSpinnerPath.BeginAnimation(Shape.StrokeDashOffsetProperty", codeBehind, StringComparison.Ordinal);
        Assert.Contains("if (!SystemParameters.ClientAreaAnimation)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("From = 0", codeBehind, StringComparison.Ordinal);
        Assert.Contains("To = -14", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Duration = TimeSpan.FromMilliseconds(900)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("RepeatBehavior = RepeatBehavior.Forever", codeBehind, StringComparison.Ordinal);
        Assert.Contains("NetworkSpeedTestSpinnerPath.BeginAnimation(Shape.StrokeDashOffsetProperty, null)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("NetworkSpeedTestSpinnerPath.StrokeDashOffset = 0;", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("NetworkSpeedTestSpinnerRotation", codeBehind, StringComparison.Ordinal);
        Assert.Contains("static Point GetNetworkSpeedTestGaugeProgressPoint(double rate)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("return GetNetworkSpeedTestGaugeProgressPointForAngle(GetNetworkSpeedTestGaugeAngle(rate));", codeBehind, StringComparison.Ordinal);
        Assert.Contains("var radians = Math.PI / 180d * angle;", codeBehind, StringComparison.Ordinal);
        Assert.Contains("new Point(118 + 98 * Math.Sin(radians), 122 - 98 * Math.Cos(radians))", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("(GetNetworkSpeedTestGaugeAngle(rate) + 75) / 150", codeBehind, StringComparison.Ordinal);
        Assert.Contains("static Point GetNetworkSpeedTestGaugeProgressPointForAngle(double angle)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("NetworkSpeedTestGaugeProgressArc.Point = GetNetworkSpeedTestGaugeProgressPointForAngle(current);", codeBehind, StringComparison.Ordinal);
        Assert.Contains("CompositionTarget.Rendering += NetworkSpeedTestGaugeProgress_Rendering;", codeBehind, StringComparison.Ordinal);
        Assert.Contains("CompositionTarget.Rendering -= NetworkSpeedTestGaugeProgress_Rendering;", codeBehind, StringComparison.Ordinal);
        Assert.Contains("GetNetworkSpeedTestGaugeProgressPointForAngle(NetworkSpeedTestGaugeNeedleRotation.Angle)", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("NetworkSpeedTestGaugeProgressArc.BeginAnimation(ArcSegment.PointProperty", codeBehind, StringComparison.Ordinal);
        Assert.Contains("static double GetNetworkSpeedTestGaugeAngle(double rate)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("rate = Math.Clamp(rate, 0, 500);", codeBehind, StringComparison.Ordinal);
        Assert.Contains("rate <= 100", codeBehind, StringComparison.Ordinal);
        Assert.Contains("-90 + rate / 100d * 156", codeBehind, StringComparison.Ordinal);
        Assert.Contains("66 + Math.Log(1 + rate - 100) / Math.Log(401) * 24", codeBehind, StringComparison.Ordinal);
        Assert.Contains("var target = GetNetworkSpeedTestGaugeAngle(rate);", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Duration = TimeSpan.FromMilliseconds(180)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("var current = NetworkSpeedTestGaugeNeedleRotation.Angle;", codeBehind, StringComparison.Ordinal);
        Assert.Contains("FillBehavior = FillBehavior.HoldEnd", codeBehind, StringComparison.Ordinal);
        var stopGaugeStart = codeBehind.IndexOf("void StopNetworkSpeedTestGaugeAnimation()", StringComparison.Ordinal);
        var stopGaugeEnd = codeBehind.IndexOf("    void StartNetworkSpeedTestSpinner()", stopGaugeStart, StringComparison.Ordinal);
        Assert.True(stopGaugeStart >= 0 && stopGaugeEnd > stopGaugeStart, "The gauge stop method is missing.");
        var stopGauge = codeBehind[stopGaugeStart..stopGaugeEnd];
        var savedGaugeAngle = stopGauge.IndexOf("var current = NetworkSpeedTestGaugeNeedleRotation.Angle;", StringComparison.Ordinal);
        var clearGaugeAnimation = stopGauge.IndexOf("NetworkSpeedTestGaugeNeedleRotation.BeginAnimation(RotateTransform.AngleProperty, null);", StringComparison.Ordinal);
        var restoreGaugeAngle = stopGauge.IndexOf("NetworkSpeedTestGaugeNeedleRotation.Angle = current;", StringComparison.Ordinal);
        Assert.True(savedGaugeAngle >= 0 && savedGaugeAngle < clearGaugeAnimation && clearGaugeAnimation < restoreGaugeAngle,
            "Stopping the gauge animation must preserve its effective angle before clearing it.");
        Assert.DoesNotContain("#39C98B", island, StringComparison.Ordinal);
        Assert.DoesNotContain("#39C98B", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void NetworkSpeedTest_TogglesAndConvertsDisplayedRateUnit()
    {
        var workspace = FindWorkspace();
        var island = File.ReadAllText(Path.Combine(
            workspace, "src", "ChronoIsle.App", "Views", "LifeIslandWindow.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(
            workspace, "src", "ChronoIsle.App", "Views", "LifeIslandWindow.xaml.cs"));
        var panel = NetworkSpeedTestPanel(island);
        var unitButton = Element(panel, "x:Name=\"NetworkSpeedTestUnitButton\"");
        var phaseTextIndex = panel.IndexOf("x:Name=\"NetworkSpeedTestPhaseText\"", StringComparison.Ordinal);
        var unitButtonIndex = panel.IndexOf("x:Name=\"NetworkSpeedTestUnitButton\"", StringComparison.Ordinal);
        var enumStart = codeBehind.IndexOf("enum NetworkSpeedTestDisplayUnit", StringComparison.Ordinal);
        var enumEnd = codeBehind.IndexOf('}', enumStart);
        var handlerStart = codeBehind.IndexOf("void NetworkSpeedTestUnitButton_Click", StringComparison.Ordinal);
        var handlerEnd = codeBehind.IndexOf("    async void NetworkSpeedTestStartButton_Click", handlerStart, StringComparison.Ordinal);
        var formatStart = codeBehind.IndexOf("string FormatNetworkSpeedTestRate(double? rate)", StringComparison.Ordinal);
        var formatEnd = codeBehind.IndexOf("    static string FormatNetworkSpeedTestLatency", formatStart, StringComparison.Ordinal);

        Assert.Contains("Content=\"Mbps ⇄ MB/s\"", unitButton, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource IslandTab}\"", unitButton, StringComparison.Ordinal);
        Assert.Contains("Click=\"NetworkSpeedTestUnitButton_Click\"", unitButton, StringComparison.Ordinal);
        Assert.DoesNotContain("Background=", unitButton, StringComparison.Ordinal);
        Assert.DoesNotContain("Foreground=", unitButton, StringComparison.Ordinal);
        Assert.DoesNotContain("BorderBrush=", unitButton, StringComparison.Ordinal);
        Assert.DoesNotContain("{DynamicResource", unitButton, StringComparison.Ordinal);
        Assert.DoesNotContain("#", unitButton, StringComparison.Ordinal);
        Assert.True(phaseTextIndex >= 0 && phaseTextIndex < unitButtonIndex, "The unit toggle must sit by the gauge phase text.");
        Assert.True(enumStart >= 0 && enumEnd > enumStart, "The rate-unit enum is missing.");
        var unitEnum = codeBehind[enumStart..(enumEnd + 1)];
        Assert.Contains("enum NetworkSpeedTestDisplayUnit { Mbps, MegabytesPerSecond }", unitEnum, StringComparison.Ordinal);
        Assert.Contains("Mbps", unitEnum, StringComparison.Ordinal);
        Assert.Contains("MegabytesPerSecond", unitEnum, StringComparison.Ordinal);
        Assert.Contains("NetworkSpeedTestDisplayUnit networkSpeedTestDisplayUnit;", codeBehind, StringComparison.Ordinal);
        Assert.Contains("string NetworkSpeedTestRateUnit =>", codeBehind, StringComparison.Ordinal);
        Assert.Contains("? \"Mbps\" : \"MB/s\"", codeBehind, StringComparison.Ordinal);
        Assert.Contains("double GetNetworkSpeedTestDisplayRate(double rate) =>", codeBehind, StringComparison.Ordinal);
        Assert.Contains("? rate : rate / 8d;", codeBehind, StringComparison.Ordinal);
        Assert.True(handlerStart >= 0 && handlerEnd > handlerStart, "The rate-unit click handler is missing.");
        var unitHandler = codeBehind[handlerStart..handlerEnd];
        Assert.Contains("NetworkSpeedTestDisplayUnit.MegabytesPerSecond", unitHandler, StringComparison.Ordinal);
        Assert.Contains("NetworkSpeedTestDisplayUnit.Mbps", unitHandler, StringComparison.Ordinal);
        Assert.Contains("UpdateNetworkSpeedTestView(networkSpeedTest.Current);", unitHandler, StringComparison.Ordinal);
        Assert.Equal(1, unitHandler.Split("networkSpeedTest.", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("networkSpeedTest.StartAsync", unitHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("networkSpeedTest.Cancel", unitHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("FormatNetworkSpeedTestRate", unitHandler, StringComparison.Ordinal);
        var updateStart = codeBehind.IndexOf("void UpdateNetworkSpeedTestView(NetworkSpeedTestSnapshot snapshot)", StringComparison.Ordinal);
        var updateEnd = codeBehind.IndexOf("    string FormatNetworkSpeedTestRate", updateStart, StringComparison.Ordinal);
        var gaugeRateStart = codeBehind.IndexOf("var gaugeRate = snapshot.Phase switch", updateStart, StringComparison.Ordinal);
        var gaugeUnitUpdate = codeBehind.IndexOf("NetworkSpeedTestGaugeUnit.Text = NetworkSpeedTestRateUnit;", updateStart, StringComparison.Ordinal);
        Assert.True(updateStart >= 0 && updateEnd > updateStart && gaugeRateStart > updateStart, "The speed-test view update is missing.");
        Assert.True(gaugeUnitUpdate > updateStart && gaugeUnitUpdate < gaugeRateStart,
            "The gauge unit must refresh for every snapshot, including completed tests.");
        var gaugeUpdate = codeBehind[updateStart..updateEnd];
        Assert.Contains("NetworkSpeedTestGaugeValue.Text = GetNetworkSpeedTestDisplayRate(rate).ToString(\"0.00\", CultureInfo.InvariantCulture);", gaugeUpdate, StringComparison.Ordinal);
        Assert.Contains("AnimateNetworkSpeedTestGauge(rate);", gaugeUpdate, StringComparison.Ordinal);
        Assert.True(formatStart >= 0 && formatEnd > formatStart, "The rate formatter is missing.");
        var rateFormatter = codeBehind[formatStart..formatEnd];
        Assert.Contains("GetNetworkSpeedTestDisplayRate(value).ToString(\"0.00\", CultureInfo.InvariantCulture)", rateFormatter, StringComparison.Ordinal);
        Assert.Contains("{NetworkSpeedTestRateUnit}", rateFormatter, StringComparison.Ordinal);
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

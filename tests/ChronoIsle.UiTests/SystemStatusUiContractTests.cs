using System.IO;

namespace ChronoIsle.UiTests;

public sealed class SystemStatusUiContractTests
{
    [Fact]
    public void Island_ProvidesSystemStatusDashboardAndCollapsedNetworkState()
    {
        var (xaml, source) = IslandFiles();
        var workspace = FindWorkspace();
        var controls = File.ReadAllText(Path.Combine(
            workspace, "src", "ChronoIsle.App", "Resources", "Controls.xaml"));
        var telemetrySource = File.ReadAllText(Path.Combine(
            workspace, "src", "ChronoIsle.App", "Services", "SystemTelemetryService.cs"));

        Assert.Contains("x:Name=\"StatusDashboardTab\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TelemetryPanel\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NetworkStatusLight\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NetworkStatusGlyph\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"Icon.NetworkGlobe\"", controls, StringComparison.Ordinal);
        Assert.Contains("M12,2 A10,10 0 1 0 12,22 A10,10 0 1 0 12,2", controls, StringComparison.Ordinal);
        Assert.Contains("M12,2 V22", controls, StringComparison.Ordinal);
        Assert.Contains("Data=\"{StaticResource Icon.NetworkGlobe}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<Viewbox Width=\"14\" Height=\"14\" Stretch=\"Uniform\">", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"NetworkStatusHollowNode\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<Canvas Width=\"18\" Height=\"16\">", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TelemetryUploadSpeed\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TelemetryDownloadSpeed\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TelemetryCpuProgress\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TelemetryMemoryProgress\"", xaml, StringComparison.Ordinal);
        Assert.Contains("void ShowTelemetryDashboard()", source, StringComparison.Ordinal);
        Assert.Contains("UpdateTelemetryView(telemetry.Current);", source, StringComparison.Ordinal);
        Assert.Contains("NetworkHealth.Offline => \"网络中断\"", source, StringComparison.Ordinal);
        Assert.Contains("NetworkHealth.Unstable => \"网络波动大\"", source, StringComparison.Ordinal);
        Assert.Contains("NetworkHealth.Connected => \"网络连接正常\"", source, StringComparison.Ordinal);
        Assert.Contains("NetworkHealth.Unstable => \"Brush.Warning\"", source, StringComparison.Ordinal);
        Assert.Contains("NetworkHealth.Connected => \"Brush.Success\"", source, StringComparison.Ordinal);
        Assert.Contains("new HttpRequestMessage(HttpMethod.Head, LatencyProbeUri)", telemetrySource, StringComparison.Ordinal);
        Assert.Contains("HttpCompletionOption.ResponseHeadersRead", telemetrySource, StringComparison.Ordinal);
        Assert.DoesNotContain("new TcpClient()", telemetrySource, StringComparison.Ordinal);
        Assert.Contains("NetworkStatusGlyph.Stroke = statusBrush;", source, StringComparison.Ordinal);
        Assert.Contains(
            @"\Processor Information(_Total)\% Processor Utility",
            telemetrySource,
            StringComparison.Ordinal);
        Assert.Contains("PdhAddEnglishCounterW", telemetrySource, StringComparison.Ordinal);
        Assert.Contains("processorUtility.TryRead(out var utility)", telemetrySource, StringComparison.Ordinal);
        Assert.Contains("processorUtility.Dispose();", telemetrySource, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "<Ellipse x:Name=\"NetworkStatusLight\" Width=\"6\" Height=\"6\"",
            xaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ToastInbox_RunsInItsOwnWindowWithoutTakingOverTheIslandHeader()
    {
        var (xaml, source) = IslandFiles();
        var appSource = File.ReadAllText(Path.Combine(FindWorkspace(), "src", "ChronoIsle.App", "App.xaml.cs"));
        Assert.Contains("collection.AddSingleton<SystemToastInboxService>();", appSource, StringComparison.Ordinal);
        Assert.Contains("toastInbox.ToastReceived +=", appSource, StringComparison.Ordinal);
        Assert.Contains("ShowSystemToast(message, toastInbox)", appSource, StringComparison.Ordinal);
        Assert.Contains("notificationWindow.ShowMessage(message, inbox);", source, StringComparison.Ordinal);
        Assert.Contains("notificationWindow?.Close();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SystemToastHeader", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Island_DoesNotCreateVisualMouseTooltips()
    {
        var (xaml, source) = IslandFiles();

        Assert.DoesNotContain("ToolTip", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ToolTip", source, StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.Name=\"实时音轨，悬浮显示播放控制\"",
            xaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NotificationWindow_FollowsTheIslandAndRespectsTaskbarPlacement()
    {
        var (_, source) = IslandFiles();
        Assert.Contains("LocationChanged += (_, _) => PositionNotificationWindow();", source, StringComparison.Ordinal);
        Assert.Contains("placement == IslandPlacement.Taskbar", source, StringComparison.Ordinal);
        Assert.Contains("notificationWindow.PositionNextTo(anchor, area", source, StringComparison.Ordinal);
        var start = source.IndexOf("public void ShowSystemToast", StringComparison.Ordinal);
        var end = source.IndexOf("public void HideSystemToast", start, StringComparison.Ordinal);
        var display = source[start..end];
        Assert.DoesNotContain("Expand();", display, StringComparison.Ordinal);
        Assert.DoesNotContain("musicModeActive =", display, StringComparison.Ordinal);
        Assert.DoesNotContain("Width =", display, StringComparison.Ordinal);
    }

    [Fact]
    public void Theme_UsesSemanticEmeraldTokensWithoutAiBlue()
    {
        var workspace = FindWorkspace();
        var tokens = File.ReadAllText(Path.Combine(
            workspace, "src", "ChronoIsle.App", "Resources", "DesignTokens.xaml"));
        var controls = File.ReadAllText(Path.Combine(
            workspace, "src", "ChronoIsle.App", "Resources", "Controls.xaml"));
        var island = File.ReadAllText(Path.Combine(
            workspace, "src", "ChronoIsle.App", "Views", "LifeIslandWindow.xaml"));

        Assert.Contains("#39C98B", tokens, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#5B7CFA", tokens, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "<Setter Property=\"Foreground\" Value=\"{DynamicResource Brush.Island}\"/>",
            controls,
            StringComparison.Ordinal);
        var checkMark = System.Xml.Linq.XDocument.Parse(controls).Descendants()
            .Single(element => element.Attribute(
                System.Xml.Linq.XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value == "Mark");
        Assert.Equal("Path", checkMark.Name.LocalName);
        Assert.Equal("{DynamicResource Brush.Island}", checkMark.Attribute("Stroke")?.Value);
        Assert.Contains("Brush.AccentSoft", island, StringComparison.Ordinal);
        var source = File.ReadAllText(Path.Combine(
            workspace, "src", "ChronoIsle.App", "Views", "LifeIslandWindow.xaml.cs"));
        Assert.Contains("ConfigureGlowBorder", source, StringComparison.Ordinal);
        Assert.Contains("theme.ThemeChanged += Theme_Changed;", source, StringComparison.Ordinal);
        Assert.Contains("theme.ThemeChanged -= Theme_Changed;", source, StringComparison.Ordinal);
        Assert.Contains("void Theme_Changed(AppThemeMode _)", source, StringComparison.Ordinal);
    }

    static (string Xaml, string Source) IslandFiles()
    {
        var workspace = FindWorkspace();
        return (
            File.ReadAllText(Path.Combine(
                workspace, "src", "ChronoIsle.App", "Views", "LifeIslandWindow.xaml")),
            File.ReadAllText(Path.Combine(
                workspace, "src", "ChronoIsle.App", "Views", "LifeIslandWindow.xaml.cs")));
    }

    static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ChronoIsle.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("ChronoIsle.sln was not found from the UI test host.");
    }
}

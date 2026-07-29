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
    public void ToastInbox_ShowsAnIndependentWindowForFiveSeconds()
    {
        var (xaml, source) = IslandFiles();
        var workspace = FindWorkspace();
        var notificationXamlPath = Path.Combine(
            workspace, "src", "ChronoIsle.App", "Views", "IslandNotificationWindow.xaml");
        var notificationSourcePath = Path.Combine(
            workspace, "src", "ChronoIsle.App", "Views", "IslandNotificationWindow.xaml.cs");
        Assert.True(File.Exists(notificationXamlPath), "The independent notification window XAML is missing.");
        Assert.True(File.Exists(notificationSourcePath), "The independent notification window source is missing.");
        var notificationXaml = File.ReadAllText(notificationXamlPath);
        var notificationSource = File.ReadAllText(notificationSourcePath);
        var inboxSource = File.ReadAllText(Path.Combine(
            workspace, "src", "ChronoIsle.App", "Services", "SystemToastInboxService.cs"));

        Assert.DoesNotContain("x:Name=\"SystemToastHeader\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SystemToastHeader", source, StringComparison.Ordinal);
        Assert.Contains("x:Class=\"ChronoIsle.App.Views.IslandNotificationWindow\"", notificationXaml, StringComparison.Ordinal);
        Assert.Contains("ShowActivated=\"False\"", notificationXaml, StringComparison.Ordinal);
        Assert.Contains("ShowInTaskbar=\"False\"", notificationXaml, StringComparison.Ordinal);
        Assert.Contains("Topmost=\"True\"", notificationXaml, StringComparison.Ordinal);
        Assert.Contains("Width=\"340\"", notificationXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NotificationAppName\"", notificationXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NotificationTitle\"", notificationXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NotificationBody\"", notificationXaml, StringComparison.Ordinal);
        Assert.Contains(
            "readonly DispatcherTimer retractTimer = new() { Interval = TimeSpan.FromSeconds(5) };",
            notificationSource,
            StringComparison.Ordinal);
        Assert.Contains("public void ShowMessage(SystemToastMessage message)", notificationSource, StringComparison.Ordinal);
        Assert.Contains("retractTimer.Stop();", notificationSource, StringComparison.Ordinal);
        Assert.Contains("retractTimer.Start();", notificationSource, StringComparison.Ordinal);
        Assert.Contains("toastInbox.ToastReceived += message", source, StringComparison.Ordinal);
        Assert.Contains("notificationWindow.ShowMessage(message);", source, StringComparison.Ordinal);
        Assert.Contains("listener.NotificationChanged += Listener_NotificationChanged;", inboxSource, StringComparison.Ordinal);
        Assert.Contains("readonly SemaphoreSlim startGate = new(1, 1);", inboxSource, StringComparison.Ordinal);
        Assert.Contains("readonly object listenerGate = new();", inboxSource, StringComparison.Ordinal);
        Assert.Contains("await startGate.WaitAsync();", inboxSource, StringComparison.Ordinal);
        Assert.Contains("startGate.Release();", inboxSource, StringComparison.Ordinal);
        Assert.Contains("lock (listenerGate)", inboxSource, StringComparison.Ordinal);
        Assert.Contains("if (disposed) return;", inboxSource, StringComparison.Ordinal);
        Assert.Contains("args.ChangeKind != UserNotificationChangedKind.Added", inboxSource, StringComparison.Ordinal);
        Assert.Contains("sender.GetNotification(args.UserNotificationId)", inboxSource, StringComparison.Ordinal);
        Assert.Contains("listener.NotificationChanged -= Listener_NotificationChanged;", inboxSource, StringComparison.Ordinal);
        Assert.Contains("notification.Notification.Visual.Bindings", inboxSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ToastInbox_DoesNotChangeMusicOrTopDockState()
    {
        var (xaml, source) = IslandFiles();
        Assert.DoesNotContain("SystemToastHeader", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SystemToastHeader", source, StringComparison.Ordinal);
        Assert.DoesNotContain("toastPreviousHeaderHeight", source, StringComparison.Ordinal);
        Assert.Contains("UpdateCollapsedMediaView(collapsedMedia, currentPreferences);", source, StringComparison.Ordinal);
        var showToastStart = source.IndexOf("void ShowSystemToast", StringComparison.Ordinal);
        var positionStart = source.IndexOf("void PositionNotificationWindow", showToastStart, StringComparison.Ordinal);
        var showToast = source[showToastStart..positionStart];
        Assert.DoesNotContain("media", showToast, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("musicModeActive", showToast, StringComparison.Ordinal);
        Assert.Contains("notificationWindow.ShowMessage(message);", showToast, StringComparison.Ordinal);
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
    public void NotificationWindow_FollowsTheIslandAndFlipsAboveTheTaskbar()
    {
        var (_, source) = IslandFiles();
        var workspace = FindWorkspace();
        var notificationSourcePath = Path.Combine(
            workspace, "src", "ChronoIsle.App", "Views", "IslandNotificationWindow.xaml.cs");
        Assert.True(File.Exists(notificationSourcePath), "The independent notification window source is missing.");
        var notificationSource = File.ReadAllText(notificationSourcePath);

        Assert.Contains("LocationChanged += (_, _) => PositionNotificationWindow();", source, StringComparison.Ordinal);
        Assert.Contains("SizeChanged += (_, _) => PositionNotificationWindow();", source, StringComparison.Ordinal);
        Assert.Contains("notificationWindow.PositionNextTo(", source, StringComparison.Ordinal);
        Assert.Contains("placement == IslandPlacement.Taskbar", source, StringComparison.Ordinal);
        Assert.Contains("public void PositionNextTo(Rect anchor, Rect workArea, bool placeAbove)", notificationSource, StringComparison.Ordinal);
        Assert.Contains("anchor.Top - height - Gap", notificationSource, StringComparison.Ordinal);
        Assert.Contains("anchor.Bottom + Gap", notificationSource, StringComparison.Ordinal);
        Assert.DoesNotContain("TaskbarToastWidth", source, StringComparison.Ordinal);
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
        Assert.Contains(
            "x:Name=\"Mark\" Text=\"✓\" Foreground=\"{DynamicResource Brush.Island}\"",
            controls,
            StringComparison.Ordinal);
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

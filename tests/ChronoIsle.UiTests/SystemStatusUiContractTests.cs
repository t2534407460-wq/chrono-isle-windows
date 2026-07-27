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
    public void ToastInbox_ShowsHeaderForFiveSeconds()
    {
        var (xaml, source) = IslandFiles();
        var workspace = FindWorkspace();
        var inboxSource = File.ReadAllText(Path.Combine(
            workspace, "src", "ChronoIsle.App", "Services", "SystemToastInboxService.cs"));

        Assert.Contains("x:Name=\"SystemToastHeader\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SystemToastText\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "readonly DispatcherTimer toastRetractTimer = new() { Interval = TimeSpan.FromSeconds(5) };",
            source,
            StringComparison.Ordinal);
        Assert.Contains("toastInbox.ToastReceived += message", source, StringComparison.Ordinal);
        Assert.Contains("void HideSystemToast()", source, StringComparison.Ordinal);
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
    public void ToastInbox_RefoldsTopDockAfterTheBannerExpires()
    {
        var (_, source) = IslandFiles();
        var hideStart = source.IndexOf("void HideSystemToast()", StringComparison.Ordinal);
        var hideEnd = source.IndexOf("void ConfigureGlowBorder()", hideStart, StringComparison.Ordinal);
        var hideHandler = source[hideStart..hideEnd];

        Assert.Contains("Header.Height = CollapsedHeaderHeight();", hideHandler, StringComparison.Ordinal);
        Assert.Contains("double CollapsedHeaderHeight()", source, StringComparison.Ordinal);
        Assert.Contains("placement != IslandPlacement.Taskbar", source, StringComparison.Ordinal);
        Assert.Contains("placement == IslandPlacement.Top && !pointerHover && !expanded", hideHandler, StringComparison.Ordinal);
        Assert.Contains("SetTopDockFolded(true);", hideHandler, StringComparison.Ordinal);
        Assert.True(
            hideHandler.IndexOf("SystemToastHeader.Visibility = Visibility.Collapsed;", StringComparison.Ordinal) <
            hideHandler.IndexOf("SetTopDockFolded(true);", StringComparison.Ordinal));
    }

    [Fact]
    public void TaskbarToast_ExpandsHorizontallyWithoutChangingHeaderHeight()
    {
        var (_, source) = IslandFiles();
        var showStart = source.IndexOf("void ShowSystemToast(", StringComparison.Ordinal);
        var showEnd = source.IndexOf("void HideSystemToast()", showStart, StringComparison.Ordinal);
        var showHandler = source[showStart..showEnd];

        Assert.Contains("var taskbarToast = placement == IslandPlacement.Taskbar;", showHandler, StringComparison.Ordinal);
        Assert.Contains("Header.Height = taskbarToast", showHandler, StringComparison.Ordinal);
        Assert.Contains("? toastPreviousHeaderHeight", showHandler, StringComparison.Ordinal);
        Assert.Contains("SystemToastText.Orientation = taskbarToast", showHandler, StringComparison.Ordinal);
        Assert.Contains("double TaskbarToastWidth()", source, StringComparison.Ordinal);
        Assert.Contains("if (SystemToastHeader.Visibility == Visibility.Visible)", source, StringComparison.Ordinal);
        Assert.Contains(
            "return placement == IslandPlacement.Taskbar ? TaskbarToastWidth() : CollapsedWidth;",
            source,
            StringComparison.Ordinal);
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

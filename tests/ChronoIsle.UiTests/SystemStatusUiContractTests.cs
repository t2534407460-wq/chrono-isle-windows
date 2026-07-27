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
        Assert.DoesNotContain(
            "<Ellipse x:Name=\"NetworkStatusLight\" Width=\"6\" Height=\"6\"",
            xaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ToastInbox_ShowsHeaderForFiveSeconds()
    {
        var (xaml, source) = IslandFiles();

        Assert.Contains("x:Name=\"SystemToastHeader\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "readonly DispatcherTimer toastRetractTimer = new() { Interval = TimeSpan.FromSeconds(5) };",
            source,
            StringComparison.Ordinal);
        Assert.Contains("toastInbox.ToastReceived += message", source, StringComparison.Ordinal);
        Assert.Contains("void HideSystemToast()", source, StringComparison.Ordinal);
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

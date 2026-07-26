using System.IO;

namespace ChronoIsle.UiTests;

public sealed class SystemStatusUiContractTests
{
    [Fact]
    public void Island_ProvidesSystemStatusDashboardAndCollapsedNetworkState()
    {
        var (xaml, source) = IslandFiles();

        Assert.Contains("x:Name=\"StatusDashboardTab\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TelemetryPanel\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NetworkStatusLight\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TelemetryUploadSpeed\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TelemetryDownloadSpeed\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TelemetryCpuProgress\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TelemetryMemoryProgress\"", xaml, StringComparison.Ordinal);
        Assert.Contains("void ShowTelemetryDashboard()", source, StringComparison.Ordinal);
        Assert.Contains("UpdateTelemetryView(telemetry.Current);", source, StringComparison.Ordinal);
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
        var island = File.ReadAllText(Path.Combine(
            workspace, "src", "ChronoIsle.App", "Views", "LifeIslandWindow.xaml"));

        Assert.Contains("#39C98B", tokens, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#5B7CFA", tokens, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Brush.AccentSoft", island, StringComparison.Ordinal);
        Assert.Contains("ConfigureGlowBorder", File.ReadAllText(Path.Combine(
            workspace, "src", "ChronoIsle.App", "Views", "LifeIslandWindow.xaml.cs")), StringComparison.Ordinal);
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

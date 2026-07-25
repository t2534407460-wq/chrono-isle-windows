using System.IO;

namespace ChronoIsle.UiTests;

public sealed class TrayMenuContractTests
{
    [Fact]
    public void Tray_uses_a_custom_window_instead_of_a_native_context_menu()
    {
        var workspace = FindWorkspace();
        var service = File.ReadAllText(Path.Combine(workspace, "src", "ChronoIsle.App", "Services", "LifeTrayService.cs"));
        var xaml = File.ReadAllText(Path.Combine(workspace, "src", "ChronoIsle.App", "Views", "TrayMenuWindow.xaml"));

        Assert.DoesNotContain("ContextMenuStrip", service, StringComparison.Ordinal);
        Assert.Contains("TrayMenuWindow", service, StringComparison.Ordinal);
        Assert.Contains("Width=\"224\"", xaml, StringComparison.Ordinal);
        Assert.Contains("WindowStyle=\"None\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AllowsTransparency=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"#111111\"", xaml, StringComparison.Ordinal);
        Assert.Contains("BorderBrush=\"#3A3A3C\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Foreground=\"#F2F2F7\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Value=\"#2C2C2E\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("#161920", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("#262B38", xaml, StringComparison.Ordinal);
        foreach (var id in new[] { "TrayOpenButton", "TrayManageButton", "TrayNamingButton", "TrayNotificationsButton", "TrayDoNotDisturbButton", "TraySettingsButton", "TrayExitButton" })
            Assert.Contains($"AutomationProperties.AutomationId=\"{id}\"", xaml, StringComparison.Ordinal);
    }

    static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ChronoIsle.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("ChronoIsle.sln was not found from the UI test host.");
    }
}

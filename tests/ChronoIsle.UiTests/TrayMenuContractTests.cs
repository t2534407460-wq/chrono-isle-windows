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
        var codeBehind = File.ReadAllText(Path.Combine(workspace, "src", "ChronoIsle.App", "Views", "TrayMenuWindow.xaml.cs"));

        Assert.DoesNotContain("ContextMenuStrip", service, StringComparison.Ordinal);
        Assert.Contains("TrayMenuWindow", service, StringComparison.Ordinal);
        Assert.Contains("Width=\"200\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Height\" Value=\"34\"/>", xaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"FontSize\" Value=\"13\"/>", xaml, StringComparison.Ordinal);
        Assert.Contains("WindowStyle=\"None\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AllowsTransparency=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"{DynamicResource Brush.Island}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("BorderBrush=\"{DynamicResource Brush.Stroke}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Foreground=\"{DynamicResource Brush.TextPrimary}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Value=\"{DynamicResource Brush.Control}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("#161920", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("#262B38", xaml, StringComparison.Ordinal);
        foreach (var id in new[] { "TrayOpenButton", "TrayManageButton", "TrayNamingButton", "TrayNotificationsButton", "TrayDoNotDisturbButton", "TraySettingsButton", "TrayExitButton" })
            Assert.Contains($"AutomationProperties.AutomationId=\"{id}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("if (IsVisible && !IsActive) Close();", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Activate();", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Mouse.PreviewMouseDownOutsideCapturedElementEvent", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Mouse.Capture(this, CaptureMode.SubTree)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("if (IsMouseCaptureWithin || IsMouseCaptured) Mouse.Capture(null);", codeBehind, StringComparison.Ordinal);
        Assert.Contains("CaptureTrayHostAtCursor()", service, StringComparison.Ordinal);
        Assert.Contains("ShowAtCursor(trayHost)", service, StringComparison.Ordinal);
        Assert.Contains("TrayHostCloseDelay", codeBehind, StringComparison.Ordinal);
        Assert.Contains("trayHostTimer.Start()", codeBehind, StringComparison.Ordinal);
        Assert.Contains("!IsWindowVisible(trayHost)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("DwmGetWindowAttribute(trayHost, 14", codeBehind, StringComparison.Ordinal);
    }

    static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ChronoIsle.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("ChronoIsle.sln was not found from the UI test host.");
    }
}

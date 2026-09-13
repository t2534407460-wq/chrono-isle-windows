using ChronoIsle.App.Services;
using Microsoft.Win32;

namespace ChronoIsle.Tests;

public sealed class NativeToastBannerTests : IDisposable
{
    readonly string root = @"Software\ChronoIsle.Tests\" + Guid.NewGuid().ToString("N");
    readonly string directory = Path.Combine(Path.GetTempPath(), "ChronoIsleBannerTests", Guid.NewGuid().ToString("N"));
    string Journal => Path.Combine(directory, "backup.json");

    [Fact]
    public void StopRestoresExistingAndMissingValuesWithoutChangingOtherNotificationSettings()
    {
        using var absent = Registry.CurrentUser.CreateSubKey(root + @"\absent");
        using var enabled = Registry.CurrentUser.CreateSubKey(root + @"\enabled");
        using var disabled = Registry.CurrentUser.CreateSubKey(root + @"\disabled");
        enabled.SetValue("ShowBanner", 1);
        disabled.SetValue("ShowBanner", 0);
        enabled.SetValue("ShowInActionCenter", 0);
        using var service = new NativeToastBannerService(root, Journal);
        Assert.True(service.Start());
        Assert.Equal(0, absent.GetValue("ShowBanner"));
        Assert.Equal(0, enabled.GetValue("ShowBanner"));
        Assert.Equal(0, enabled.GetValue("ShowInActionCenter"));
        Assert.True(File.Exists(Journal));
        Assert.True(service.Restore());
        Assert.Null(absent.GetValue("ShowBanner"));
        Assert.Equal(1, enabled.GetValue("ShowBanner"));
        Assert.Equal(0, disabled.GetValue("ShowBanner"));
        Assert.False(File.Exists(Journal));
        service.SuppressApp("enabled");
        Assert.Equal(1, enabled.GetValue("ShowBanner"));
    }

    [Fact]
    public void NextInstanceRecoversAnInterruptedSessionAndRespectsUserEdits()
    {
        using var key = Registry.CurrentUser.CreateSubKey(root + @"\chat");
        key.SetValue("ShowBanner", 1);
        var interrupted = new NativeToastBannerService(root, Journal);
        interrupted.Start();
        using var next = new NativeToastBannerService(root, Journal);
        Assert.True(next.Restore());
        Assert.Equal(1, key.GetValue("ShowBanner"));
        next.Start();
        key.SetValue("ShowBanner", 1); // User re-enabled banners during takeover.
        next.RefreshRegisteredApps();
        Assert.Equal(1, key.GetValue("ShowBanner"));
        next.Restore();
        Assert.Equal(1, key.GetValue("ShowBanner"));
    }

    [Fact]
    public void FailedBackupDoesNotDisableTheOriginalBanner()
    {
        using var key = Registry.CurrentUser.CreateSubKey(root + @"\chat");
        key.SetValue("ShowBanner", 1);
        Directory.CreateDirectory(directory);
        var fileAsDirectory = Path.Combine(directory, "file");
        File.WriteAllText(fileAsDirectory, "occupied");
        using var service = new NativeToastBannerService(root, Path.Combine(fileAsDirectory, "backup.json"));
        Assert.False(service.Start());
        Assert.Equal(1, key.GetValue("ShowBanner"));
    }

    [Fact]
    public void NewlyRegisteredAppsAreSuppressedAndRestored()
    {
        using var service = new NativeToastBannerService(root, Journal);
        service.Start();
        using var key = Registry.CurrentUser.CreateSubKey(root + @"\new-chat");
        service.RefreshRegisteredApps();
        Assert.Equal(0, key.GetValue("ShowBanner"));
        service.Restore();
        Assert.Null(key.GetValue("ShowBanner"));
    }

    [Fact]
    public void ClassicDesktopAppIdsSuppressTheirLeafWithoutChangingFolderKeys()
    {
        const string folder = "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}";
        const string appId = folder + @"\WindowsPowerShell\v1.0\powershell.exe";
        using var key = Registry.CurrentUser.CreateSubKey(root + @"\" + appId);
        using var parent = Registry.CurrentUser.OpenSubKey(root + @"\" + folder)!;
        using var service = new NativeToastBannerService(root, Journal);
        Assert.True(NativeToastBannerService.IsValidAppId(appId));
        service.Start();
        Assert.Equal(0, key.GetValue("ShowBanner"));
        Assert.Null(parent.GetValue("ShowBanner"));
        service.Restore();
        Assert.Null(key.GetValue("ShowBanner"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..\\unrelated")]
    [InlineData("app/name")]
    [InlineData("app\"arguments")]
    [InlineData("app\nname")]
    public void AppIdsCannotEscapeTheirRegistryOrLaunchArgument(string id)
    {
        Assert.False(NativeToastBannerService.IsValidAppId(id));
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

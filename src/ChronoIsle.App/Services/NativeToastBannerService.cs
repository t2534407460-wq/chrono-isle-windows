using Microsoft.Win32;
using System.Text.Json;

namespace ChronoIsle.App.Services;

// Windows has no public API to replace another app's toast surface. Change only
// ShowBanner, leaving delivery, sounds, DND and the notification center intact.
public sealed class NativeToastBannerService : IDisposable
{
    const string SettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";
    readonly string registryPath;
    readonly string journalPath;
    readonly object gate = new();
    Dictionary<string, int?> originals = new(StringComparer.OrdinalIgnoreCase);
    bool active;

    public NativeToastBannerService() : this(SettingsKey, Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ChronoIsle", "notification-banner-backup.json")) { }

    internal NativeToastBannerService(string registryPath, string journalPath)
    {
        this.registryPath = registryPath;
        this.journalPath = journalPath;
    }

    public bool IsActive { get { lock (gate) return active; } }

    public bool Start()
    {
        lock (gate)
        {
            if (active) return true;
            if (!Restore()) return false;
            active = true;
            RefreshRegisteredApps();
            return active;
        }
    }

    public void RefreshRegisteredApps()
    {
        lock (gate)
        {
            if (!active) return;
            try
            {
                using var root = Registry.CurrentUser.OpenSubKey(registryPath);
                if (root is null) return;
                foreach (var appId in root.GetSubKeyNames())
                {
                    if (Guid.TryParse(appId, out _)) SuppressDesktopApps(root, appId);
                    else SuppressApp(appId);
                }
            }
            catch (Exception exception)
            {
                ToastInboxDiagnostics.Write("banner-scan-failed", 0, ToastInboxDiagnostics.Failure(exception));
                Restore();
            }
        }
    }

    void SuppressDesktopApps(RegistryKey root, string appId)
    {
        // Classic shortcuts use {KnownFolderGuid}\path\app.exe as their AUMID.
        // Those IDs occupy nested registry keys; the folder keys are not apps.
        using var key = root.OpenSubKey(appId);
        if (key is null) return;
        var children = key.GetSubKeyNames();
        if (children.Length == 0 || appId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            SuppressApp(appId);
        else
            foreach (var child in children) SuppressDesktopApps(root, appId + @"\" + child);
    }

    public void SuppressApp(string appId)
    {
        lock (gate)
        {
            if (!active || !IsValidAppId(appId) || originals.ContainsKey(appId)) return;
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey($@"{registryPath}\{appId}");
                var value = key.GetValue("ShowBanner");
                if (value is not null && value is not int) return;
                if (value is int number && number == 0) return;
                originals.Add(appId, (int?)value);
                SaveJournal(); // Durable backup must precede the registry write.
                key.SetValue("ShowBanner", 0, RegistryValueKind.DWord);
                ToastInboxDiagnostics.Write("banner-suppressed", 0, $"appId={appId}; readback={key.GetValue("ShowBanner", "absent")}");
            }
            catch (Exception exception)
            {
                ToastInboxDiagnostics.Write("banner-suppress-failed", 0, ToastInboxDiagnostics.Failure(exception));
                Restore();
            }
        }
    }

    public bool Restore()
    {
        lock (gate)
        {
            active = false;
            try
            {
                if (File.Exists(journalPath))
                    originals = new(JsonSerializer.Deserialize<Dictionary<string, int?>>(
                        File.ReadAllText(journalPath)) ?? [], StringComparer.OrdinalIgnoreCase);
                foreach (var (appId, original) in originals.ToArray())
                {
                    if (!IsValidAppId(appId)) throw new InvalidDataException("Invalid banner backup app id.");
                    using var key = Registry.CurrentUser.OpenSubKey($@"{registryPath}\{appId}", writable: true);
                    // Respect settings edited by the user while ChronoIsle was running.
                    if (key?.GetValue("ShowBanner") is int current && current == 0)
                    {
                        if (original is int number) key.SetValue("ShowBanner", number, RegistryValueKind.DWord);
                        else key.DeleteValue("ShowBanner", throwOnMissingValue: false);
                    }
                    originals.Remove(appId);
                }
                if (File.Exists(journalPath)) File.Delete(journalPath);
                return true;
            }
            catch (Exception exception)
            {
                ToastInboxDiagnostics.Write("banner-restore-failed", 0, ToastInboxDiagnostics.Failure(exception));
                return false;
            }
        }
    }

    void SaveJournal()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
        var temporary = journalPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(originals));
        File.Move(temporary, journalPath, overwrite: true);
    }

    internal static bool IsValidAppId(string? value) => !string.IsNullOrWhiteSpace(value) &&
        value.IndexOfAny(['/', '"', '\0']) < 0 && !value.Any(char.IsControl) &&
        value.Split('\\').All(part => part.Length > 0 && part is not "." and not "..");

    public void Dispose() => Restore();
}

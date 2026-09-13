using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;
using System.Windows.Media.Imaging;
using Windows.Storage.Streams;
using System.Windows.Threading;

namespace ChronoIsle.App.Services;

public enum ToastInboxAccess
{
    Unknown,
    Allowed,
    Denied,
    Unavailable
}

public sealed record SystemToastMessage(
    uint Id,
    string AppName,
    string Title,
    string Body,
    string AppUserModelId,
    DateTimeOffset CreatedAt,
    BitmapSource? Icon = null);

internal static class ToastInboxDiagnostics
{
    static readonly object gate = new();
    static readonly string path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ChronoIsle",
        "life-toast-inbox-diagnostics.log");

    internal static void Write(string stage, uint notificationId, string details)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            lock (gate)
                File.AppendAllText(path, Format(DateTimeOffset.Now, stage, notificationId, details));
        }
        catch { }
    }

    internal static string Source(SystemToastMessage message) =>
        $"app={Clean(message.AppName)}; appId={Clean(message.AppUserModelId)}";

    internal static string Failure(Exception exception) =>
        $"exception={exception.GetType().Name}; hresult=0x{exception.HResult:X8}";

    static string Format(DateTimeOffset timestamp, string stage, uint notificationId, string details) =>
        $"{timestamp:O} | {stage} | id={notificationId} | {Clean(details)}{Environment.NewLine}";

    static string Clean(string? value) => string.IsNullOrWhiteSpace(value)
        ? "(none)"
        : value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}

internal static class ToastTextComposer
{
    public static (string Title, string Body) Compose(IEnumerable<string?> values)
    {
        var lines = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .ToArray();
        return lines.Length switch
        {
            0 => ("新通知", string.Empty),
            1 => (lines[0], string.Empty),
            _ => (lines[0], string.Join(" · ", lines.Skip(1)))
        };
    }

    public static (string Title, string Body) Compose(
        IEnumerable<string?>? preferredValues,
        IEnumerable<IEnumerable<string?>> fallbackBindings)
    {
        var preferred = preferredValues?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray() ?? [];
        return preferred.Length > 0
            ? Compose(preferred)
            : Compose(fallbackBindings.SelectMany(values => values));
    }
}

public sealed class SystemToastInboxService : IDisposable
{
    readonly DispatcherTimer timer;
    readonly NativeToastBannerService banners;
    readonly HashSet<uint> knownIds = [];
    readonly object stateGate = new();
    readonly SemaphoreSlim operationGate = new(1, 1);
    UserNotificationListener? listener;
    bool disposed;
    bool enabled;
    bool initialized;
    int generation;

    public SystemToastInboxService() : this(new NativeToastBannerService()) { }

    public SystemToastInboxService(NativeToastBannerService banners)
    {
        this.banners = banners;
        // Keep the WinRT listener on its originating dispatcher. On Windows 11
        // desktop apps, NotificationChanged registration can break the COM proxy.
        timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) => _ = PollAsync();
    }

    public event Action<SystemToastMessage>? ToastReceived;
    public event Action<ToastInboxAccess>? AccessChanged;
    public ToastInboxAccess Access { get; private set; } = ToastInboxAccess.Unknown;
    public bool IsRunning { get { lock (stateGate) return !disposed && enabled && initialized && Access == ToastInboxAccess.Allowed; } }
    public bool ReplacesBanners => IsRunning && banners.IsActive;

    // Called on the WPF dispatcher: Windows requires the consent request on the UI thread.
    public async Task StartAsync()
    {
        int version;
        lock (stateGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (IsRunning) return;
            enabled = true;
            version = generation;
        }
        await operationGate.WaitAsync();
        try
        {
            if (!IsCurrent(version) || IsRunning) return;
            listener ??= UserNotificationListener.Current;
            var status = await listener.RequestAccessAsync();
            if (!IsCurrent(version)) return;
            ToastInboxDiagnostics.Write("access-request", 0, $"status={status}");
            if (status != UserNotificationListenerAccessStatus.Allowed)
            {
                banners.Restore();
                SetAccess(status == UserNotificationListenerAccessStatus.Denied ? ToastInboxAccess.Denied : ToastInboxAccess.Unknown);
                return;
            }

            // Start a fresh session without replaying the existing notification center.
            var baseline = await listener.GetNotificationsAsync(NotificationKinds.Toast);
            lock (stateGate)
            {
                if (!IsCurrent(version)) return;
                knownIds.Clear();
                foreach (var notification in baseline) knownIds.Add(notification.Id);
                initialized = true;
            }
            ToastInboxDiagnostics.Write("poll-baseline", 0, $"messages={baseline.Count}");
            lock (stateGate)
            {
                if (!IsCurrent(version)) return;
                banners.Start();
                SetAccess(ToastInboxAccess.Allowed);
                timer.Start();
                ToastInboxDiagnostics.Write("listener-started", 0, "mode=dispatcher-poll; interval-ms=500");
            }
        }
        catch (Exception exception)
        {
            if (!IsCurrent(version)) return;
            ToastInboxDiagnostics.Write("start-failed", 0, ToastInboxDiagnostics.Failure(exception));
            Stop();
            SetAccess(ToastInboxAccess.Unavailable);
        }
        finally { operationGate.Release(); }
    }

    async Task PollAsync()
    {
        if (!IsRunning || !await operationGate.WaitAsync(0)) return;
        var version = generation;
        try
        {
            if (!IsCurrent(version)) return;
            if (listener!.GetAccessStatus() != UserNotificationListenerAccessStatus.Allowed)
            {
                Stop();
                SetAccess(ToastInboxAccess.Denied);
                return;
            }
            var notifications = await listener.GetNotificationsAsync(NotificationKinds.Toast);
            var messages = notifications.OrderBy(item => item.CreationTime)
                .Select(ToMessage).Where(item => item is not null).Cast<SystemToastMessage>().ToArray();
            lock (stateGate)
            {
                if (!IsCurrent(version)) return;
                banners.RefreshRegisteredApps();
                foreach (var message in messages)
                {
                    if (!knownIds.Add(message.Id)) continue;
                    banners.SuppressApp(message.AppUserModelId);
                    ToastInboxDiagnostics.Write("toast-published", message.Id, ToastInboxDiagnostics.Source(message));
                    ToastReceived?.Invoke(message);
                }
                if (knownIds.Count > 512)
                    knownIds.IntersectWith(notifications.Select(item => item.Id));
            }
        }
        catch (Exception exception)
        {
            if (!IsCurrent(version)) return;
            ToastInboxDiagnostics.Write("poll-failed", 0, ToastInboxDiagnostics.Failure(exception));
            Stop();
            SetAccess(ToastInboxAccess.Unavailable);
        }
        finally { operationGate.Release(); }
    }

    bool IsCurrent(int version)
    {
        lock (stateGate) return !disposed && enabled && generation == version;
    }

    public async Task<BitmapSource?> LoadIconAsync(uint id)
    {
        try
        {
            if (!IsRunning || listener?.GetAccessStatus() != UserNotificationListenerAccessStatus.Allowed) return null;
            var logo = listener.GetNotification(id)?.AppInfo.DisplayInfo.GetLogo(new Windows.Foundation.Size(48, 48));
            if (logo is null) return null;
            using var stream = await logo.OpenReadAsync();
            if (stream.Size > 2 * 1024 * 1024) return null;
            using var reader = new DataReader(stream);
            await reader.LoadAsync((uint)stream.Size);
            var bytes = new byte[(int)stream.Size];
            reader.ReadBytes(bytes);
            using var memory = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = memory;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch { return null; }
    }

    public bool Dismiss(uint id)
    {
        try
        {
            if (!IsRunning || listener?.GetAccessStatus() != UserNotificationListenerAccessStatus.Allowed) return false;
            listener.RemoveNotification(id);
            return true;
        }
        catch (Exception exception)
        {
            ToastInboxDiagnostics.Write("dismiss-failed", id, ToastInboxDiagnostics.Failure(exception));
            return false;
        }
    }

    static SystemToastMessage? ToMessage(UserNotification notification)
    {
        try
        {
            var visual = notification.Notification.Visual;
            var binding = visual.GetBinding(KnownNotificationBindings.ToastGeneric);
            var text = ToastTextComposer.Compose(binding?.GetTextElements().Select(item => item.Text),
                visual.Bindings.Select(item => item.GetTextElements().Select(element => element.Text)));
            var appName = notification.AppInfo.DisplayInfo.DisplayName;
            return new SystemToastMessage(notification.Id,
                string.IsNullOrWhiteSpace(appName) ? "Windows" : appName.Trim(),
                text.Title, text.Body, notification.AppInfo.AppUserModelId ?? string.Empty, notification.CreationTime);
        }
        catch { return null; }
    }

    void SetAccess(ToastInboxAccess value)
    {
        Access = value;
        ToastInboxDiagnostics.Write("access-changed", 0, $"value={value}");
        AccessChanged?.Invoke(value);
    }

    public void Stop()
    {
        lock (stateGate)
        {
            if (disposed) return;
            enabled = false;
            generation++;
            initialized = false;
            knownIds.Clear();
            timer.Stop();
            banners.Restore();
            SetAccess(ToastInboxAccess.Unknown);
        }
    }

    public void Dispose()
    {
        lock (stateGate)
        {
            if (disposed) return;
            Stop();
            disposed = true;

        }
    }
}

using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

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
    DateTimeOffset CreatedAt);

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
    readonly System.Threading.Timer timer;
    readonly HashSet<uint> knownIds = [];
    readonly object knownIdsGate = new();
    readonly object listenerGate = new();
    readonly SemaphoreSlim startGate = new(1, 1);
    UserNotificationListener? listener;
    bool initialized;
    bool listening;
    bool disposed;
    int polling;

    public SystemToastInboxService() =>
        timer = new System.Threading.Timer(_ => _ = PollAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    public event Action<SystemToastMessage>? ToastReceived;
    public event Action<ToastInboxAccess>? AccessChanged;
    public ToastInboxAccess Access { get; private set; } = ToastInboxAccess.Unknown;

    public async Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await startGate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            listener ??= UserNotificationListener.Current;
            var status = await listener.RequestAccessAsync();
            SetAccess(status == UserNotificationListenerAccessStatus.Allowed
                ? ToastInboxAccess.Allowed
                : ToastInboxAccess.Denied);
            if (Access != ToastInboxAccess.Allowed) return;
            if (!listening)
            {
                await PollAsync();
                lock (listenerGate)
                {
                    if (disposed) return;
                    if (!listening)
                    {
                        listener.NotificationChanged += Listener_NotificationChanged;
                        listening = true;
                    }
                }
                await PollAsync();
            }
            lock (listenerGate)
            {
                if (disposed) return;
                timer.Change(TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(2.5));
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Toast inbox unavailable: {exception.Message}");
            SetAccess(ToastInboxAccess.Unavailable);
        }
        finally
        {
            startGate.Release();
        }
    }

    async Task PollAsync()
    {
        if (disposed || listener is null || Access != ToastInboxAccess.Allowed ||
            Interlocked.Exchange(ref polling, 1) != 0)
            return;
        try
        {
            var notifications = await listener.GetNotificationsAsync(NotificationKinds.Toast);
            var messages = notifications
                .OrderBy(item => item.CreationTime)
                .Select(ToMessage)
                .Where(item => item is not null)
                .Cast<SystemToastMessage>()
                .ToArray();

            if (!initialized)
            {
                lock (knownIdsGate)
                    foreach (var message in messages) knownIds.Add(message.Id);
                initialized = true;
                return;
            }

            foreach (var message in messages)
            {
                if (!TryRemember(message.Id)) continue;
                ToastReceived?.Invoke(message);
            }

            lock (knownIdsGate)
            {
                if (knownIds.Count > 512)
                {
                    var activeIds = messages.Select(item => item.Id).ToHashSet();
                    knownIds.IntersectWith(activeIds);
                }
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Toast inbox poll failed: {exception.Message}");
        }
        finally
        {
            Volatile.Write(ref polling, 0);
        }
    }

    void Listener_NotificationChanged(
        UserNotificationListener sender,
        UserNotificationChangedEventArgs args)
    {
        if (disposed || args.ChangeKind != UserNotificationChangedKind.Added) return;
        try
        {
            var notification = sender.GetNotification(args.UserNotificationId);
            if (notification is null) return;
            var message = ToMessage(notification);
            if (message is null || !TryRemember(message.Id)) return;
            ToastReceived?.Invoke(message);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Toast inbox event failed: {exception.Message}");
        }
    }

    bool TryRemember(uint id)
    {
        lock (knownIdsGate) return knownIds.Add(id);
    }

    static SystemToastMessage? ToMessage(UserNotification notification)
    {
        try
        {
            var visual = notification.Notification.Visual;
            var binding = visual.GetBinding(KnownNotificationBindings.ToastGeneric);
            var text = ToastTextComposer.Compose(
                binding?.GetTextElements().Select(item => item.Text),
                notification.Notification.Visual.Bindings.Select(item =>
                    item.GetTextElements().Select(element => element.Text)));
            var appName = notification.AppInfo.DisplayInfo.DisplayName;
            var appId = notification.AppInfo.AppUserModelId;
            return new SystemToastMessage(
                notification.Id,
                string.IsNullOrWhiteSpace(appName) ? "Windows" : appName.Trim(),
                text.Title,
                text.Body,
                appId ?? string.Empty,
                notification.CreationTime);
        }
        catch { return null; }
    }

    void SetAccess(ToastInboxAccess value)
    {
        if (Access == value) return;
        Access = value;
        AccessChanged?.Invoke(value);
    }

    public void Dispose()
    {
        lock (listenerGate)
        {
            if (disposed) return;
            disposed = true;
            if (listener is not null && listening)
                listener.NotificationChanged -= Listener_NotificationChanged;
            listening = false;
        }
        timer.Dispose();
    }
}

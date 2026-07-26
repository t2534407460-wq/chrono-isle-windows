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
}

public sealed class SystemToastInboxService : IDisposable
{
    readonly System.Threading.Timer timer;
    readonly HashSet<uint> knownIds = [];
    UserNotificationListener? listener;
    bool initialized;
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
        try
        {
            listener ??= UserNotificationListener.Current;
            var status = await listener.RequestAccessAsync();
            SetAccess(status == UserNotificationListenerAccessStatus.Allowed
                ? ToastInboxAccess.Allowed
                : ToastInboxAccess.Denied);
            if (Access != ToastInboxAccess.Allowed) return;
            await PollAsync();
            timer.Change(TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(2.5));
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Toast inbox unavailable: {exception.Message}");
            SetAccess(ToastInboxAccess.Unavailable);
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
                foreach (var message in messages) knownIds.Add(message.Id);
                initialized = true;
                return;
            }

            foreach (var message in messages)
            {
                if (!knownIds.Add(message.Id)) continue;
                ToastReceived?.Invoke(message);
            }

            if (knownIds.Count > 512)
            {
                var activeIds = messages.Select(item => item.Id).ToHashSet();
                knownIds.IntersectWith(activeIds);
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

    static SystemToastMessage? ToMessage(UserNotification notification)
    {
        try
        {
            var binding = notification.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
            var text = ToastTextComposer.Compose(binding?.GetTextElements().Select(item => item.Text) ?? []);
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
        if (disposed) return;
        disposed = true;
        timer.Dispose();
    }
}

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

internal static class ToastInboxThreading
{
    internal static Task RunAsync(Action action)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action();
                completion.SetResult(true);
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return completion.Task;
    }
}

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
    const int NotificationReadRetryCount = 3;
    static readonly TimeSpan NotificationReadRetryDelay = TimeSpan.FromMilliseconds(75);
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
            ToastInboxDiagnostics.Write("access-request", 0, $"status={status}");
            SetAccess(status == UserNotificationListenerAccessStatus.Allowed
                ? ToastInboxAccess.Allowed
                : ToastInboxAccess.Denied);
            if (Access != ToastInboxAccess.Allowed) return;
            if (!listening)
            {
                await PollAsync();
                var currentListener = listener!;
                await ToastInboxThreading.RunAsync(() =>
                {
                    lock (listenerGate)
                    {
                        if (disposed || listening) return;
                        ToastInboxDiagnostics.Write(
                            "listener-subscribe-attempt",
                            0,
                            $"notification-changed=true; apartment={Thread.CurrentThread.GetApartmentState()}");
                        currentListener.NotificationChanged += Listener_NotificationChanged;
                        listening = true;
                        ToastInboxDiagnostics.Write("listener-subscribed", 0, "notification-changed=true");
                    }
                });
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
            ToastInboxDiagnostics.Write("start-failed", 0, ToastInboxDiagnostics.Failure(exception));
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
                ToastInboxDiagnostics.Write("poll-baseline", 0, $"messages={messages.Length}");
                return;
            }

            foreach (var message in messages)
            {
                if (!TryRemember(message.Id)) continue;
                ToastInboxDiagnostics.Write("poll-published", message.Id, ToastInboxDiagnostics.Source(message));
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
            ToastInboxDiagnostics.Write("poll-failed", 0, ToastInboxDiagnostics.Failure(exception));
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
        if (disposed) return;
        ToastInboxDiagnostics.Write("listener-event", args.UserNotificationId, $"kind={args.ChangeKind}");
        if (args.ChangeKind != UserNotificationChangedKind.Added) return;
        _ = PublishChangedNotificationAsync(sender, args.UserNotificationId);
    }

    async Task PublishChangedNotificationAsync(UserNotificationListener sender, uint notificationId)
    {
        try
        {
            for (var attempt = 0; attempt < NotificationReadRetryCount; attempt++)
            {
                var notification = sender.GetNotification(notificationId);
                var message = notification is null ? null : ToMessage(notification);
                if (message is not null)
                {
                    if (!TryRemember(message.Id))
                    {
                        ToastInboxDiagnostics.Write("event-known", message.Id, ToastInboxDiagnostics.Source(message));
                        return;
                    }
                    ToastInboxDiagnostics.Write("event-published", message.Id, ToastInboxDiagnostics.Source(message));
                    ToastReceived?.Invoke(message);
                    return;
                }

                ToastInboxDiagnostics.Write(
                    notification is null ? "event-not-found" : "event-unreadable",
                    notificationId,
                    $"attempt={attempt + 1}");
                if (attempt < NotificationReadRetryCount - 1)
                    await Task.Delay(NotificationReadRetryDelay);
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Toast inbox event failed: {exception.Message}");
            ToastInboxDiagnostics.Write("event-failed", notificationId, ToastInboxDiagnostics.Failure(exception));
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
        ToastInboxDiagnostics.Write("access-changed", 0, $"value={value}");
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

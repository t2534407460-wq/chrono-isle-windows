using System.Globalization;
using OpenIsland.App.Services.Persistence;
using OpenIsland.App.Services.Productivity;
using OpenIsland.App.Services.Scheduling;

namespace OpenIsland.App.Services;

public enum ReminderSchedulerStatus
{
    Stopped,
    Running,
    Degraded
}

public sealed record ReminderSchedulerHealth(
    ReminderSchedulerStatus Status,
    DateTimeOffset? LastSuccessfulTickAt,
    DateTimeOffset? LastFailedTickAt,
    int ConsecutiveFailures,
    long SkippedOverlappingTicks,
    bool IsPolling,
    string? LastErrorMessage);

public sealed class ReminderService : IDisposable
{
    readonly LifeDataService data;
    readonly LifePreferencesService preferences;
    readonly WindowsNotificationService notifications;
    readonly System.Threading.Timer timer;
    readonly SemaphoreSlim pollGate = new(1, 1);
    readonly object healthLock = new();
    ReminderSchedulerHealth health = new(ReminderSchedulerStatus.Stopped, null, null, 0, 0, false, null);
    int started;
    int disposed;
    readonly ReminderPolicyService policy;
    readonly ReminderDeliveryStore deliveryStore;
    readonly ReminderDueDetector dueDetector;
    readonly NotificationDispatcher notificationDispatcher;
    readonly Dictionary<string, AgendaItem> occurrenceItems = new(StringComparer.Ordinal);
    readonly object occurrenceItemsLock = new();
    readonly FocusService? focus;

    public event EventHandler<AgendaItem>? ReminderDue;
    public event EventHandler<ReminderSchedulerHealth>? HealthChanged;
    public event EventHandler<NotificationDispatcherHealth>? NotificationHealthChanged;
    public event EventHandler<DeferredNotificationSummary>? DeferredSummaryReleased;

    public ReminderSchedulerHealth Health
    {
        get
        {
            lock (healthLock) return health;
        }
    }

    public NotificationDispatcherHealth NotificationHealth => notificationDispatcher.Health;

    public ReminderService(LifeDataService data, LifePreferencesService preferences, WindowsNotificationService notifications, FocusService? focus = null)
    {
        this.data = data;
        this.preferences = preferences;
        this.notifications = notifications;
        this.focus = focus;
        var runtime = LifeDataStoreRuntimeRegistry.GetOrCreate(data.DatabasePath);
        deliveryStore = new ReminderDeliveryStore(runtime.WriteQueue);
        dueDetector = new ReminderDueDetector(deliveryStore);
        policy = new ReminderPolicyService(deliveryStore);
        notificationDispatcher = new NotificationDispatcher(
            new NotificationOutboxStore(runtime.WriteQueue),
            new ReminderNotificationTransport(DeliverOutboxNotificationAsync));
        notificationDispatcher.HealthChanged += (_, value) =>
        {
            try { NotificationHealthChanged?.Invoke(this, value); }
            catch { /* A UI observer must not terminate notification delivery. */ }
        };
        timer = new System.Threading.Timer(_ => _ = PollNowAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Interlocked.Exchange(ref started, 1) != 0) return;

        UpdateHealth(current => current with { Status = ReminderSchedulerStatus.Running });
        try
        {
            RefreshSchedule();
        }
        catch (Exception exception)
        {
            RecordFailure(exception);
        }
        timer.Change(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));
    }

    public void RefreshSchedule()
    {
        foreach (var item in data.ReminderItems()) RegisterUpcoming(item);
    }

    public void Schedule(AgendaItem item)
    {
        RegisterUpcoming(item);
    }

    public void Delete(AgendaItem item) => Delete([item]);

    public int Delete(IEnumerable<AgendaItem> items)
    {
        var targets = items.GroupBy(item => (item.Kind, item.Id)).Select(group => group.First()).ToList();
        foreach (var item in targets) notifications.Remove(item);
        var deleted = data.DeleteAgendaItems(targets);
        RefreshSchedule();
        return deleted;
    }

    public void Cancel(AgendaItem item) => notifications.Remove(item);

    public bool IsDoNotDisturbEnabled => preferences.Load().DoNotDisturbEnabled;
    public bool IsFullScreenSilentEnabled => preferences.Load().FullScreenSilentEnabled;
    public void SetDoNotDisturb(bool enabled)
    {
        var current = preferences.Load(); if (current.DoNotDisturbEnabled == enabled) return;
        preferences.Save(current with { DoNotDisturbEnabled = enabled }); RefreshSchedule();
        if (!enabled) DeferredSummaryReleased?.Invoke(this, policy.ReleaseDeferredSummary(DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N")));
    }
    public void SetFullScreenSilent(bool enabled)
    {
        var current = preferences.Load(); if (current.FullScreenSilentEnabled == enabled) return;
        preferences.Save(current with { FullScreenSilentEnabled = enabled }); RefreshSchedule();
    }

    public Task<bool> PollNowAsync() => ScanOnceAsync(DateTimeOffset.UtcNow);

    public async Task<bool> ScanOnceAsync(DateTimeOffset nowUtc)
    {
        nowUtc = nowUtc.ToUniversalTime();
        if (Volatile.Read(ref disposed) != 0) return false;
        if (!await pollGate.WaitAsync(0).ConfigureAwait(false))
        {
            UpdateHealth(current => current with { SkippedOverlappingTicks = current.SkippedOverlappingTicks + 1 });
            return false;
        }

        UpdateHealth(current => current with { IsPolling = true });
        try
        {
            await Task.Run(() => PollCore(nowUtc)).ConfigureAwait(false);
            RecordSuccess();
            return true;
        }
        catch (Exception exception)
        {
            RecordFailure(exception);
            return true;
        }
        finally
        {
            pollGate.Release();
        }
    }

    void PollCore(DateTimeOffset nowUtc)
    {
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, TimeZoneInfo.Local).DateTime;
        var due = data.ClaimDueReminders(localNow);
        foreach (var item in due)
        {
            var dueAt = item.RemindAt ?? item.StartsAt;
            RegisterOccurrence(item, new DateTimeOffset(dueAt));
        }

        while (dueDetector.TryClaimDue(nowUtc, Guid.NewGuid().ToString("N")) is { } lease)
        {
            try
            {
                var item = ResolveOccurrenceItem(lease)
                    ?? throw new InvalidOperationException("The reminder item no longer exists.");
                var current = preferences.Load();
                var context = new ReminderPresentationContext(
                    current.FullScreenSilentEnabled,
                    focus?.RestoreActive() is not null,
                    current.DoNotDisturbEnabled);
                var decision = policy.Apply(new ReminderPolicyInput(
                    item.Id,
                    lease.OccurrenceKey,
                    lease.DueAtUtc,
                    ReminderPriority.Normal,
                    context), nowUtc);

                if (decision.Kind == ReminderPolicyDecisionKind.Defer)
                {
                    if (!dueDetector.MarkDelivered(lease, nowUtc))
                        throw new InvalidOperationException("The reminder occurrence lease was lost.");
                    continue;
                }

                var action = decision.Kind == ReminderPolicyDecisionKind.IslandBannerOnly
                    ? "IslandBanner"
                    : "ToastAndIsland";
                var key = NotificationIdempotencyKey.Create(
                    item.Id,
                    lease.OccurrenceKey,
                    action,
                    lease.RuleRevision,
                    lease.DueAtUtc);
                deliveryStore.EnqueueNotification(new NotificationOutboxEntry(
                    key,
                    item.Id,
                    lease.OccurrenceKey,
                    action,
                    nowUtc));
                if (!dueDetector.MarkDelivered(lease, nowUtc))
                    throw new InvalidOperationException("The reminder occurrence lease was lost.");
            }
            catch (Exception exception)
            {
                var failure = dueDetector.MarkFailed(lease, nowUtc, exception.Message);
                if (failure != ReminderFailureOutcome.NotOwned) throw;
            }
        }

        for (var attempt = 0; attempt < 64; attempt++)
        {
            var result = notificationDispatcher.DispatchNextAsync(nowUtc).GetAwaiter().GetResult();
            if (result == NotificationDispatchOutcome.NothingDue) break;
            if (result is NotificationDispatchOutcome.RetryScheduled or NotificationDispatchOutcome.DeadLettered)
                throw new InvalidOperationException(notificationDispatcher.Health.LastError ?? "Notification delivery failed.");
        }

        RefreshSchedule();
    }

    void RegisterUpcoming(AgendaItem item)
    {
        if (item.RemindAt is not { } dueAt) return;
        RegisterOccurrence(item, new DateTimeOffset(dueAt));
    }

    void RegisterOccurrence(AgendaItem item, DateTimeOffset dueAt)
    {
        var occurrenceKey = OccurrenceKey(item, dueAt);
        lock (occurrenceItemsLock)
            occurrenceItems[occurrenceKey] = item with
            {
                StartsAt = dueAt.LocalDateTime,
                RemindAt = dueAt.LocalDateTime
            };
        dueDetector.Register(new ReminderOccurrenceSeed(occurrenceKey, item.Id, dueAt));
    }

    AgendaItem? ResolveOccurrenceItem(ReminderOccurrenceLease lease)
    {
        lock (occurrenceItemsLock)
            if (occurrenceItems.TryGetValue(lease.OccurrenceKey, out var known)) return known;

        var parts = lease.OccurrenceKey.Split('', 3);
        if (parts.Length != 3) return null;
        var managed = data.ManagedItems().FirstOrDefault(item =>
            item.Id == lease.ItemId && string.Equals(item.Kind, parts[0], StringComparison.Ordinal));
        return managed is null
            ? null
            : new AgendaItem(managed.Id, managed.Kind, managed.Title, managed.Notes,
                lease.DueAtUtc.LocalDateTime, null, lease.DueAtUtc.LocalDateTime, false,
                string.Equals(managed.Kind, "recurring", StringComparison.Ordinal));
    }

    async Task DeliverOutboxNotificationAsync(NotificationDeliveryRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var item = ResolveOccurrenceItem(new ReminderOccurrenceLease(
            request.OccurrenceKey, request.ItemId, request.TargetDeliveryAtUtc, 1,
            request.ClaimToken, DateTimeOffset.UtcNow, request.AttemptCount))
            ?? throw new InvalidOperationException("The reminder item no longer exists.");
        if (string.Equals(request.ActionType, "ToastAndIsland", StringComparison.Ordinal) &&
            preferences.Load().WindowsNotifications)
            notifications.ShowNow(item);
        ReminderDue?.Invoke(this, item);
        await Task.CompletedTask;
    }

    static string OccurrenceKey(AgendaItem item, DateTimeOffset dueAt) => string.Join('',
        item.Kind,
        item.Id,
        dueAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

    sealed class ReminderNotificationTransport(
        Func<NotificationDeliveryRequest, CancellationToken, Task> deliver) : INotificationTransport
    {
        public Task DeliverAsync(NotificationDeliveryRequest request, CancellationToken cancellationToken) =>
            deliver(request, cancellationToken);
    }

    void RecordSuccess()
    {
        var status = Volatile.Read(ref disposed) == 0 ? ReminderSchedulerStatus.Running : ReminderSchedulerStatus.Stopped;
        UpdateHealth(current => current with
        {
            Status = status,
            LastSuccessfulTickAt = DateTimeOffset.UtcNow,
            ConsecutiveFailures = 0,
            IsPolling = false,
            LastErrorMessage = null
        });
    }

    void RecordFailure(Exception exception)
    {
        var status = Volatile.Read(ref disposed) == 0 ? ReminderSchedulerStatus.Degraded : ReminderSchedulerStatus.Stopped;
        UpdateHealth(current => current with
        {
            Status = status,
            LastFailedTickAt = DateTimeOffset.UtcNow,
            ConsecutiveFailures = current.ConsecutiveFailures + 1,
            IsPolling = false,
            LastErrorMessage = exception.Message
        });
    }

    void UpdateHealth(Func<ReminderSchedulerHealth, ReminderSchedulerHealth> update)
    {
        ReminderSchedulerHealth snapshot;
        lock (healthLock)
        {
            health = update(health);
            snapshot = health;
        }

        try
        {
            HealthChanged?.Invoke(this, snapshot);
        }
        catch
        {
            // Health observers must not be able to terminate the reminder scheduler.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Interlocked.Exchange(ref started, 0);
        timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        timer.Dispose();
        UpdateHealth(current => current with { Status = ReminderSchedulerStatus.Stopped, IsPolling = false });
    }
}

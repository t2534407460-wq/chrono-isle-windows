using ChronoIsle.App.Services.Persistence;
using ChronoIsle.App.Services.Productivity;
using ChronoIsle.App.Services.Scheduling;

namespace ChronoIsle.App.Services;

public sealed class ReminderService : IDisposable
{
    readonly LifeDataService data;
    readonly LifePreferencesService preferences;
    readonly WindowsNotificationService notifications;
    readonly System.Threading.Timer timer;
    readonly ReminderPolicyService policy;
    readonly FocusService? focus;

    public event EventHandler<AgendaItem>? ReminderDue;
    public event EventHandler<DeferredNotificationSummary>? DeferredSummaryReleased;

    public ReminderService(LifeDataService data, LifePreferencesService preferences, WindowsNotificationService notifications, FocusService? focus = null)
    {
        this.data = data;
        this.preferences = preferences;
        this.notifications = notifications;
        this.focus = focus;
        var runtime = LifeDataStoreRuntimeRegistry.GetOrCreate(data.DatabasePath);
        policy = new ReminderPolicyService(new ReminderDeliveryStore(runtime.WriteQueue));
        timer = new System.Threading.Timer(_ => Poll(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Start()
    {
        RefreshSchedule();
        timer.Change(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));
    }

    public void RefreshSchedule()
    {
        foreach (var item in data.ReminderItems())
        {
            notifications.Remove(item);
            var current = preferences.Load();
            if (current.WindowsNotifications && !current.DoNotDisturbEnabled && !current.FullScreenSilentEnabled) notifications.Schedule(item);
        }
    }

    public void Schedule(AgendaItem item)
    {
        var current = preferences.Load();
        if (current.WindowsNotifications && !current.DoNotDisturbEnabled && !current.FullScreenSilentEnabled)
            notifications.Schedule(item);
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
        var current = preferences.Load();
        if (current.DoNotDisturbEnabled == enabled) return;
        preferences.Save(current with { DoNotDisturbEnabled = enabled });
        RefreshSchedule();
        if (!enabled)
            DeferredSummaryReleased?.Invoke(this, policy.ReleaseDeferredSummary(DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N")));
    }

    public void SetFullScreenSilent(bool enabled)
    {
        var current = preferences.Load();
        if (current.FullScreenSilentEnabled == enabled) return;
        preferences.Save(current with { FullScreenSilentEnabled = enabled });
        RefreshSchedule();
    }

    void Poll()
    {
        var due = data.ClaimDueReminders(DateTime.Now);
        var current = preferences.Load();
        foreach (var item in due)
        {
            var dueAt = item.RemindAt ?? item.StartsAt;
            var context = new ReminderPresentationContext(current.FullScreenSilentEnabled, focus?.RestoreActive() is not null, current.DoNotDisturbEnabled);
            var occurrenceKey = $"legacy:{item.Kind}:{item.Id}:{dueAt.ToUniversalTime():O}";
            var decision = policy.Apply(new ReminderPolicyInput(item.Id, occurrenceKey, new DateTimeOffset(dueAt), ReminderPriority.Normal, context), DateTimeOffset.UtcNow);
            if (decision.Kind != ReminderPolicyDecisionKind.Defer) ReminderDue?.Invoke(this, item);
        }
        if (due.Count > 0) RefreshSchedule();
    }

    public void Dispose() => timer.Dispose();
}
namespace OpenIsland.App.Services;

public sealed class ReminderService : IDisposable
{
    readonly LifeDataService data;
    readonly LifePreferencesService preferences;
    readonly WindowsNotificationService notifications;
    readonly System.Threading.Timer timer;

    public event EventHandler<AgendaItem>? ReminderDue;

    public ReminderService(LifeDataService data, LifePreferencesService preferences, WindowsNotificationService notifications)
    {
        this.data = data;
        this.preferences = preferences;
        this.notifications = notifications;
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
            if (preferences.Load().WindowsNotifications) notifications.Schedule(item);
        }
    }

    public void Schedule(AgendaItem item)
    {
        if (preferences.Load().WindowsNotifications) notifications.Schedule(item);
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

    void Poll()
    {
        var due = data.ClaimDueReminders(DateTime.Now);
        foreach (var item in due) ReminderDue?.Invoke(this, item);
        if (due.Count > 0) RefreshSchedule();
    }

    public void Dispose() => timer.Dispose();
}
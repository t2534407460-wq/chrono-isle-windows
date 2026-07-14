using System.Security.Cryptography;
using System.Text;
using Microsoft.Toolkit.Uwp.Notifications;
using Windows.UI.Notifications;

namespace OpenIsland.App.Services;

public sealed class WindowsNotificationService
{
    const string Group = "OpenIsland";
    bool registered;

    public event EventHandler<(string Kind, string Id)>? Activated;
    public bool IsAvailable { get; private set; }

    public void Register()
    {
        if (registered) return;
        try
        {
            ToastNotificationManagerCompat.OnActivated += OnActivated;
            registered = true;
            IsAvailable = true;
        }
        catch
        {
            IsAvailable = false;
        }
    }

    public void Schedule(AgendaItem item)
    {
        if (!IsAvailable || item.RemindAt is null || item.RemindAt <= DateTime.Now) return;
        Remove(item);
        var content = new ToastContentBuilder()
            .AddArgument("kind", item.Kind)
            .AddArgument("id", item.Id)
            .AddText(item.Kind switch { "event" => "\u65e5\u7a0b\u63d0\u9192", "reminder" => "\u63d0\u9192", "recurring" => "\u5468\u671f\u63d0\u9192", _ => "\u5f85\u529e\u63d0\u9192" })
            .AddText(item.Title)
            .GetToastContent();
        var scheduled = new ScheduledToastNotification(content.GetXml(), new DateTimeOffset(item.RemindAt.Value))
        {
            Tag = TagFor(item),
            Group = Group
        };
        ToastNotificationManagerCompat.CreateToastNotifier().AddToSchedule(scheduled);
    }

    public void Remove(AgendaItem item)
    {
        if (!IsAvailable) return;
        var notifier = ToastNotificationManagerCompat.CreateToastNotifier();
        foreach (var scheduled in notifier.GetScheduledToastNotifications().Where(x => x.Group == Group && x.Tag == TagFor(item)).ToList())
            notifier.RemoveFromSchedule(scheduled);
    }

    void OnActivated(ToastNotificationActivatedEventArgsCompat args)
    {
        var values = args.Argument.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Split('=', 2))
            .Where(x => x.Length == 2)
            .ToDictionary(x => Uri.UnescapeDataString(x[0]), x => Uri.UnescapeDataString(x[1]), StringComparer.OrdinalIgnoreCase);
        if (values.TryGetValue("kind", out var kind) && values.TryGetValue("id", out var id))
            Activated?.Invoke(this, (kind, id));
    }

    static string TagFor(AgendaItem item)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(item.Kind + ":" + item.Id));
        return Convert.ToHexString(hash)[..16];
    }
}

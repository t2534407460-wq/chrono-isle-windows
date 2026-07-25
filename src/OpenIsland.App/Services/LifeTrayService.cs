using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using OpenIsland.App.Views;

namespace OpenIsland.App.Services;

public sealed class LifeTrayService : IDisposable
{
    readonly ReminderService reminders;
    readonly LifePreferencesService preferences;
    Forms.NotifyIcon? icon;
    TrayMenuWindow? menu;

    public event EventHandler? OpenRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ManageRequested;
    public event EventHandler? NamingRequested;
    public event EventHandler? ExitRequested;

    public LifeTrayService(ReminderService reminders, LifePreferencesService preferences)
    {
        this.reminders = reminders;
        this.preferences = preferences;
    }

    public void Initialize()
    {
        if (icon is not null) return;
        icon = new Forms.NotifyIcon
        {
            Icon = CurrentIcon(),
            Text = "Island",
            Visible = true
        };
        icon.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
                OpenRequested?.Invoke(this, EventArgs.Empty);
            else if (args.Button == Forms.MouseButtons.Right)
                ShowMenu();
        };
    }

    void ShowMenu()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        dispatcher.BeginInvoke(() =>
        {
            if (menu?.IsVisible == true)
            {
                menu.Activate();
                return;
            }

            var current = preferences.Load();
            var window = new TrayMenuWindow(current.WindowsNotifications, reminders.IsDoNotDisturbEnabled);
            menu = window;
            window.OpenRequested += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
            window.SettingsRequested += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
            window.ManageRequested += (_, _) => ManageRequested?.Invoke(this, EventArgs.Empty);
            window.NamingRequested += (_, _) => NamingRequested?.Invoke(this, EventArgs.Empty);
            window.ExitRequested += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
            window.WindowsNotificationsChanged += enabled => UpdateWindowsNotifications(enabled);
            window.DoNotDisturbChanged += enabled => reminders.SetDoNotDisturb(enabled);
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(menu, window)) menu = null;
            };
            window.ShowAtCursor();
        });
    }

    void UpdateWindowsNotifications(bool enabled)
    {
        var current = preferences.Load();
        if (current.WindowsNotifications == enabled) return;
        preferences.Save(current with { WindowsNotifications = enabled });
        reminders.RefreshSchedule();
    }

    static Drawing.Icon CurrentIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/OpenIsland;component/Assets/face.ico", UriKind.Absolute);
            using var stream = System.Windows.Application.GetResourceStream(uri)?.Stream;
            if (stream is not null) return new Drawing.Icon(stream);
        }
        catch { }
        return Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        menu?.Close();
        menu = null;
        if (icon is null) return;
        icon.Visible = false;
        icon.Dispose();
        icon = null;
    }
}
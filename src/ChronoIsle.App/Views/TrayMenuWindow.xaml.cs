using System.Windows;

namespace ChronoIsle.App.Views;

public partial class TrayMenuWindow : Window
{
    readonly bool notificationsEnabled;
    readonly bool doNotDisturbEnabled;

    public event EventHandler? OpenRequested;
    public event EventHandler? ManageRequested;
    public event EventHandler? NamingRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;
    public event Action<bool>? WindowsNotificationsChanged;
    public event Action<bool>? DoNotDisturbChanged;

    public TrayMenuWindow(bool notificationsEnabled, bool doNotDisturbEnabled)
    {
        InitializeComponent();
        this.notificationsEnabled = notificationsEnabled;
        this.doNotDisturbEnabled = doNotDisturbEnabled;
        NotificationsValue.Text = notificationsEnabled ? "通知开" : "通知关";
        DoNotDisturbValue.Text = doNotDisturbEnabled ? "勿扰开" : "勿扰关";
        Deactivated += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (IsVisible) Close();
        });
    }

    public void ShowAtCursor()
    {
        Show();
        var cursor = System.Windows.Forms.Control.MousePosition;
        var area = SystemParameters.WorkArea;
        Left = Math.Clamp(cursor.X - ActualWidth + 8, area.Left + 8, area.Right - ActualWidth - 8);
        Top = Math.Clamp(cursor.Y - ActualHeight - 8, area.Top + 8, area.Bottom - ActualHeight - 8);
    }

    void Open_Click(object sender, RoutedEventArgs e) => CloseAfter(() => OpenRequested?.Invoke(this, EventArgs.Empty));
    void Manage_Click(object sender, RoutedEventArgs e) => CloseAfter(() => ManageRequested?.Invoke(this, EventArgs.Empty));
    void Naming_Click(object sender, RoutedEventArgs e) => CloseAfter(() => NamingRequested?.Invoke(this, EventArgs.Empty));
    void Settings_Click(object sender, RoutedEventArgs e) => CloseAfter(() => SettingsRequested?.Invoke(this, EventArgs.Empty));
    void Exit_Click(object sender, RoutedEventArgs e) => CloseAfter(() => ExitRequested?.Invoke(this, EventArgs.Empty));
    void Notifications_Click(object sender, RoutedEventArgs e) => CloseAfter(() => WindowsNotificationsChanged?.Invoke(!notificationsEnabled));
    void DoNotDisturb_Click(object sender, RoutedEventArgs e) => CloseAfter(() => DoNotDisturbChanged?.Invoke(!doNotDisturbEnabled));

    void CloseAfter(Action action)
    {
        Close();
        action();
    }
}

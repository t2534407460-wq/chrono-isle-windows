using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace ChronoIsle.App.Views;

public partial class TrayMenuWindow : Window
{
    static readonly TimeSpan TrayHostCloseDelay = TimeSpan.FromMilliseconds(1200);
    readonly bool notificationsEnabled;
    readonly bool doNotDisturbEnabled;
    readonly DispatcherTimer trayHostTimer;
    nint trayHost;
    DateTime? trayHostHiddenSinceUtc;

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
        trayHostTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        trayHostTimer.Tick += MonitorTrayHost;
        this.notificationsEnabled = notificationsEnabled;
        this.doNotDisturbEnabled = doNotDisturbEnabled;
        NotificationsValue.Text = notificationsEnabled ? "通知开" : "通知关";
        DoNotDisturbValue.Text = doNotDisturbEnabled ? "勿扰开" : "勿扰关";
        Deactivated += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (IsVisible && !IsActive) Close();
        });
        AddHandler(Mouse.PreviewMouseDownOutsideCapturedElementEvent,
            new MouseButtonEventHandler((_, _) => Close()), true);
        PreviewKeyDown += (_, args) =>
            { if (args.Key == Key.Escape) Close(); };
    }

    internal static nint CaptureTrayHostAtCursor()
    {
        var cursor = Forms.Control.MousePosition;
        var window = WindowFromPoint(new NativePoint(cursor.X, cursor.Y));
        var root = GetAncestor(window, 2);
        return root != 0 ? root : window;
    }

    public void ShowAtCursor(nint trayHost)
    {
        this.trayHost = trayHost;
        Show();
        var cursor = Forms.Control.MousePosition;
        var area = SystemParameters.WorkArea;
        Left = Math.Clamp(cursor.X - ActualWidth + 8, area.Left + 8, area.Right - ActualWidth - 8);
        Top = Math.Clamp(cursor.Y - ActualHeight - 8, area.Top + 8, area.Bottom - ActualHeight - 8);
        Activate();
        Focus();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (IsVisible) Mouse.Capture(this, CaptureMode.SubTree);
        });
        if (trayHost != 0) trayHostTimer.Start();
    }

    void MonitorTrayHost(object? sender, EventArgs e)
    {
        if (IsTrayHostVisible())
        {
            trayHostHiddenSinceUtc = null;
            return;
        }

        var now = DateTime.UtcNow;
        trayHostHiddenSinceUtc ??= now;
        if (now - trayHostHiddenSinceUtc >= TrayHostCloseDelay && IsVisible)
            Close();
    }

    bool IsTrayHostVisible()
    {
        if (trayHost == 0) return true;
        if (!IsWindow(trayHost) || !IsWindowVisible(trayHost) || !GetWindowRect(trayHost, out var rect))
            return false;

        if (DwmGetWindowAttribute(trayHost, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;
        var screen = Forms.Screen.FromHandle(trayHost).Bounds;
        var host = Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        var visible = Drawing.Rectangle.Intersect(screen, host);
        return visible.Width >= 8 && visible.Height >= 8;
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

    protected override void OnClosed(EventArgs e)
    {
        trayHostTimer.Stop();
        if (IsMouseCaptureWithin || IsMouseCaptured) Mouse.Capture(null);
        base.OnClosed(e);
    }

    [StructLayout(LayoutKind.Sequential)]
    readonly struct NativePoint
    {
        public readonly int X;
        public readonly int Y;

        public NativePoint(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    static extern nint WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    static extern nint GetAncestor(nint window, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetWindowRect(nint window, out NativeRect rect);

    [DllImport("dwmapi.dll")]
    static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int valueSize);
}

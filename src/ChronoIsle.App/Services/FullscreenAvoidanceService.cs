using System.Drawing;
using System.Runtime.InteropServices;

namespace ChronoIsle.App.Services;

public sealed record FullscreenWindowInfo(string MonitorDeviceName, Rectangle MonitorBounds);

/// <summary>检测其他进程是否有前台窗口完整覆盖了所在显示器。</summary>
public sealed class FullscreenAvoidanceService : IDisposable
{
    const int DwmExtendedFrameBounds = 9;
    readonly System.Threading.Timer timer;
    FullscreenWindowInfo? lastValue;
    bool disposed;
    public FullscreenWindowInfo? Current => lastValue;

    public FullscreenAvoidanceService() =>
        timer = new System.Threading.Timer(_ => Poll(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    public event Action<FullscreenWindowInfo?>? ContextChanged;

    public void Start() => timer.Change(TimeSpan.Zero, TimeSpan.FromMilliseconds(500));

    void Poll()
    {
        if (disposed) return;
        var context = Detect();
        if (lastValue == context) return;
        lastValue = context;
        ContextChanged?.Invoke(context);
    }

    static FullscreenWindowInfo? Detect()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || !IsWindowVisible(window)) return null;
        GetWindowThreadProcessId(window, out var processId);
        if (processId == (uint)Environment.ProcessId) return null;

        var className = new char[128];
        var classLength = GetClassName(window, className, className.Length);
        var windowClass = classLength > 0 ? new string(className, 0, classLength) : string.Empty;
        if (windowClass is "Progman" or "WorkerW" or "Shell_TrayWnd") return null;

        if (DwmGetWindowAttribute(window, DwmExtendedFrameBounds, out var bounds, Marshal.SizeOf<NativeRect>()) != 0 &&
            !GetWindowRect(window, out bounds))
            return null;

        var windowBounds = Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
        var monitor = System.Windows.Forms.Screen.FromHandle(window);
        return CoversMonitor(windowBounds, monitor.Bounds)
            ? new FullscreenWindowInfo(monitor.DeviceName, monitor.Bounds)
            : null;
    }

    public static bool CoversMonitor(Rectangle window, Rectangle monitor, int tolerance = 2) =>
        window.Width > 0 &&
        window.Height > 0 &&
        Math.Abs(window.Left - monitor.Left) <= tolerance &&
        Math.Abs(window.Top - monitor.Top) <= tolerance &&
        Math.Abs(window.Right - monitor.Right) <= tolerance &&
        Math.Abs(window.Bottom - monitor.Bottom) <= tolerance;

    public void Dispose()
    {
        disposed = true;
        timer.Dispose();
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
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr window, char[] className, int maximumCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);

    [DllImport("dwmapi.dll")]
    static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out NativeRect value, int valueSize);
}

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ChronoIsle.App.Services;

internal static class NotificationAppLauncher
{
    internal static bool Open(string appId) => Open(appId,
        id => FindRunningWindow(ResolveExecutable(id)), ActivateWindow, Launch);

    internal static bool Open(string appId,
        Func<string, (bool IsRunning, IntPtr Window)> findWindow,
        Func<IntPtr, bool> activateWindow, Action<string> launch)
    {
        var existing = findWindow(appId);
        if (existing.IsRunning)
            return existing.Window != IntPtr.Zero && activateWindow(existing.Window);
        launch(appId);
        return true;
    }

    static string? ResolveExecutable(string appId)
    {
        object? shell = null, folder = null, item = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
            folder = ((dynamic)shell!).NameSpace("shell:AppsFolder");
            if (folder is null) return null;
            item = ((dynamic)folder).ParseName(appId);
            return item is null ? null : ((dynamic)item).ExtendedProperty("System.Link.TargetParsingPath") as string;
        }
        finally
        {
            if (item is not null) Marshal.FinalReleaseComObject(item);
            if (folder is not null) Marshal.FinalReleaseComObject(folder);
            if (shell is not null) Marshal.FinalReleaseComObject(shell);
        }
    }

    internal static (bool IsRunning, IntPtr Window) FindRunningWindow(string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable))
            return (false, IntPtr.Zero);

        var processIds = new HashSet<int>();
        var inaccessibleProcess = false;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
                        processIds.Add(process.Id);
                }
                catch (System.ComponentModel.Win32Exception) { inaccessibleProcess = true; }
                catch (InvalidOperationException) { }
            }
        }
        if (processIds.Count == 0) return (inaccessibleProcess, IntPtr.Zero);

        var window = IntPtr.Zero;
        EnumWindows((candidate, _) =>
        {
            GetWindowThreadProcessId(candidate, out var processId);
            if (!processIds.Contains(processId) || GetWindow(candidate, 4) != IntPtr.Zero ||
                GetWindowTextLength(candidate) == 0 || (GetWindowLongPtr(candidate, -20).ToInt64() & 0x80) != 0)
                return true;
            if (!GetWindowRect(candidate, out var bounds) || bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top)
                return true;
            // 托盘窗口通常不可见，不能依赖 MainWindowHandle；优先复用可见窗口。
            if (window == IntPtr.Zero || IsWindowVisible(candidate)) window = candidate;
            return !IsWindowVisible(candidate);
        }, IntPtr.Zero);
        return (true, window);
    }

    static bool ActivateWindow(IntPtr window)
    {
        ShowWindowAsync(window, IsIconic(window) ? 9 : 5);
        return SetForegroundWindow(window);
    }

    static void Launch(string appId)
    {
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        start.ArgumentList.Add(@"shell:AppsFolder\" + appId);
        Process.Start(start)?.Dispose();
    }

    delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);
    [StructLayout(LayoutKind.Sequential)]
    struct WindowRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr window, out WindowRect bounds);
    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);
    [DllImport("user32.dll")]
    static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]
    static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")]
    static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr window);
}

using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChronoIsle.App.ViewModels;
using ChronoIsle.App.Views;

namespace ChronoIsle.UiTests;

static class MaximizedChatWindowChecks
{
    public static void Verify(LifeViewModel vm, IServiceProvider services, string? output)
    {
        var monitors = new List<nint>();
        EnumDisplayMonitors(0, 0, (nint monitor, nint dc, ref NativeRect bounds, nint parameter) => { monitors.Add(monitor); return true; }, 0);
        var observations = new List<object>();
        var failures = new List<string>();
        var previousDpi = SetThreadDpiAwarenessContext(new nint(-4));
        try
        {
            // Synthetic long answers reproduce the user's layout without reading their conversation.
            vm.Messages.Clear();
            vm.Messages.Add(new("long-answer", vm.SelectedSession!.Id, "assistant", "知识库问答\n\n" +
                string.Join('\n', Enumerable.Repeat("原文 [S1]：这是用于检查长回答滚动及输入框可见范围的合成测试资料。", 100)), DateTime.Now));
            foreach (var monitor in monitors)
            {
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                Assert.True(GetMonitorInfo(monitor, ref info));
                var window = new LifeMainWindow(vm, services)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
                    ShowInTaskbar = false, ShowActivated = false
                };
                try
                {
                    window.Show();
                    var handle = new WindowInteropHelper(window).Handle;
                    Assert.True(SetWindowPos(handle, 0, info.Work.Left + 30, info.Work.Top + 30,
                        Math.Min(1000, info.Work.Right - info.Work.Left - 60), Math.Min(760, info.Work.Bottom - info.Work.Top - 60), 0x14));
                    Pump(window);
                    Assert.True(GetWindowRect(handle, out var normal));
                    var minMaxPointer = Marshal.AllocHGlobal(Marshal.SizeOf<MinMaxInfo>());
                    try
                    {
                        Marshal.StructureToPtr(new MinMaxInfo { MaxTrackSize = new NativePoint { X = 10000, Y = 10000 } }, minMaxPointer, false);
                        SendMessage(handle, 0x0024, 0, minMaxPointer);
                        var minMax = Marshal.PtrToStructure<MinMaxInfo>(minMaxPointer);
                        var dpiScale = GetDpiForWindow(handle) / 96d;
                        Assert.True(minMax.MinTrackSize.X >= window.MinWidth * dpiScale);
                        Assert.True(minMax.MinTrackSize.Y >= window.MinHeight * dpiScale);
                    }
                    finally { Marshal.FreeHGlobal(minMaxPointer); }
                    for (var repeat = 0; repeat < 2; repeat++)
                    {
                        window.WindowState = WindowState.Maximized;
                        Pump(window);
                        Assert.True(GetWindowRect(handle, out var maximized));
                        var input = Descendants<TextBox>(window).Single(t => AutomationProperties.GetAutomationId(t) == "ChatInput");
                        var send = Descendants<Button>(window).Single(b => AutomationProperties.GetAutomationId(b) == "ChatSendButton");
                        var settings = Descendants<Button>(window).Single(b => Equals(b.Content, "设置"));
                        var inputBottom = input.PointToScreen(new Point(input.ActualWidth, input.ActualHeight));
                        var sendBottom = send.PointToScreen(new Point(send.ActualWidth, send.ActualHeight));
                        var settingsBottom = settings.PointToScreen(new Point(settings.ActualWidth, settings.ActualHeight));
                        observations.Add(new { Repeat = repeat, Dpi = GetDpiForWindow(handle), Work = info.Work, Window = maximized,
                            InputBottom = new { inputBottom.X, inputBottom.Y }, SendBottom = new { sendBottom.X, sendBottom.Y }, SettingsBottom = new { settingsBottom.X, settingsBottom.Y } });
                        if (maximized.Left < info.Work.Left || maximized.Top < info.Work.Top || maximized.Right > info.Work.Right || maximized.Bottom > info.Work.Bottom)
                            failures.Add($"Window {maximized} extends beyond work area {info.Work}.");
                        if (inputBottom.Y > info.Work.Bottom || sendBottom.Y > info.Work.Bottom || settingsBottom.Y > info.Work.Bottom)
                            failures.Add($"Bottom controls extend beyond work area: input={inputBottom.Y}, send={sendBottom.Y}, settings={settingsBottom.Y}, work={info.Work.Bottom}.");
                        window.WindowState = WindowState.Normal;
                        Pump(window);
                        Assert.True(GetWindowRect(handle, out var restored));
                        Assert.Equal(normal, restored);
                    }
                    if ((info.Flags & 1) != 0) VerifyDpiLayouts(window, info.Work, output);
                }
                finally { window.Close(); }
            }
        }
        finally
        {
            if (previousDpi != 0) SetThreadDpiAwarenessContext(previousDpi);
            if (!string.IsNullOrWhiteSpace(output))
            {
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "maximized-chat-bounds.json"), JsonSerializer.Serialize(new { Observations = observations, Failures = failures }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
            }
        }
        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    public static void VerifyMarkdownViewer(MarkdownViewerWindow window, string? output)
    {
        var observations = new List<object>();
        var handle = new WindowInteropHelper(window).Handle;
        var monitors = new List<nint>();
        EnumDisplayMonitors(0, 0, (nint monitor, nint dc, ref NativeRect bounds, nint parameter) => { monitors.Add(monitor); return true; }, 0);
        var maximize = Descendants<Button>(window).Single(b => AutomationProperties.GetAutomationId(b) == "MarkdownMaximize");
        var minimize = Descendants<Button>(window).Single(b => AutomationProperties.GetAutomationId(b) == "MarkdownMinimize");
        minimize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(window);
        Assert.Equal(WindowState.Minimized, window.WindowState);
        window.WindowState = WindowState.Normal; Pump(window);
        foreach (var monitor in monitors)
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            Assert.True(GetMonitorInfo(monitor, ref info));
            Assert.True(SetWindowPos(handle, 0, info.Work.Left + 30, info.Work.Top + 30, 700, 500, 0x14));
            Pump(window);
            Assert.True(GetWindowRect(handle, out var normal));
            for (var repeat = 0; repeat < 2; repeat++)
            {
                maximize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(window);
                Assert.Equal(WindowState.Maximized, window.WindowState);
                Assert.True(GetWindowRect(handle, out var maximized));
                Assert.Equal(info.Work, maximized);
                var viewer = Descendants<MarkdownView>(window).Single();
                var bottom = viewer.PointToScreen(new Point(viewer.ActualWidth, viewer.ActualHeight));
                Assert.True(bottom.Y <= info.Work.Bottom && bottom.X <= info.Work.Right);
                observations.Add(new { Repeat = repeat, Work = info.Work, Window = maximized, Bottom = new { bottom.X, bottom.Y } });
                maximize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(window);
                Assert.Equal(WindowState.Normal, window.WindowState);
                Assert.True(GetWindowRect(handle, out var restored));
                Assert.Equal(normal, restored);
            }
        }
        window.Width = window.MinWidth; window.Height = window.MinHeight; Pump(window);
        var close = Descendants<Button>(window).Single(b => AutomationProperties.GetAutomationId(b) == "MarkdownClose");
        var closeBounds = close.TransformToAncestor(window).TransformBounds(new Rect(close.RenderSize));
        Assert.True(closeBounds.Right <= window.ActualWidth && closeBounds.Top >= 0 && closeBounds.Bottom <= 49);
        Assert.True(Descendants<MarkdownView>(window).Single().ActualHeight > 0);
        if (!string.IsNullOrWhiteSpace(output)) File.WriteAllText(Path.Combine(output, "markdown-viewer-bounds.json"),
            JsonSerializer.Serialize(observations, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
    }

    static void VerifyDpiLayouts(Window window, NativeRect work, string? output)
    {
        // Render the real content at simulated DPI without changing the user's display settings.
        var content = (FrameworkElement)window.Content;
        content.DataContext = window.DataContext;
        window.Content = null;
        Pump(window);
        Assert.Null(VisualTreeHelper.GetParent(content));
        // Reparent to resume layout after WPF suspends a removed visual tree.
        var preview = new Border { Child = content };
        var input = Descendants<TextBox>(content).Single(t => AutomationProperties.GetAutomationId(t) == "ChatInput");
        var send = Descendants<Button>(content).Single(b => AutomationProperties.GetAutomationId(b) == "ChatSendButton");
        foreach (var scale in new[] { 1d, 1.25, 1.5, 2d })
        {
            VisualTreeHelper.SetRootDpi(preview, new DpiScale(scale, scale));
            var size = new Size((work.Right - work.Left) / scale, (work.Bottom - work.Top) / scale);
            preview.Width = size.Width;
            preview.Height = size.Height;
            preview.Measure(size);
            preview.Arrange(new Rect(size));
            preview.UpdateLayout();
            Assert.Equal(scale, VisualTreeHelper.GetDpi(content).DpiScaleX);
            foreach (var element in new FrameworkElement[] { input, send })
            {
                var bounds = element.TransformToAncestor(content).TransformBounds(new Rect(element.RenderSize));
                Assert.True(bounds.Bottom <= size.Height && bounds.Right <= size.Width && bounds.Top >= 0,
                    $"DPI={scale}; control={element.Name}; bounds={bounds}; viewport={size}; root={content.RenderSize}");
            }
            if (string.IsNullOrWhiteSpace(output)) continue;
            Directory.CreateDirectory(output);
            var bitmap = new RenderTargetBitmap(work.Right - work.Left, work.Bottom - work.Top, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(preview);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, $"maximized-chat-dpi-{scale * 100:0}.png"));
            encoder.Save(file);
        }
    }

    static void Pump(Window window)
    {
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T value) yield return value;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public override readonly string ToString() => $"({Left},{Top})-({Right},{Bottom})";
    }
    [StructLayout(LayoutKind.Sequential)]
    struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)]
    struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    struct MinMaxInfo { public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    delegate bool MonitorCallback(nint monitor, nint dc, ref NativeRect bounds, nint parameter);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
}

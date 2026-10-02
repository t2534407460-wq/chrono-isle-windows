using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChronoIsle.App.Services.Markdown;
using ChronoIsle.App.Services;
using ChronoIsle.App.ViewModels;
using ChronoIsle.App.Views;

namespace ChronoIsle.UiTests;

static class MarkdownUiChecks
{
    public static void Verify(LifeViewModel vm, IServiceProvider services, ThemeService theme, string? output)
    {
        var directory = Path.Combine(Path.GetTempPath(), "chronoisle-markdown-ui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "来源 中文 [示例].md");
        var markdown = "# 文档标题\n\n## 操作步骤\n\n1. **先停止应用**，再调用 `run(items[0])`。\n2. 运行检查。\n\n```csharp\nvar items = new[] { 1, 2 };\nRun(items[0]);\n```\n\n| 参数 | 说明 |\n| --- | --- |\n| page | 页面 |\n\n> 引用内容\n\n[参考文档](https://example.com/guide)\n\n";
        File.WriteAllText(path, markdown);
        vm.Messages.Clear();
        vm.Messages.Add(new("long", vm.SelectedSession!.Id, "assistant", markdown + string.Join("\n\n", Enumerable.Range(1, 100).Select(i => $"段落 {i}：用于验证滚轮可以查看完整对话。")) + $"\n\n[来源]({MarkdownDocuments.SourceLink(path, 3)})\n\n最后一行 END_MARKER", DateTime.Now));
        var window = new LifeMainWindow(vm, services) { Width = 840, Height = 560, Left = -32000, Top = -32000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show(); Pump(window);
            var chat = Descendants<ListBox>(window).Single(l => ReferenceEquals(l.ItemsSource, vm.Messages));
            var scroll = Descendants<ScrollViewer>(chat).First();
            var answer = Descendants<MarkdownView>(chat).Single();
            Assert.Contains(answer.Document.Blocks, b => b is Table);
            Assert.Contains(answer.Document.Blocks, b => b is System.Windows.Documents.List);
            Assert.True(scroll.ScrollableHeight > scroll.ViewportHeight);
            scroll.ScrollToHome(); Pump(window);
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
            answer.RaiseEvent(wheel); Pump(window);
            Assert.True(wheel.Handled);
            Assert.True(scroll.VerticalOffset > 0);
            Assert.True(scroll.VerticalOffset < scroll.ScrollableHeight);
            scroll.ScrollToEnd(); Pump(window);
            Assert.InRange(scroll.ScrollableHeight - scroll.VerticalOffset, 0, 1);
            var end = answer.Document.ContentEnd.GetPositionAtOffset(-3)!;
            var endRect = end.GetCharacterRect(LogicalDirection.Backward);
            var screen = answer.PointToScreen(endRect.BottomLeft);
            var viewportTop = scroll.PointToScreen(new Point(0, 0)).Y;
            Assert.InRange(screen.Y, viewportTop, viewportTop + scroll.ActualHeight);
            answer.SelectAll(); Assert.Contains("END_MARKER", answer.Selection.Text);
            answer.Selection.Select(answer.Document.ContentStart, answer.Document.ContentStart);
            Save(window, output, "markdown-chat-scrolled-end");
            scroll.ScrollToHome(); Pump(window); Save(window, output, "markdown-chat-formatted");

            var links = Logical(answer.Document).OfType<Hyperlink>().ToArray();
            Assert.True(links.Length >= 2);
            VerifyFirstMouseClick(window, answer, scroll, links.Last(), output);
            var clicked = ""; answer.Navigate = address => clicked = address;
            links.Last().RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
            Assert.Equal(MarkdownDocuments.SourceLink(path, 3), clicked);
            // Actual link handler opens an actual viewer and asynchronously reads only our fixture.
            var previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(window.Dispatcher));
            MarkdownViewerWindow.OpenLink(window, clicked);
            var viewerWindow = Application.Current.Windows.OfType<MarkdownViewerWindow>().Single();
            try
            {
                for (var i = 0; i < 200 && !viewerWindow.Title.StartsWith("Markdown ·"); i++) { Pump(viewerWindow); Thread.Sleep(10); }
                Assert.True(viewerWindow.Title.StartsWith("Markdown ·"), string.Join("\n", Descendants<TextBlock>(viewerWindow).Select(t => t.Text)));
                Assert.Equal(WindowStyle.None, viewerWindow.WindowStyle);
                Assert.True(viewerWindow.AllowsTransparency);
                Assert.Same(window.FindResource("WindowControl"), viewerWindow.FindResource("WindowControl"));
                Assert.Contains(Descendants<TextBlock>(viewerWindow), t => t.Text == viewerWindow.Title);
                var viewer = Descendants<MarkdownView>(viewerWindow).Single();
                Assert.Contains("Run(items[0]);", new TextRange(viewer.Document.ContentStart, viewer.Document.ContentEnd).Text);
                foreach (var mode in new[] { AppThemeMode.Dark, AppThemeMode.Light })
                {
                    theme.Apply(mode); Pump(viewerWindow);
                    Assert.Equal(((SolidColorBrush)Application.Current.FindResource("Brush.TextPrimary")).Color, ((SolidColorBrush)viewer.Document.Foreground).Color);
                    Save(viewerWindow, output, "markdown-document-viewer-" + mode);
                    Save(window, output, "markdown-chat-formatted-" + mode);
                }
                MaximizedChatWindowChecks.VerifyMarkdownViewer(viewerWindow, output);
                var sourceToggle = Descendants<CheckBox>(viewerWindow).Single();
                sourceToggle.IsChecked = true; Pump(viewerWindow);
                Assert.Contains(Descendants<TextBox>(viewerWindow), box => box.IsVisible && box.IsReadOnly && box.Text == markdown);
                sourceToggle.IsChecked = false; Pump(viewerWindow);
                viewer.Markdown = "# 顶部\n\n" + string.Join("\n\n", Enumerable.Repeat("用于检查显式文档定位。", 100)) + "\n\n## 锚点\n\n目标内容";
                Pump(viewerWindow); viewer.JumpTo("#锚点"); Pump(viewerWindow);
                Assert.True(viewer.VerticalOffset > 0);
                viewer.JumpTo("#L1"); Pump(viewerWindow);
                Assert.InRange(viewer.VerticalOffset, 0, 1);
                viewer.Markdown = markdown; Pump(viewerWindow);
                // A missing next document leaves the current readable document in place and shows an error.
                var failed = viewerWindow.LoadAsync(new Uri(Path.Combine(directory, "missing.md")));
                for (var i = 0; i < 200 && !failed.IsCompleted; i++) { Pump(viewerWindow); Thread.Sleep(10); }
                failed.GetAwaiter().GetResult();
                Assert.Contains(Descendants<TextBlock>(viewerWindow), t => t.Text.StartsWith("无法打开文档："));
                Assert.Contains("文档标题", new TextRange(viewer.Document.ContentStart, viewer.Document.ContentEnd).Text);
                theme.Apply(AppThemeMode.Dark);
                MarkdownImageChecks.Verify(viewerWindow, output);
            }
            finally
            {
                Descendants<Button>(viewerWindow).Single(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "MarkdownClose").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(viewerWindow.IsVisible);
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        }
        finally { window.Close(); Directory.Delete(directory, true); }
    }

    static IEnumerable<DependencyObject> Logical(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) { yield return child; foreach (var item in Logical(child)) yield return item; }
    }

    static void VerifyFirstMouseClick(Window window, MarkdownView answer, ScrollViewer scroll, Hyperlink link, string? output)
    {
        var foreground = GetForegroundWindow();
        GetCursorPos(out var cursor);
        var oldNavigate = answer.Navigate;
        var calls = 0;
        var events = new System.Collections.Generic.List<string>();
        RequestBringIntoViewEventHandler log = (_, e) => events.Add($"bring: {e.TargetObject?.GetType().Name} rect={e.TargetRect}; offset={scroll.VerticalOffset}");
        window.AddHandler(FrameworkElement.RequestBringIntoViewEvent, log, true);
        try
        {
            window.Left = 80; window.Top = 80; window.Topmost = true; window.ShowActivated = true; window.Activate();
            SetForegroundWindow(new WindowInteropHelper(window).Handle);
            Pump(window);
            Descendants<TextBox>(window).Single(t => System.Windows.Automation.AutomationProperties.GetAutomationId(t) == "ChatInput").Focus();
            answer.Selection.Select(answer.Document.ContentStart, answer.Document.ContentStart);
            scroll.ScrollToEnd(); Pump(window);
            var before = scroll.VerticalOffset;
            answer.Navigate = _ => calls++;
            var rect = link.ContentStart.GetPositionAtOffset(1)!.GetCharacterRect(LogicalDirection.Forward);
            var point = answer.PointToScreen(new Point(rect.Left + 2, rect.Top + rect.Height / 2));
            events.Add($"point={point}; rect={rect}; foreground={GetForegroundWindow()}; window={new WindowInteropHelper(window).Handle}; hit={window.InputHitTest(window.PointFromScreen(point))?.GetType().Name}");
            Assert.Equal(new WindowInteropHelper(window).Handle, GetAncestor(WindowFromPoint(new NativePoint { X = (int)point.X, Y = (int)point.Y }), 2));
            SetCursorPos((int)point.X, (int)point.Y); PumpInput(window);
            mouse_event(0x0002, 0, 0, 0, 0); PumpInput(window);
            events.Add($"after down: offset={scroll.VerticalOffset}; calls={calls}; focus={Keyboard.FocusedElement?.GetType().Name}");
            mouse_event(0x0004, 0, 0, 0, 0); PumpInput(window);
            events.Add($"after up: offset={scroll.VerticalOffset}; calls={calls}; before={before}");
            if (!string.IsNullOrWhiteSpace(output)) File.WriteAllLines(Path.Combine(output, "first-link-click.txt"), events);
            Assert.Equal(1, calls);
            Assert.InRange(Math.Abs(scroll.VerticalOffset - before), 0, 1);
            Assert.True(link.IsKeyboardFocused);
            link.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(answer), Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
            Assert.Equal(2, calls); // Keep native keyboard activation after the mouse-focus correction.
        }
        finally
        {
            mouse_event(0x0004, 0, 0, 0, 0);
            answer.Navigate = oldNavigate;
            window.RemoveHandler(FrameworkElement.RequestBringIntoViewEvent, log);
            window.Topmost = false; window.Left = -32000; window.Top = -32000;
            SetCursorPos(cursor.X, cursor.Y); SetForegroundWindow(foreground);
        }
    }

    [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X, Y; }
    internal static void Click(Window window, FrameworkElement target)
    {
        var foreground = GetForegroundWindow(); GetCursorPos(out var cursor);
        var left = window.Left; var top = window.Top; var topmost = window.Topmost;
        try
        {
            window.Left = 80; window.Top = 80; window.Topmost = true; PumpInput(window);
            var point = target.PointToScreen(new Point(target.ActualWidth / 2, target.ActualHeight / 2));
            Assert.Equal(new WindowInteropHelper(window).Handle, GetAncestor(WindowFromPoint(new NativePoint { X = (int)point.X, Y = (int)point.Y }), 2));
            SetCursorPos((int)point.X, (int)point.Y); PumpInput(window);
            mouse_event(0x0002, 0, 0, 0, 0); PumpInput(window);
            mouse_event(0x0004, 0, 0, 0, 0); PumpInput(window);
        }
        finally
        {
            mouse_event(0x0004, 0, 0, 0, 0);
            window.Topmost = topmost; window.Left = left; window.Top = top;
            SetCursorPos(cursor.X, cursor.Y); SetForegroundWindow(foreground);
        }
    }
    [DllImport("user32.dll")] static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, uint x, uint y, uint data, nuint extraInfo);
    static void PumpInput(FrameworkElement element)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(100), System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            (_, _) => frame.Continue = false, element.Dispatcher);
        try { System.Windows.Threading.Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        element.UpdateLayout();
    }
    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T value) yield return value; foreach (var nested in Descendants<T>(child)) yield return nested; }
    }
    static void Pump(FrameworkElement element)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        element.Dispatcher.BeginInvoke(() => frame.Continue = false, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        element.UpdateLayout();
    }
    static void Save(FrameworkElement view, string? output, string name)
    {
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        var bitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(output, name + ".png")); encoder.Save(stream);
    }
}

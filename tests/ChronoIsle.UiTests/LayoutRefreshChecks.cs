using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using ChronoIsle.App.Services;
using ChronoIsle.App.Services.Persistence;
using ChronoIsle.App.Services.Productivity;
using ChronoIsle.App.Services.ImportExport;
using ChronoIsle.App.Views;

namespace ChronoIsle.UiTests;

// Shares the existing STA fixture. The markup previews do not initialize accounts, telemetry or notifications.
static class LayoutRefreshChecks
{
    public static void Verify(ThemeService theme, string? output)
    {
        CheckControls(theme);
        foreach (var mode in new[] { AppThemeMode.Light, AppThemeMode.Dark })
        {
            theme.Apply(mode);
            foreach (var (file, width, height) in new[]
            {
                ("LifeSettingsWindow", 560d, 520d), ("LifeSettingsWindow", 780d, 760d),
                ("LifeManagementWindow", 680d, 500d), ("LifeManagementWindow", 920d, 700d),
                ("TrayMenuWindow", 224d, 330d), ("IslandNotificationWindow", 388d, 270d)
            })
            {
                var window = LoadMarkup(file);
                try
                {
                    Show(window, width, height);
                    if (file == "IslandNotificationWindow")
                    {
                        ((TextBlock)window.FindName("NotificationAppName")).Text = "时屿 · 提醒";
                        ((TextBlock)window.FindName("NotificationTitle")).Text = "项目评审将在十分钟后开始";
                        ((TextBlock)window.FindName("NotificationBody")).Text = "请准备设计文档与待确认事项。长通知在有限空间内保持可读。";
                    }
                    Pump(window);
                    CheckButtonBounds(window);
                    Save(window, output, $"layout-{file}-{mode}-{width}");
                    if (file != "LifeSettingsWindow") continue;
                    var scroll = Descendants<ScrollViewer>(window).First(v => v.Content is StackPanel && v.ScrollableHeight > 0);
                    foreach (var section in new[] { "NotificationSection", "AppearanceSection", "DataSection" })
                    {
                        ((FrameworkElement)window.FindName(section)).BringIntoView();
                        Pump(window);
                        Assert.True(scroll.VerticalOffset > 0);
                        CheckButtonBounds(window);
                        Save(window, output, $"layout-settings-{section}-{mode}-{width}");
                    }
                }
                finally { window.Close(); }
            }
            var island = LoadMarkup("LifeIslandWindow");
            try
            {
                Show(island, 620, 820);
                ((FrameworkElement)island.FindName("ExpandedContent")).Visibility = Visibility.Visible;
                var scroll = (ScrollViewer)island.FindName("ExpandedScrollViewer");
                scroll.MaxHeight = 750;
                var panels = new[] { "CalendarPanel", "TelemetryPanel", "QuickAskPanel", "ToolsPanel" };
                foreach (var name in panels)
                {
                    foreach (var other in panels) ((FrameworkElement)island.FindName(other)).Visibility = other == name ? Visibility.Visible : Visibility.Collapsed;
                    scroll.ScrollToTop(); Pump(island);
                    CheckButtonBounds(island);
                    Save(island, output, $"layout-{name}-{mode}");
                    if (name != "ToolsPanel") continue;
                    foreach (var tool in new[] { "CaseConverterToolPanel", "NetworkSpeedTestToolPanel" })
                    {
                        foreach (var other in new[] { "NamingToolPanel", "CaseConverterToolPanel", "NetworkSpeedTestToolPanel" })
                            ((FrameworkElement)island.FindName(other)).Visibility = other == tool ? Visibility.Visible : Visibility.Collapsed;
                        Pump(island); CheckButtonBounds(island);
                        Save(island, output, $"layout-{tool}-{mode}");
                    }
                }
            }
            finally { island.Close(); }
            CheckDialog(output, mode);
            CheckCalendar(output, mode);
        }
        CheckManagement(theme, output);
    }

    static void CheckCalendar(string? output, AppThemeMode mode)
    {
        var calendar = new Calendar { SelectedDate = new DateTime(2026, 10, 2), DisplayDate = new DateTime(2026, 10, 2) };
        var host = new Window { Content = calendar };
        try
        {
            Show(host, 360, 350); Pump(host);
            Assert.Equal(42, Descendants<CalendarDayButton>(calendar).Count());
            Assert.Single(Descendants<CalendarDayButton>(calendar).Where(day => day.IsSelected));
            Save(calendar, output, $"layout-calendar-popup-{mode}");
            var item = Descendants<CalendarItem>(calendar).Single();
            var month = (Grid)item.Template.FindName("PART_MonthView", item);
            Assert.Equal(7, month.Children.OfType<FrameworkElement>().Count(child => Grid.GetRow(child) == 0 && child.ActualHeight > 0));
            ((Button)item.Template.FindName("PART_NextButton", item)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(host);
            Assert.Equal(11, calendar.DisplayDate.Month);
            ((Button)item.Template.FindName("PART_PreviousButton", item)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(host);
            Assert.Equal(10, calendar.DisplayDate.Month);
            ((Button)item.Template.FindName("PART_HeaderButton", item)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(host);
            Assert.Equal(CalendarMode.Year, calendar.DisplayMode);
            Assert.Equal(12, Descendants<CalendarButton>(calendar).Count(button => button.IsVisible));
            Assert.False(month.IsVisible);
            Save(calendar, output, $"layout-calendar-year-{mode}");
            ((Button)item.Template.FindName("PART_HeaderButton", item)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(host);
            Assert.Equal(CalendarMode.Decade, calendar.DisplayMode);
            Assert.Equal(12, Descendants<CalendarButton>(calendar).Count(button => button.IsVisible));
            calendar.DisplayMode = CalendarMode.Month;
            calendar.SelectedDate = new DateTime(2026, 10, 4); Pump(host);
            Assert.Equal("4", Descendants<CalendarDayButton>(calendar).Single(day => day.IsSelected).Content?.ToString());
        }
        finally { host.Close(); }
    }

    static void CheckManagement(ThemeService theme, string? output)
    {
        var folder = Path.Combine(Path.GetTempPath(), "chronoisle-layout", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "layout.db");
        var data = new LifeDataService(path);
        var connections = new SqliteConnectionFactory(path);
        var queue = new SqliteDbWriteQueue(connections);
        var attributes = new TaskAttributesService(connections, queue);
        for (var i = 0; i < 8; i++)
        {
            var item = data.Save(i == 0 ? new string('长', 80) : $"核对项目资料与执行计划 {i + 1}", "合成界面测试资料", DateTime.Today.AddDays(2), null);
            var current = attributes.Get(item.Id)!;
            attributes.Update(current with { EstimatedMinutes = 15, Category = "工作" });
        }
        data.Save("已完成事项 · 检查归档布局", "合成资料", DateTime.Today.AddDays(-1), null, completed: true);
        try
        {
            foreach (var mode in new[] { AppThemeMode.Light, AppThemeMode.Dark })
            {
                theme.Apply(mode);
                var window = new LifeManagementWindow(data, null!, new FocusService(queue, connections), attributes, new MarkdownItemTransferService(data));
                try
                {
                    Show(window, 680, 600);
                    foreach (var tab in new[] { "NowTab", "ActiveTab", "ArchiveTab" })
                    {
                        ((Button)window.FindName(tab)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(window);
                        CheckButtonBounds(window);
                        if (tab == "ActiveTab")
                        {
                            var more = Descendants<Button>(window).First(b => Equals(b.Content, "更多设置 ▾"));
                            more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(window);
                            var category = Descendants<ComboBox>(window).First(b => b.IsEditable);
                            Assert.True(category.IsVisible && category.ActualHeight >= 32);
                            CheckButtonBounds(window);
                        }
                        Save(window, output, $"layout-workspace-{tab}-{mode}");
                    }
                }
                finally { window.Close(); }
            }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(folder, true); }
    }

    static void CheckControls(ThemeService theme)
    {
        foreach (var mode in new[] { AppThemeMode.Light, AppThemeMode.Dark })
        foreach (var accent in Enum.GetValues<AppAccentScheme>())
        {
            theme.Apply(mode, accent);
            foreach (var background in new[] { "Brush.Window", "Brush.Card", "Brush.Surface" })
            foreach (var foreground in new[] { "Brush.TextPrimary", "Brush.TextSecondary", "Brush.TextTertiary" })
                Assert.True(Contrast(foreground, background) >= 4.5, $"{mode}/{accent}: {foreground} on {background}");
            Assert.True(Contrast("Brush.OnAccent", "Brush.Accent") >= 4.5, $"{mode}/{accent}: primary button contrast");
            Assert.True(Contrast("Brush.Danger", "Brush.DangerSoft") >= 4.5, $"{mode}: danger contrast");
        }
        theme.Apply(AppThemeMode.Light, AppAccentScheme.Emerald);
        var combo = new ComboBox { IsEditable = true, Text = "自定义分类", Width = 200, ItemsSource = new[] { "工作", "生活" } };
        var wide = new Border { Width = 1000, Height = 100 };
        var scroll = new ScrollViewer { Content = wide, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Height = 140 };
        var panel = new StackPanel(); panel.Children.Add(combo); panel.Children.Add(scroll);
        var host = new Window { Content = panel };
        try
        {
            Show(host, 340, 280); Pump(host);
            var text = Assert.IsType<TextBox>(combo.Template.FindName("PART_EditableTextBox", combo));
            Assert.True(text.IsVisible); Assert.Equal("自定义分类", text.Text);
            text.Text = "项目分类"; Pump(host); Assert.Equal("项目分类", combo.Text);
            var horizontal = Descendants<ScrollBar>(scroll).Single(b => b.Orientation == Orientation.Horizontal);
            Assert.True(horizontal.IsVisible && horizontal.ActualWidth > 200 && horizontal.ActualHeight <= 20, $"Horizontal scroll bar: visible={horizontal.IsVisible}, {horizontal.ActualWidth} × {horizontal.ActualHeight}");
            scroll.ScrollToHorizontalOffset(300); Pump(host); Assert.Equal(300, scroll.HorizontalOffset);
        }
        finally { host.Close(); }
    }

    static void CheckDialog(string? output, AppThemeMode mode)
    {
        var content = new StackPanel();
        for (var i = 0; i < 20; i++) content.Children.Add(new TextBox { Text = $"待安排任务 {i + 1}：核对项目资料和执行时间", Margin = new Thickness(0, 0, 0, 12) });
        var confirm = new Button { Content = "确认创建", MinWidth = 100, Style = (Style)Application.Current.FindResource("Button.Primary") };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(confirm);
        var factory = typeof(LifeMainWindow).Assembly.GetType("ChronoIsle.App.Views.DialogLayout")!.GetMethod("Create")!;
        var window = (Window)factory.Invoke(null, new object?[] { "安排任务", null, content, actions, 420d, 400d })!;
        try
        {
            Show(window, 420, 400); Pump(window);
            var viewport = Descendants<ScrollViewer>(window).First(v => ReferenceEquals(v.Content, content));
            Assert.True(viewport.ScrollableHeight > 400);
            var before = confirm.TranslatePoint(new Point(), window);
            viewport.ScrollToEnd(); Pump(window);
            Assert.Equal(before, confirm.TranslatePoint(new Point(), window));
            Assert.True(before.Y + confirm.ActualHeight < window.ActualHeight);
            Save(window, output, $"layout-dialog-{mode}");
        }
        finally { window.Close(); }
    }

    static double Contrast(string foreground, string background)
    {
        static double L(Color c)
        {
            static double Linear(byte b) => b / 255d <= .04045 ? b / 255d / 12.92 : Math.Pow((b / 255d + .055) / 1.055, 2.4);
            return .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
        }
        var a = L(((SolidColorBrush)Application.Current.FindResource(foreground)).Color);
        var b = L(((SolidColorBrush)Application.Current.FindResource(background)).Color);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }

    static Window LoadMarkup(string name)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "ChronoIsle.sln"))) root = root.Parent;
        var document = XDocument.Load(Path.Combine(root!.FullName, "src/ChronoIsle.App/Views", name + ".xaml"));
        // The compiled Markdown control already sets this read-only default in its constructor.
        foreach (var markdown in document.Descendants().Where(e => e.Name.LocalName == "MarkdownView")) markdown.Attribute("IsReadOnly")?.Remove();
        var events = new HashSet<string> { "Click", "Checked", "Unchecked", "TextChanged", "SizeChanged", "SelectionChanged", "DropDownOpened", "DropDownClosed", "MouseEnter", "MouseLeave", "MouseLeftButtonDown", "MouseMove", "MouseLeftButtonUp", "KeyDown", "Loaded", "Closing", "Closed" };
        foreach (var attribute in document.Descendants().Attributes().ToArray())
        {
            if (events.Contains(attribute.Name.LocalName) || attribute.Name.LocalName.StartsWith("PreviewMouse") || attribute.Name.LocalName == "Class") attribute.Remove();
        }
        var markup = document.ToString();
        foreach (var ns in new[] { "ChronoIsle.App", "ChronoIsle.App.Views", "ChronoIsle.App.Services.Domain" })
            markup = markup.Replace($"clr-namespace:{ns}\"", $"clr-namespace:{ns};assembly=ChronoIsle\"");
        return (Window)XamlReader.Parse(markup);
    }

    static void Show(Window window, double width, double height)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -32000; window.Top = -32000; window.ShowInTaskbar = false; window.ShowActivated = false; window.Topmost = false;
        window.SizeToContent = SizeToContent.Manual; window.Width = width; window.Height = height;
        window.Show(); Pump(window);
    }

    static void CheckButtonBounds(Window window)
    {
        foreach (var button in Descendants<Button>(window).Where(b => b.IsVisible && b.Content is string && b.ActualWidth > 0))
        {
            var point = button.TranslatePoint(new Point(), window);
            // Vertical overflow inside a ScrollViewer is intentional; horizontal overflow is not.
            Assert.True(point.X >= -1 && point.X + button.ActualWidth <= window.ActualWidth + 1, $"{window.Title}: {button.Content} overflows horizontally ({point.X}, {button.ActualWidth}/{window.ActualWidth})");
        }
    }

    static void Pump(FrameworkElement element)
    {
        element.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        element.UpdateLayout();
    }
    static void Save(FrameworkElement element, string? output, string name)
    {
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * 1.5), (int)Math.Ceiling(element.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(output, name + ".png")); encoder.Save(file);
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
}

using System.IO;
using System.Reflection;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ChronoIsle.App;
using ChronoIsle.App.Services;
using ChronoIsle.App.Services.Domain;
using ChronoIsle.App.Services.ImportExport;
using ChronoIsle.App.Services.Media;
using ChronoIsle.App.Services.Persistence;
using ChronoIsle.App.Services.Productivity;
using ChronoIsle.App.Services.Reporting;
using ChronoIsle.App.Services.State;
using ChronoIsle.App.Services.Sync;
using ChronoIsle.App.ViewModels;
using ChronoIsle.App.Views;
using Microsoft.Data.Sqlite;

namespace ChronoIsle.UiTests;

public sealed class TodayDashboardCardTests
{
    [Fact]
    public async Task Today_cards_keep_fold_state_and_navigate_without_hijacking_actions()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "island-today-cards", Guid.NewGuid().ToString("N"));
            System.Windows.Application? app = null;
            LifeIslandWindow? island = null;
            Window? host = null;
            try
            {
                Directory.CreateDirectory(directory);
                app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                foreach (var name in new[] { "DesignTokens", "Controls", "Components" })
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/ChronoIsle;component/Resources/{name}.xaml") });
                var database = Path.Combine(directory, "life.db");
                var data = new LifeDataService(database);
                var connections = new SqliteConnectionFactory(database);
                var queue = new SqliteDbWriteQueue(connections);
                var attributes = new TaskAttributesService(connections, queue);
                var next = data.Save("下一项工作", "", null, null);
                var overdue = data.Save("逾期资料", "", null, null);
                data.Save("待整理资料", "", null, null);
                data.Save("第二份待整理资料", "", null, null);
                data.Save("第三份待整理资料", "", null, null);
                foreach (var (id, due) in new[] { (next.Id, DateTimeOffset.UtcNow.AddHours(1)), (overdue.Id, DateTimeOffset.UtcNow.AddHours(-1)) })
                    queue.Execute(u =>
                    {
                        using var command = u.Connection.CreateCommand(); command.Transaction = u.Transaction;
                        command.CommandText = "UPDATE life_items SET due_utc_instant=$due,due_time_semantics='AbsoluteInstant' WHERE id=$id";
                        command.Parameters.AddWithValue("$due", due.ToString("O")); command.Parameters.AddWithValue("$id", id); command.ExecuteNonQuery();
                    });
                var focus = new FocusService(queue, connections);
                var preferences = new LifePreferencesService(directory);
                using var theme = new ThemeService(preferences);
                using var media = new MediaSessionService();
                using var audio = new AudioSpectrumService();
                using var telemetry = new SystemTelemetryService();
                // Keep the dormant sampler's disposal write inside this synthetic test directory.
                typeof(SystemTelemetryService).GetField("storagePath", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(telemetry, Path.Combine(directory, "traffic.json"));
                using var fps = new ForegroundFpsService();
                using var network = new NetworkSpeedTestService();
                using var reminders = new ReminderService(data, preferences, new WindowsNotificationService(), focus);
                var reports = new ReportService(queue);
                var assistant = new LifeViewModel(data, null!, null!, null!, null!);
                using var account = new CloudAccountClient(Path.Combine(directory, "account"), new AccountStub());
                CompleteRequest(() => account.LoginAsync("qa@example.test", "Synthetic-password1!"));
                var sourceStub = new SourceStub();
                using var source = new Day21HabitClient(account, data, sourceStub);
                CompleteRequest(() => source.RefreshAsync());
                island = new(data, reminders, new ChinaStatutoryHolidayCalendar(), new TodayDashboardService(data), focus,
                    reports, new IslandStateCoordinator(), attributes, preferences, theme, media, audio, telemetry, fps,
                    assistant, network, new NamingSuggestionService(null!, null!), source);
                Invoke(island, "InitializeTodayDashboard");
                island.EnableWeeklyReportHistory();
                Invoke(island, "ShowTodayDashboard");
                Assert.Null(island.FindName("Day21DashboardTab"));
                var root = (StackPanel)island.FindName("ExpandedContent");
                ((ScrollViewer)island.FindName("ExpandedScrollViewer")).Content = null;
                root.Visibility = Visibility.Visible;
                // Preview the real controls without starting the island's native hooks or background services.
                island.Close();
                host = new Window { Width = 620, Height = 780, Left = -32000, Top = -32000, ShowActivated = false,
                    ShowInTaskbar = false, Style = (Style)app.FindResource("Window.Display"),
                    Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
                host.SetResourceReference(Window.BackgroundProperty, "Brush.Island");
                host.Show(); Pump(host);
                var minute = (System.Windows.Controls.ComboBox)island.FindName("MinuteSelector");
                Invoke(island, "ShowCalendarDashboard"); Pump(host);
                minute.ApplyTemplate();
                Assert.NotNull(minute.Template.FindName("PART_Popup", minute));
                for (var value = 0; value < 60; value++) minute.Items.Add(value.ToString("00"));
                minute.SelectedIndex = 58;
                minute.IsDropDownOpen = true; Pump(host);
                var popup = (System.Windows.Controls.Primitives.Popup)minute.Template.FindName("PART_Popup", minute);
                Assert.True(popup.IsOpen);
                var popupScroll = Descendants<ScrollViewer>(popup.Child).Single();
                Assert.True(popupScroll.ScrollableHeight > 0);
                Assert.True(popupScroll.VerticalOffset > 0, "Opening the dropdown should reveal the selected minute.");
                minute.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(minute), 0, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
                Pump(host); Assert.False(minute.IsDropDownOpen);
                minute.IsDropDownOpen = true; Pump(host);
                foreach (var key in new[] { Key.Down, Key.Enter })
                    minute.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(minute), 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
                Pump(host); Assert.False(minute.IsDropDownOpen); Assert.Equal(59, minute.SelectedIndex);
                Invoke(island, "ShowTodayDashboard"); Pump(host);
                var cardIds = Descendants<Border>(root).Select(AutomationProperties.GetAutomationId).Where(id => id.StartsWith("TodayCard-", StringComparison.Ordinal)).ToArray();
                Assert.Equal(4, cardIds.Length);
                Assert.Equal(new[] { "TodayCard-workbench", "TodayCard-conversations", "TodayCard-day21", "TodayCard-reports" }, cardIds);
                Assert.True(root.ActualHeight < 820, $"Fully expanded content is too tall: {root.ActualHeight}");
                Assert.Contains("TodayCard-day21", cardIds);
                var day21 = (Day21HabitPanel)island.FindName("Day21SourcePanel");
                Assert.True(day21.IsVisible);
                Assert.True(Card(root, "day21").IsAncestorOf(day21));
                var reportNavigation = 0; var day21Navigation = 0; var recommendations = 0; var chatNavigation = 0; var workbenchNavigation = 0;
                ItemNavigationTarget? selectedItem = null;
                island.ReportsRequested += (_, _) => reportNavigation++;
                island.Day21Requested += (_, _) => day21Navigation++;
                island.RecommendationsRequested += (_, _) => recommendations++;
                island.OpenRequested += (_, _) => chatNavigation++;
                island.ManageRequested += (_, _) => workbenchNavigation++;
                island.ItemDetailsRequested += (_, target) => selectedItem = target;
                Save(root, "today-default");
                foreach (var id in cardIds)
                    if (Body(root, id[10..]).Visibility == Visibility.Collapsed) Click(root, "TodayCardToggle-" + id[10..]);
                foreach (var mode in new[] { AppThemeMode.Dark, AppThemeMode.Light })
                {
                    theme.Apply(mode); Pump(host); Save(root, $"today-{mode}-expanded");
                    foreach (var card in Descendants<Border>(root).Where(b => AutomationProperties.GetAutomationId(b).StartsWith("TodayCard-", StringComparison.Ordinal)))
                        foreach (var button in Descendants<Button>(card).Where(b => AutomationProperties.GetAutomationId(b).StartsWith("TodayCardNavigate-", StringComparison.Ordinal) || AutomationProperties.GetAutomationId(b).StartsWith("TodayCardToggle-", StringComparison.Ordinal)))
                        {
                            var bounds = button.TransformToAncestor(card).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
                            Assert.True(bounds.Left >= 0 && bounds.Right <= card.ActualWidth + 1, $"Header button exceeds card: {AutomationProperties.GetAutomationId(button)}");
                        }
                    foreach (var id in cardIds) Click(root, "TodayCardToggle-" + id[10..]);
                    Pump(host);
                    foreach (var id in cardIds) Assert.Equal(Visibility.Collapsed, Body(root, id[10..]).Visibility);
                    Invoke(island, "BuildTodayDashboard");
                    Invoke(island, "ShowCalendarDashboard"); Assert.False(day21.IsVisible);
                    Invoke(island, "ShowTodayDashboard"); Pump(host);
                    foreach (var id in cardIds) Assert.Equal(Visibility.Collapsed, Body(root, id[10..]).Visibility);
                    Save(root, $"today-{mode}-collapsed");
                    Click(root, "TodayCardNavigate-reports");
                    Assert.Equal(Visibility.Collapsed, Body(root, "reports").Visibility);
                    foreach (var id in cardIds) Click(root, "TodayCardToggle-" + id[10..]);
                    Pump(host);
                    foreach (var id in cardIds) Assert.Equal(Visibility.Visible, Body(root, id[10..]).Visibility);
                }
                Assert.Equal(2, reportNavigation);
                Click(root, "TodayCardNavigate-weekly-history"); Assert.Equal(3, reportNavigation);
                Click(root, "TodayCardNavigate-day21"); Assert.Equal(1, day21Navigation);
                Click(root, "TodayCardNavigate-workbench"); Assert.Equal(1, workbenchNavigation);
                Click(root, "TodayCardNavigate-suggestions"); Assert.Equal(1, recommendations);
                Click(root, "TodayCardNavigate-overview-下一行动"); Assert.Equal(next.Id, selectedItem?.Id);
                Click(root, "TodayCardNavigate-overview-逾期事项"); Assert.Equal(overdue.Id, selectedItem?.Id);
                Click(root, "TodayInboxToggle"); Pump(host);
                Assert.True(Descendants<TextBox>(Card(root, "workbench")).All(box => box.IsVisible));
                Invoke(island, "BuildTodayDashboard"); Pump(host);
                Assert.True(Descendants<TextBox>(Card(root, "workbench")).All(box => box.IsVisible));
                foreach (var (key, title) in new[] { ("count-今日", "今日详情"), ("count-逾期", "逾期详情"), ("count-待整理", "待整理详情"), ("inbox", "待整理详情") })
                {
                    var opened = "";
                    Dispatcher.CurrentDispatcher.BeginInvoke(() =>
                    {
                        var dialog = app.Windows.OfType<Window>().Single(w => w.Title == title);
                        opened = dialog.Title; dialog.Close();
                    }, DispatcherPriority.Loaded);
                    Click(root, "TodayCardNavigate-" + key); Assert.Equal(title, opened);
                }
                var input = Descendants<TextBox>(Card(root, "conversations")).Single();
                input.Text = "记录一个新事项";
                input.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                Assert.Equal(0, chatNavigation);
                var submitted = ""; island.ChatRequested += (_, text) => submitted = text;
                Descendants<Button>(Card(root, "conversations")).Single(b => Equals(b.Content, "发送")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("记录一个新事项", submitted); Assert.Equal(0, chatNavigation);
                Click(root, "TodayCardNavigate-conversations"); Assert.Equal(1, chatNavigation);
                Descendants<Button>(Card(root, "reports")).Single(b => Equals(b.Content, "生成下周计划")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True((bool)typeof(LifeIslandWindow).GetField("nextWeekPlanVisible", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(island)!);
                Assert.Equal(3, reportNavigation);
                Card(root, "reports").RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                Assert.Equal(3, reportNavigation);
                // Exercise the real island viewport and height updates, not only a detached preview.
                var live = new LifeIslandWindow(data, reminders, new ChinaStatutoryHolidayCalendar(), new TodayDashboardService(data), focus,
                    reports, new IslandStateCoordinator(), attributes, preferences, theme, media, audio, telemetry, fps,
                    assistant, network, new NamingSuggestionService(null!, null!), source);
                try
                {
                    typeof(LifeIslandWindow).GetField("weatherEnabled", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(live, true);
                    live.Topmost = false; live.Show();
                    live.Left = SystemParameters.WorkArea.Left + 50; live.Top = SystemParameters.WorkArea.Top + 60;
                    live.OpenTodayPanel();
                    ((Button)live.FindName("ExpandedPinButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    foreach (var timer in new[] { "clockTimer", "focusTimer", "collapseTimer" })
                        ((DispatcherTimer)typeof(LifeIslandWindow).GetField(timer, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(live)!).Stop();
                    live.EnableWeeklyReportHistory();
                    PumpFor(live, TimeSpan.FromMilliseconds(350));
                    theme.Apply(AppThemeMode.Dark); Pump(live); Save(live, "window-Dark-default");
                    theme.Apply(AppThemeMode.Light); Pump(live); Save(live, "window-Light-default");
                    var viewport = (ScrollViewer)live.FindName("ExpandedScrollViewer");
                    var liveRoot = (StackPanel)live.FindName("ExpandedContent");
                    var draft = Descendants<TextBox>(Card(liveRoot, "conversations")).Single();
                    draft.Focus(); draft.Text = "保留正在输入的草稿"; Invoke(live, "Refresh"); Pump(live);
                    Assert.Same(draft, Descendants<TextBox>(Card(liveRoot, "conversations")).Single());
                    Assert.Equal("保留正在输入的草稿", draft.Text); draft.Clear(); Keyboard.ClearFocus();
                    focus.Start(next.Id, 25); Invoke(live, "BuildTodayDashboard"); Pump(live);
                    var pause = Descendants<Button>(Card(liveRoot, "workbench")).Single(b => Equals(b.Content, "暂停专注"));
                    pause.Focus(); pause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(live);
                    Assert.True(focus.RestoreActive()!.IsPaused);
                    Assert.Contains(Descendants<Button>(Card(liveRoot, "workbench")), b => Equals(b.Content, "继续专注"));
                    var end = Descendants<Button>(Card(liveRoot, "workbench")).Single(b => Equals(b.Content, "结束专注"));
                    end.Focus(); end.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(live);
                    Assert.Null(focus.RestoreActive());
                    Descendants<Button>(Card(liveRoot, "workbench")).Single(b => Equals(b.Content, "保留未完成")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(live);
                    Assert.InRange(live.ActualWidth, 619, 621);
                    var liveCards = Descendants<Border>(liveRoot).Select(AutomationProperties.GetAutomationId).Where(id => id.StartsWith("TodayCard-", StringComparison.Ordinal)).ToArray();
                    Assert.Equal(4, liveCards.Length);
                    foreach (var id in liveCards)
                        if (Body(liveRoot, id[10..]).Visibility == Visibility.Collapsed) Click(liveRoot, "TodayCardToggle-" + id[10..]);
                    Pump(live);
                    var expandedHeight = viewport.ExtentHeight;
                    Assert.True(viewport.ActualHeight > 100);
                    viewport.ScrollToEnd(); Pump(live);
                    Assert.Equal(viewport.ScrollableHeight, viewport.VerticalOffset, 1);
                    foreach (var id in liveCards) Click(liveRoot, "TodayCardToggle-" + id[10..]);
                    Pump(live);
                    Assert.True(viewport.ExtentHeight < expandedHeight - 200);
                    Assert.InRange(viewport.VerticalOffset, 0, viewport.ScrollableHeight + 1);
                    foreach (var id in liveCards) Click(liveRoot, "TodayCardToggle-" + id[10..]);
                    Pump(live);
                    Assert.Equal(expandedHeight, viewport.ExtentHeight, 1);
                    Assert.True(viewport.ActualHeight <= SystemParameters.WorkArea.Height);
                    sourceStub.CardCount = 8;
                    CompleteRequest(() => source.RefreshAsync());
                    var liveSource = (Day21HabitPanel)live.FindName("Day21SourcePanel"); liveSource.Render(); Pump(live);
                    var sourceScroll = (ScrollViewer)Body(liveRoot, "day21").Child;
                    Assert.True(sourceScroll.ScrollableHeight > 0);
                    Assert.InRange(sourceScroll.ActualHeight, 0, 221);
                    Assert.True(viewport.ExtentHeight < 900, $"Large data should remain bounded: {viewport.ExtentHeight}");
                    sourceScroll.ScrollToEnd(); Pump(live);
                    Assert.Equal(sourceScroll.ScrollableHeight, sourceScroll.VerticalOffset, 1);
                    viewport.Height = 450; viewport.ScrollToTop(); Pump(live);
                    Assert.True(viewport.ScrollableHeight > 0, "The constrained viewport must scroll for this check.");
                    sourceScroll.ScrollToEnd(); Pump(live);
                    sourceScroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent }); Pump(live);
                    Assert.True(viewport.VerticalOffset > 0, "Scrolling past the card edge should continue down the page.");
                    viewport.Height = double.NaN; Invoke(live, "ResizeExpandedToContent"); Pump(live);
                    Save(live, "window-many-habits");
                }
                finally
                {
                    live.Close();
                    foreach (var name in new[] { "weeklyReportTimer", "weeklyHistoryTimer" })
                        (typeof(LifeIslandWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(live) as DispatcherTimer)?.Stop();
                }
                using var managementScope = new ManagementScope(data, reminders, focus, attributes);
                managementScope.Window.OpenDay21Sources(); managementScope.Window.Show(); Pump(managementScope.Window);
                Assert.Equal(Visibility.Visible, ((ScrollViewer)managementScope.Window.FindName("Day21Scroller")).Visibility);
                managementScope.Window.OpenRecommendations(); Pump(managementScope.Window);
                Assert.Equal(Visibility.Collapsed, ((ScrollViewer)managementScope.Window.FindName("Day21Scroller")).Visibility);
                Assert.Equal(Visibility.Visible, ((FrameworkElement)managementScope.Window.FindName("RecommendationSummary")).Visibility);
            }
            catch (Exception e) { completion.TrySetException(e); }
            finally
            {
                host?.Close(); island?.Close();
                if (island is not null)
                    foreach (var name in new[] { "weeklyReportTimer", "weeklyHistoryTimer" })
                        (typeof(LifeIslandWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(island) as DispatcherTimer)?.Stop();
                app?.Shutdown(); SqliteConnection.ClearAllPools();
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
            completion.TrySetResult();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }
    static void Invoke(LifeIslandWindow island, string method) => typeof(LifeIslandWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(island, null);
    static Border Card(DependencyObject root, string key) => Descendants<Border>(root).Single(b => AutomationProperties.GetAutomationId(b) == "TodayCard-" + key);
    static Border Body(DependencyObject root, string key) => Descendants<Border>(root).Single(b => AutomationProperties.GetAutomationId(b) == "TodayCardBody-" + key);
    static void Click(DependencyObject root, string id) => Descendants<Button>(root).Single(b => AutomationProperties.GetAutomationId(b) == id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    static void Pump(Window host) { host.UpdateLayout(); Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout(); }
    static void PumpFor(Window host, TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration }; timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); Pump(host);
    }
    static void CompleteRequest(Func<Task> operation)
    {
        var pending = Task.Run(operation);
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!pending.IsCompleted && DateTime.UtcNow < deadline)
        { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); Thread.Sleep(5); }
        Assert.True(pending.IsCompletedSuccessfully, pending.Exception?.ToString() ?? "Synthetic request timed out.");
    }
    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match; foreach (var item in Descendants<T>(child)) yield return item; }
    }
    static void Save(FrameworkElement root, string name)
    {
        var output = Environment.GetEnvironmentVariable("CHRONOISLE_TODAY_CARDS_QA_DIR"); if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var oldBackground = (root as Panel)?.ReadLocalValue(Panel.BackgroundProperty);
        if (root is Panel panel) panel.SetResourceReference(Panel.BackgroundProperty, "Brush.Island");
        var visual = new DrawingVisual(); using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        bitmap.Render(visual); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(output, name + ".png")); png.Save(stream);
        if (root is Panel restored) { if (oldBackground == DependencyProperty.UnsetValue) restored.ClearValue(Panel.BackgroundProperty); else restored.SetValue(Panel.BackgroundProperty, oldBackground); }
    }
    sealed class ManagementScope : IDisposable
    {
        public LifeManagementWindow Window { get; }
        public ManagementScope(LifeDataService data, ReminderService reminders, FocusService focus, TaskAttributesService attributes)
        { Window = new(data, reminders, focus, attributes, new MarkdownItemTransferService(data)) { Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false }; }
        public void Dispose() => Window.Close();
    }
    sealed class AccountStub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new CloudTokens("synthetic-access", "synthetic-refresh", 900, Guid.Parse("38e6c435-23fd-4cc9-a0a7-abd927a54d21"))) });
    }
    sealed class SourceStub : HttpMessageHandler
    {
        public int CardCount { get; set; } = 1;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var day = DateTime.Today.ToString("yyyy-MM-dd");
            var cards = Enumerable.Range(1, CardCount).Select(index =>
            {
                var id = $"00000000-0000-0000-0000-{index:D12}";
                var entry = JsonSerializer.SerializeToElement(new { habitId = id, date = day, value = 2, note = "", recordedAt = DateTimeOffset.Now.ToUnixTimeMilliseconds(), remainderSeconds = 0, sessions = Array.Empty<object>() });
                var data = JsonSerializer.SerializeToElement(new { plan = new { id, name = "戒烟", input = "COUNT", mode = "AT_LEAST", start = day, unit = "支", archivedOn = (string?)null }, entries = new Dictionary<string, JsonElement> { [day] = entry }, timer = (object?)null });
                return new Day21HabitCard("21day", id, 1, day, index == 1 ? "戒烟" : $"习惯 {index}", "支", "COUNT", true, 10, entry, null, data, ["count", "set_total", "archive"]);
            }).ToArray();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { sourceProject = "21day", cursor = 1, cards }) });
        }
    }
}

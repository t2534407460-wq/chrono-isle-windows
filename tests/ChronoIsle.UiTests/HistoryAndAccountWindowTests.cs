using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ChronoIsle.App.Services;
using ChronoIsle.App.Services.Commanding;
using ChronoIsle.App.Services.Knowledge;
using ChronoIsle.App.Services.Persistence;
using ChronoIsle.App.Services.Productivity;
using ChronoIsle.App.Services.ImportExport;
using ChronoIsle.App.Services.Reporting;
using ChronoIsle.App.Services.Sync;
using ChronoIsle.App.Views;
using Microsoft.Data.Sqlite;

namespace ChronoIsle.UiTests;

public sealed class HistoryAndAccountWindowTests
{
    [Fact]
    public async Task NonemptyHistorySnapshotAndVerifiedRegistrationWorkThroughTheWindows()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), "island-history-account-ui", Guid.NewGuid().ToString("N"));
            System.Windows.Application? app = null;
            HistoryReportWindow? history = null;
            CloudAccountWindow? account = null;
            try
            {
                Directory.CreateDirectory(path);
                app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                foreach (var name in new[] { "DesignTokens", "Controls", "Components" })
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/ChronoIsle;component/Resources/{name}.xaml") });
                var database = Path.Combine(path, "life.db");
                var data = new LifeDataService(database);
                var queue = new SqliteDbWriteQueue(new SqliteConnectionFactory(database));
                queue.Execute(u =>
                {
                    using var command = u.Connection.CreateCommand(); command.Transaction = u.Transaction;
                    command.CommandText = "INSERT INTO life_items(id,kind,title,status,created_at,updated_at,completed_at_utc) VALUES('ui-history','Todo','阅读设计资料','Completed','2026-10-01T01:00:00Z','2026-10-02T01:00:00Z','2026-10-02T01:00:00Z')";
                    command.ExecuteNonQuery();
                });
                var reports = new HistoryReportService(queue);
                history = new(reports) { Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
                history.Show(); Pump();
                Assert.Single(((DataGrid)history.FindName("Records")).Items);
                ButtonNamed(history, "保存快照").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Single(reports.Snapshots());
                var start = (DatePicker)history.FindName("StartDate"); var end = (DatePicker)history.FindName("EndDate");
                start.SelectedDate = new(2026, 10, 3); end.SelectedDate = new(2026, 10, 1);
                ButtonNamed(history, "查询").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Contains("开始日期不能晚于结束日期", ((TextBlock)history.FindName("Notice")).Text);
                ButtonNamed(history, "全部历史").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                ((ComboBox)history.FindName("SnapshotPicker")).SelectedIndex = 0;
                Assert.Single(((DataGrid)history.FindName("Records")).Items);

                using var client = new CloudAccountClient(Path.Combine(path, "account"), new IdentityStub());
                var preferences = new LifePreferencesService(path);
                using var theme = new ThemeService(preferences);
                using var sync = new CloudSyncService(client, data, preferences, new SyncStub());
                VerifySettingsEntry(data, client, sync, theme, preferences, path);

                account = new(client, sync) { Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
                account.Show(); Pump();
                VerifyChrome(account, "Account"); VerifyChrome(history, "History");
                ((Button)account.FindName("Submit")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Contains("邮箱", ((TextBlock)account.FindName("Message")).Text);
                ((Button)account.FindName("RegisterTab")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                foreach (var mode in new[] { AppThemeMode.Dark, AppThemeMode.Light }) { theme.Apply(mode); Pump(); Save(account, $"register-{mode}"); }
                ((TextBox)account.FindName("Email")).Text = "qa@example.test";
                ((PasswordBox)account.FindName("Password")).Password = "Verification-test1!";
                ((Button)account.FindName("Submit")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => ((ProgressBar)account.FindName("BusyProgress")).Visibility == Visibility.Collapsed);
                Assert.Equal(Visibility.Visible, ((StackPanel)account.FindName("Verification")).Visibility);
                Assert.Equal(Visibility.Collapsed, ((StackPanel)account.FindName("Credentials")).Visibility);
                Assert.Empty(((PasswordBox)account.FindName("Password")).Password);
                Save(account, "verification-Light");
                ((TextBox)account.FindName("Code")).Text = "123456";
                ((Button)account.FindName("Verify")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => ((ProgressBar)account.FindName("BusyProgress")).Visibility == Visibility.Collapsed);
                Assert.Equal("qa@example.test", client.Account?.Email);
                Assert.Contains("已登录", ((TextBlock)account.FindName("AccountStatus")).Text);
                Assert.Equal(Visibility.Collapsed, ((StackPanel)account.FindName("Verification")).Visibility);
                Assert.Equal(Visibility.Collapsed, ((StackPanel)account.FindName("AuthPanel")).Visibility);
                Assert.Equal(Visibility.Visible, ((StackPanel)account.FindName("ProfilePanel")).Visibility);
                ((Button)account.FindName("SyncNow")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => ((ProgressBar)account.FindName("BusyProgress")).Visibility == Visibility.Collapsed);
                Assert.Contains("同步完成", ((TextBlock)account.FindName("SyncStatus")).Text); Assert.NotNull(sync.LastSuccess);
                VerifySettingsEntry(data, client, sync, theme, preferences, path);
                VerifyDay21Sources(data, client, theme, preferences, queue, history, database);

                foreach (var mode in new[] { AppThemeMode.Dark, AppThemeMode.Light })
                {
                    theme.Apply(mode); Pump(); Save(history, $"history-{mode}-table");
                    Descendants<TabControl>(history).Single().SelectedIndex = 1; Pump(); Save(history, $"history-{mode}-trend");
                    Descendants<TabControl>(history).Single().SelectedIndex = 0;
                    Save(account, $"account-{mode}");
                }
                ((Button)account.FindName("Logout")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => ((ProgressBar)account.FindName("BusyProgress")).Visibility == Visibility.Collapsed);
                Assert.Null(client.Account); Assert.Equal(Visibility.Visible, ((StackPanel)account.FindName("AuthPanel")).Visibility);
                foreach (var mode in new[] { AppThemeMode.Dark, AppThemeMode.Light })
                {
                    theme.Apply(mode); account.Width = 440; account.Height = 560; Pump(); Save(account, $"login-{mode}-440");
                }
                ((Button)account.FindName("ForgotPassword")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("新密码", ((TextBlock)account.FindName("PasswordLabel")).Text);
                ((PasswordBox)account.FindName("Password")).Password = "Reset-test1!";
                ((Button)account.FindName("Submit")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => ((ProgressBar)account.FindName("BusyProgress")).Visibility == Visibility.Collapsed);
                ((TextBox)account.FindName("Code")).Text = "123456";
                ((Button)account.FindName("Verify")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => ((ProgressBar)account.FindName("BusyProgress")).Visibility == Visibility.Collapsed);
                Assert.Equal("欢迎回到时屿", ((TextBlock)account.FindName("AuthHeading")).Text);
                Assert.Contains("密码已重置", ((TextBlock)account.FindName("Message")).Text);
                completion.SetResult();
            }
            catch (Exception error) { completion.SetException(error); }
            finally
            {
                account?.Close(); history?.Close(); app?.Shutdown(); SqliteConnection.ClearAllPools();
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }
    static void VerifySettingsEntry(LifeDataService data, CloudAccountClient client, CloudSyncService sync, ThemeService theme, LifePreferencesService preferences, string path)
    {
        using var reminders = new ReminderService(data, preferences, new WindowsNotificationService());
        var settings = new LifeSettingsWindow(new ProviderSettingsService(), preferences, reminders, new OpenAiChatService(),
            new AutoStartService(null!, null!, () => null), data, new AssistantCommandPipeline(Path.Combine(path, "life.db")), theme, client, sync,
            knowledgeSettings: new KnowledgeBaseSettingsService(path))
        { Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        try
        {
            ((TextBox)settings.FindName("Url")).Text = "https://api.example.test";
            ((TextBox)settings.FindName("Model")).Text = "test-model";
            ((PasswordBox)settings.FindName("Key")).Clear();
            settings.Show(); Pump();
            var login = (Button)settings.FindName("AccountButton");
            foreach (var mode in new[] { AppThemeMode.Dark, AppThemeMode.Light })
            foreach (var (width, height) in new[] { (560d, 520d), (780d, 760d) })
            {
                theme.Apply(mode); settings.Width = width; settings.Height = height; Pump();
                var bounds = login.TransformToAncestor(settings).TransformBounds(new Rect(login.RenderSize));
                Assert.True(login.IsVisible && login.IsEnabled);
                Assert.True(bounds.Top >= 0 && bounds.Bottom <= settings.ActualHeight && bounds.Left >= 0 && bounds.Right <= settings.ActualWidth);
                Save(settings, $"settings-avatar-{(client.Account is null ? "guest" : "signed-in")}-{mode}-{width}");
            }
            Exception? failure = null;
            settings.Dispatcher.BeginInvoke(() =>
            {
                CloudAccountWindow? opened = null;
                try
                {
                    opened = Application.Current.Windows.OfType<CloudAccountWindow>().Single(w => ReferenceEquals(w.Owner, settings));
                    Assert.Same(settings, opened.Owner); Assert.True(opened.IsVisible);
                    Assert.Equal(client.Account is null ? Visibility.Visible : Visibility.Collapsed, ((StackPanel)opened.FindName("AuthPanel")).Visibility);
                    Assert.Equal(client.Account is null ? "登录你的账号" : client.Account.Email, ((TextBlock)settings.FindName("AccountName")).Text);
                    VerifyChrome(opened, "Account");
                }
                catch (Exception error) { failure = error; }
                finally { opened?.Close(); }
            }, DispatcherPriority.ApplicationIdle);
            login.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (failure is not null) throw failure;
            Assert.True(settings.IsVisible);
        }
        finally { settings.Close(); }
    }
    static void VerifyDay21Sources(LifeDataService data, CloudAccountClient account, ThemeService theme, LifePreferencesService preferences, SqliteDbWriteQueue queue, HistoryReportWindow history, string database)
    {
        var before = data.ManagedItems().Count;
        var server = new Day21Stub(); using var sources = new Day21HabitClient(account, data, server, () => server.Now, "Asia/Shanghai");
        var panel = new Day21HabitPanel(); panel.Initialize(sources);
        var host = new Window { Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, Width = 440, Height = 720,
            Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        host.SetResourceReference(Window.BackgroundProperty, "Brush.Surface");
        using var reminders = new ReminderService(data, preferences, new WindowsNotificationService());
        var connections = new SqliteConnectionFactory(database);
        var management = new LifeManagementWindow(data, reminders, new FocusService(queue, connections), new TaskAttributesService(connections, queue), new MarkdownItemTransferService(data), sources)
        { Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        try
        {
            host.Show(); PumpUntil(() => sources.LastFetched is not null && ((ProgressBar)panel.FindName("SourceBusy")).Visibility == Visibility.Collapsed);
            Assert.Equal(3, sources.Cached().Count); Assert.Contains(Descendants<TextBlock>(panel), t => t.Text.Contains("今日尚未记录"));
            Assert.Contains(Descendants<TextBlock>(panel), t => t.Text.Contains("已记录 0"));
            Assert.Empty(Descendants<Button>(panel).Where(b => Equals(b.Content, "完成计时") || Equals(b.Content, "取消计时")));
            Assert.Single(Descendants<Button>(panel).Where(b => Equals(b.Content, "接管计时")));
            ButtonNamed(panel, "+1").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => ((ProgressBar)panel.FindName("SourceBusy")).Visibility == Visibility.Collapsed);
            Assert.Equal(1, sources.Cached().Single(c => c.Input == "COUNT").Value);
            var countCard = Descendants<Border>(panel).Single(b => b.Child is StackPanel p && Descendants<TextBlock>(p).Any(t => t.Text == "阅读次数"));
            ButtonNamed(countCard, "修改今日总量").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var total = Descendants<TextBox>(countCard).Single(t => System.Windows.Automation.AutomationProperties.GetName(t) == "当日总量"); total.Text = "不是数字";
            ButtonNamed(countCard, "确认总量校正").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Contains("整数总量", ((TextBlock)panel.FindName("SourceMessage")).Text); Assert.Equal("不是数字", total.Text);
            total.Text = "0"; ButtonNamed(countCard, "确认总量校正").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => ((ProgressBar)panel.FindName("SourceBusy")).Visibility == Visibility.Collapsed);
            Assert.Equal(0, sources.Cached().Single(c => c.Input == "COUNT").Value); Assert.Equal(before, data.ManagedItems().Count);
            foreach (var mode in new[] { AppThemeMode.Dark, AppThemeMode.Light }) { theme.Apply(mode); Pump(); Save(host, $"day21-source-{mode}-440"); }
            management.Show(); Pump(); ButtonNamed(management, "21day 打卡").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Assert.Equal(Visibility.Visible, ((ScrollViewer)management.FindName("Day21Scroller")).Visibility);
            Assert.Equal(Visibility.Collapsed, ((WrapPanel)management.FindName("LocalActions")).Visibility);
            Assert.Equal(Visibility.Collapsed, ((ScrollViewer)management.FindName("ItemsScroller")).Visibility);
            Assert.NotEqual(((Button)management.FindName("ActiveTab")).Background, ButtonNamed(management, "21day 打卡").Background);
            foreach (var mode in new[] { AppThemeMode.Dark, AppThemeMode.Light }) { theme.Apply(mode); management.Width = 680; management.Height = 500; Pump(); Save(management, $"day21-workbench-{mode}-680"); }
            ((Button)management.FindName("ActiveTab")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Assert.Equal(Visibility.Collapsed, ((ScrollViewer)management.FindName("Day21Scroller")).Visibility);
            Assert.Equal(Visibility.Visible, ((WrapPanel)management.FindName("LocalActions")).Visibility);
            var sourceHistory = (Day21HabitPanel)history.FindName("Day21History"); sourceHistory.Initialize(sources);
            Descendants<TabControl>(history).Single().SelectedIndex = 2; PumpUntil(() => ((ProgressBar)sourceHistory.FindName("SourceBusy")).Visibility == Visibility.Collapsed);
            Assert.Equal(Visibility.Collapsed, ((WrapPanel)history.FindName("LocalReportFilters")).Visibility);
            Assert.Equal(Visibility.Collapsed, ((Grid)history.FindName("LocalReportFooter")).Visibility);
            Assert.Empty(Descendants<Button>(sourceHistory).Where(b => Equals(b.Content, "+1") || Equals(b.Content, "接管计时")));
            foreach (var mode in new[] { AppThemeMode.Dark, AppThemeMode.Light }) { theme.Apply(mode); Pump(); Save(history, $"day21-history-{mode}"); }
            Descendants<TabControl>(history).Single().SelectedIndex = 0;
            Assert.Equal(Visibility.Visible, ((WrapPanel)history.FindName("LocalReportFilters")).Visibility);
            Assert.Equal(Visibility.Visible, ((Grid)history.FindName("LocalReportFooter")).Visibility);
        }
        finally { host.Close(); management.Close(); }
    }
    static void VerifyChrome(Window window, string prefix)
    {
        Assert.Equal(WindowStyle.None, window.WindowStyle); Assert.True(window.AllowsTransparency);
        Assert.Equal(ResizeMode.CanResize, window.ResizeMode);
        Assert.NotNull(System.Windows.Shell.WindowChrome.GetWindowChrome(window));
        foreach (var action in new[] { "TitleBar", "Minimize", "Maximize", "Close" })
            Assert.Single(Descendants<FrameworkElement>(window).Where(e => System.Windows.Automation.AutomationProperties.GetAutomationId(e) == prefix + action));
        var maximize = Descendants<Button>(window).Single(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == prefix + "Maximize");
        maximize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(WindowState.Maximized, window.WindowState);
        maximize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(WindowState.Normal, window.WindowState); Pump();
    }
    static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    static void PumpUntil(Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(8);
        while (!done() && DateTime.UtcNow < until) { Pump(); Thread.Sleep(10); }
        Assert.True(done(), "The account operation did not complete."); Pump();
    }
    static Button ButtonNamed(DependencyObject root, string name) => Descendants<Button>(root).Single(b => Equals(b.Content, name));
    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match; foreach (var item in Descendants<T>(child)) yield return item; }
    }
    static void Save(Window window, string name)
    {
        var output = Environment.GetEnvironmentVariable("CHRONOISLE_ACCOUNT_HISTORY_QA_DIR"); if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output); window.UpdateLayout();
        var root = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual(); using (var drawing = visual.RenderOpen())
        { drawing.DrawRectangle(window.Background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight)); drawing.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight)); }
        bitmap.Render(visual); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(output, name + ".png")); png.Save(stream);
    }
    sealed class IdentityStub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.RequestUri!.AbsolutePath.EndsWith("/register", StringComparison.Ordinal) || request.RequestUri.AbsolutePath.EndsWith("/reset", StringComparison.Ordinal)
                ? "{\"challengeId\":\"f2b3848b-6b9e-4328-af1e-390e63bfb7d5\",\"expiresAt\":\"2099-10-05T10:00:00Z\"}"
                : "{\"accessToken\":\"synthetic-access\",\"refreshToken\":\"synthetic-refresh\",\"expiresIn\":900,\"userId\":\"38e6c435-23fd-4cc9-a0a7-abd927a54d21\"}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
    sealed class SyncStub : HttpMessageHandler
    {
        readonly List<JsonObject> changes = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post)
            {
                var operations = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsArray(); var result = new JsonArray();
                foreach (var op in operations)
                {
                    var revision = changes.Count + 1;
                    changes.Add(new JsonObject { ["sequence"] = revision, ["entity"] = new JsonObject {
                        ["entityType"] = op!["entityType"]!.DeepClone(), ["entityId"] = op["entityId"]!.DeepClone(), ["revision"] = revision,
                        ["schemaVersion"] = 1, ["data"] = op["data"]?.DeepClone(), ["deleted"] = op["deleted"]!.DeepClone() } });
                    result.Add(new JsonObject { ["operationId"] = op["operationId"]!.DeepClone(), ["status"] = "applied", ["revision"] = revision });
                }
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(result) };
            }
            var after = long.Parse(request.RequestUri!.Query.Split('&')[0].Split('=')[1]);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { cursor = changes.Count, highWatermark = changes.Count,
                changes = changes.Where(c => c["sequence"]!.GetValue<int>() > after) }) };
        }
    }
    sealed class Day21Stub : HttpMessageHandler
    {
        readonly List<JsonObject> data = [];
        long revision = 3;
        public DateTimeOffset Now { get; } = new(2026, 10, 5, 10, 0, 0, TimeSpan.FromHours(8));
        public Day21Stub()
        {
            foreach (var (name, input, recorded) in new[] { ("阅读次数", "COUNT", false), ("减少吸烟", "DAILY", true), ("专注计时由另一设备负责", "TIMER", false) })
            {
                var id = Guid.NewGuid().ToString(); var entries = new JsonObject();
                if (recorded) entries["2026-10-05"] = JsonSerializer.SerializeToNode(new { habitId = id, date = "2026-10-05", value = 0, note = "真实零记录", recordedAt = Now.ToUnixTimeMilliseconds(), remainderSeconds = 0, sessions = Array.Empty<object>() });
                data.Add(new JsonObject { ["plan"] = JsonSerializer.SerializeToNode(new { id, name, input, start = "2026-10-05", unit = input == "TIMER" ? "分钟" : "次", mode = input == "DAILY" ? "CHECK" : "AT_LEAST", archivedOn = (string?)null, smoking = input == "DAILY" }), ["entries"] = entries,
                    ["timer"] = input == "TIMER" ? JsonSerializer.SerializeToNode(new { at = Now.AddMinutes(-3).ToUnixTimeMilliseconds(), day = "2026-10-05", id = Guid.NewGuid(), device = Guid.NewGuid() }) : null });
            }
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post)
            {
                var op = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!["operation"]!;
                var index = data.FindIndex(d => d["plan"]!["id"]!.GetValue<string>() == op["entityId"]!.GetValue<string>()); data[index] = op["data"]!.DeepClone().AsObject(); revision++;
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { operationId = op["operationId"]!.GetValue<Guid>(), status = "applied", revision, current = data[index] }) };
            }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { sourceProject = "21day", cursor = revision, cards = data.Select(d => new {
                sourceProject = "21day", sourceId = d["plan"]!["id"]!.GetValue<string>(), sourceRevision = revision, day = "2026-10-05", name = d["plan"]!["name"]!.GetValue<string>(), unit = d["plan"]!["unit"]!.GetValue<string>(), input = d["plan"]!["input"]!.GetValue<string>(), scheduled = true, target = 20,
                entry = d["entries"]!["2026-10-05"], timer = d["timer"], data = d, actions = d["timer"] is not null ? new[] { "timer_finish", "timer_cancel", "timer_takeover" } : d["plan"]!["input"]!.GetValue<string>() == "COUNT" ? new[] { "count", "set_total", "archive" } : new[] { "confirm", "set_total", "archive" } }) }) };
        }
    }
}

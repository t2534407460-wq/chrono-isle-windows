using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChronoIsle.App;
using ChronoIsle.App.Services;
using ChronoIsle.App.Services.Commanding;
using ChronoIsle.App.ViewModels;
using ChronoIsle.App.Views;

namespace ChronoIsle.UiTests;

public sealed class AssistantTaskCardTests
{
    [Fact]
    public async Task Shared_card_renders_and_binds_choices_dates_and_confirmation_at_island_and_chat_widths()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "assistant-card-test", Guid.NewGuid().ToString("N"));
            System.Windows.Application? app = null;
            Window? host = null;
            try
            {
                app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                Directory.CreateDirectory(directory);
                var data = new LifeDataService(Path.Combine(directory, "life.db"));
                var vm = new LifeViewModel(data, null!, null!, null!, null!);
                foreach (var name in new[] { "DesignTokens", "Controls", "Components" })
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    { Source = new Uri($"pack://application:,,,/ChronoIsle;component/Resources/{name}.xaml") });
                var card = new AssistantTaskCard { DataContext = vm };
                host = new Window { Content = card, Left = -32000, Top = -32000, Width = 580,
                    SizeToContent = SizeToContent.Height, ShowInTaskbar = false, ShowActivated = false };
                host.Show();
                var id = Guid.NewGuid().ToString("N");
                var cases = new[]
                {
                    new AssistantInteraction(id, 1, "NeedsInput", "补充任务信息", "创建：睡觉 · 每天",
                        [new("0.timeText", "每天几点提醒你？", "time", [])]),
                    new AssistantInteraction(id, 2, "NeedsInput", "选择目标事项", "修改：项目评审",
                        [new("0.candidate", "你要修改哪一个项目评审？", "choice", [new("项目评审 · 日程 · 09 月 21 日 14:00", "one"), new("项目评审 · 日程 · 09 月 22 日 15:00", "two")])]),
                    new AssistantInteraction(id, 3, "NeedsInput", "补充任务信息", "创建：项目评审",
                        [new("0.timeText", "开始日期与时间", "datetime", []), new("0.endText", "结束日期与时间", "datetime", [])]),
                    new AssistantInteraction(id, 4, "NeedsConfirmation", "核对并执行", "调整时间：项目评审\n2026-09-21 14:00 → 2026-09-22 15:00\n确认后执行；修改信息后会重新核对。", []),
                    new AssistantInteraction(id, 5, "NeedsInput", "一起完善计划", "目标：站起来活动\n频率：每 120 分钟\n排除：午休",
                        [new("0.schedule.days", "哪些日子执行？", "choice", AssistantScenarioPlanner.DayOptions, "custom", "工作时间不等于工作日；法定工作日包含调休。"),
                         new("0.schedule.weekdays", "选择每周执行的日期", "multichoice", AssistantScenarioPlanner.WeekdayOptions, "1,3,5", DependsOn: "0.schedule.days", DependsValue: "custom"),
                         new("0.schedule.window", "每天在哪个时间段内提醒？", "time_range", [new("09:00–18:00", "09:00-18:00")], "09:00-18:00", "选择建议或自定义工作时间。"),
                         new("0.schedule.exclusion", "排除哪个休息时段？", "time_range", [new("12:00–13:00", "12:00-13:00")], "12:00-13:00"),
                         new("0.schedule.first", "第一次什么时候提醒？", "choice", [new("先工作满一个间隔", "after_interval"), new("工作开始时", "start")], "after_interval"),
                         new("0.schedule.rhythm", "休息后如何继续计时？", "choice", [new("继续原来的节奏", "skip"), new("休息结束重新计时", "restart")])],
                         "先补充影响执行的条件，再核对实际提醒时刻。"),
                    new AssistantInteraction(id, 6, "NeedsInput", "安排日程", "项目评审 · 明天 15:00",
                        [new("0.durationText", "这项日程持续多久？", "duration", AssistantScenarioPlanner.DurationOptions, "90", "可选建议，也可输入分钟数。")]),
                    new AssistantInteraction(id, 7, "NeedsInput", "查看事项", "选择想查看的时间范围",
                        [new("0.timeText", "查询范围", "choice", [new("本周", "本周"),new("自定义", "自定义")], "自定义"),
                         new("0.dueText", "开始日期", "date", [], "2026-09-21", DependsOn: "0.timeText", DependsValue: "自定义"),
                         new("0.endText", "结束日期", "date", [], "2026-09-25", DependsOn: "0.timeText", DependsValue: "自定义")]),
                    new AssistantInteraction(id, 8, "NeedsConfirmation", "核对执行计划", "站起来活动\n周一至周五 · 09:00–18:00\n每 120 分钟 · 排除 12:00–13:00\n首次：工作满一个间隔\n午休后：继续原节奏",
                        [], "确认后创建一个重复事项。", Preview: ["09-21 周一 11:00", "09-21 周一 13:00", "09-21 周一 15:00", "09-21 周一 17:00"])
                };
                var output = Environment.GetEnvironmentVariable("CHRONOISLE_ASSISTANT_QA_DIR");
                using var themeService = new ThemeService(new LifePreferencesService());
                foreach (var theme in new[] { AppThemeMode.Dark, AppThemeMode.Light })
                for (var index = 0; index < cases.Length; index++)
                foreach (var width in new[] { 340d, 560d })
                {
                    themeService.Apply(theme);
                    vm.Interaction = null;
                    vm.Interaction = cases[index];
                    card.Width = width;
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { },
                        System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    card.Measure(new Size(width, 600));
                    card.Arrange(new Rect(0, 0, width, card.DesiredSize.Height));
                    card.UpdateLayout();
                    Assert.True(card.ActualHeight > 70 && card.ActualHeight <= 520, $"case={index}, height={card.ActualHeight}, visibility={card.Visibility}");
                    Assert.True(card.ActualWidth <= width);
                    var visible = Descendants<FrameworkElement>(card).Where(e => e.Visibility == Visibility.Visible).ToArray();
                    if (index == 1)
                    {
                        var choices = visible.OfType<ComboBox>().Single(b => b.Items.Count == 2);
                        choices.SelectedIndex = 1;
                        Assert.Equal("two", vm.InteractionFields.Single().InputValue);
                    }
                    if (index == 0)
                    {
                        var combos = visible.OfType<ComboBox>().Where(b => b.Items.Count is 24 or 60).ToArray();
                        Assert.Equal(2, combos.Length);
                        combos.Single(b => b.Items.Count == 24).SelectedIndex = 0;
                        combos.Single(b => b.Items.Count == 60).SelectedIndex = 30;
                        Assert.Equal("00:30", vm.InteractionFields.Single().InputValue);
                    }
                    if (index == 3)
                        Assert.Contains(visible.OfType<Button>(), b => Equals(b.Content, "确认执行") && b.Command?.CanExecute(null) == true);
                    if (index == 4)
                    {
                        var days = vm.InteractionFields.First(f => f.Key.EndsWith(".days"));
                        var weekdays = vm.InteractionFields.First(f => f.Key.EndsWith(".weekdays"));
                        Assert.True(weekdays.IsVisible);
                        Assert.Equal("1,3,5", weekdays.InputValue);
                        days.Value = "daily";
                        Assert.False(weekdays.IsVisible);
                        days.Value = "custom";
                        Assert.True(weekdays.IsVisible);
                        var range = vm.InteractionFields.First(f => f.IsRange);
                        range.RangeStart = "08:30";
                        Assert.Equal("08:30-18:00", range.InputValue);
                    }
                    if (index == 5)
                    {
                        var duration = vm.InteractionFields.Single();
                        duration.Suggestion = "120";
                        Assert.Equal("120", duration.InputValue);
                        duration.Value = "75";
                        Assert.Equal("75", duration.InputValue);
                    }
                    if (index == 6)
                    {
                        var range = vm.InteractionFields.First(f => f.IsChoice);
                        range.Value = "本周";
                        Assert.All(vm.InteractionFields.Where(f => f.HasDate), f => Assert.False(f.IsVisible));
                        range.Value = "自定义";
                        Assert.All(vm.InteractionFields.Where(f => f.HasDate), f => Assert.True(f.IsVisible));
                    }
                    if (index == 7)
                        Assert.Contains(visible.OfType<TextBlock>(), t => t.Text.Contains("09-21 周一 13:00"));
                    if (string.IsNullOrWhiteSpace(output)) continue;
                    Directory.CreateDirectory(output);
                    var bitmap = new RenderTargetBitmap((int)(width * 1.5), (int)Math.Ceiling(card.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
                    var visual = new DrawingVisual();
                    using (var drawing = visual.RenderOpen())
                        drawing.DrawRectangle(new VisualBrush(card), null, new Rect(0, 0, card.ActualWidth, card.ActualHeight));
                    bitmap.Render(visual);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(output, $"assistant-card-{theme}-{index}-{width}.png"));
                    encoder.Save(file);
                }

                VerifyContinuePlanningButton(data, Path.Combine(directory, "life.db"), card, output);
            }
            catch (Exception e) { completed.TrySetException(e); }
            finally
            {
                host?.Close();
                app?.Shutdown();
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
            completed.TrySetResult();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    static void VerifyContinuePlanningButton(LifeDataService data, string path, AssistantTaskCard card, string? output)
    {
        var session = data.NewSession();
        var turn = new AssistantDraftTurn(Guid.NewGuid().ToString("N"), session.Id, 1, "NeedsInput",
            "下班后到午夜提醒我活动", DateTimeOffset.Now, "Asia/Shanghai", DateTimeOffset.UtcNow.AddMinutes(15),
            [new("create_reminder", "下班后到午夜提醒我活动", "活动",
                RepeatText: "工作日",
                Schedule: new("120", "18:00-00:00", "none", "official", FirstTrigger: "after_interval", WeekdaysText: "1,2,3,4,5"))],
            [new("0.schedule.window", "每天在哪个时间段内提醒？", "time_range", [], "18:00-00:00")],
            new Dictionary<int,AssistantPlanCandidateBindingV2>(), new Dictionary<string,AssistantPlanCandidateBindingV2>(), InteractionVersion: 2);
        new AssistantDraftStore(path).Save(turn);
        var actions = new AssistantActionService(data, new ChinaStatutoryHolidayCalendar(), new ConversationRouter(),
            new LocalAgendaQueryService(data), new NoModel());
        var vm = new LifeViewModel(data, actions, new ProviderSettingsService(), null!,
            new ChronoIsle.App.Services.State.IslandStateCoordinator());
        vm.SelectedSession = vm.Sessions.Single(s => s.Id == session.Id);
        card.DataContext = vm;
        Pump();
        var submit = Descendants<Button>(card).Single(b => Equals(b.Content, "继续规划"));
        Assert.True(submit.IsEnabled);
        var range = Assert.Single(vm.InteractionFields);
        var endInput = Descendants<TextBox>(card).Single(t =>
            System.Windows.Automation.AutomationProperties.GetName(t) == "时段结束（24 小时制）");
        endInput.Text = "02:00";
        Click();
        Assert.True(range.HasError);
        Assert.Contains("跨午夜", range.Error);
        Assert.Contains(Descendants<TextBlock>(card), t => t.IsVisible && t.Text.Contains("其他跨午夜"));
        Assert.Empty(data.Messages(session.Id));
        Save("invalid-window");

        endInput.Text = "00:00";
        Click();
        Assert.Equal("NeedsConfirmation", vm.Interaction?.State);
        Assert.Contains("20:00", vm.Interaction!.Summary);
        Assert.Contains("22:00", vm.Interaction.Summary);
        Assert.Empty(data.RecurringReminders());
        Assert.False(vm.IsSending);
        Assert.Contains(Descendants<Button>(card), b => b.IsVisible && Equals(b.Content, "确认执行"));
        Save("midnight-preview");

        void Pump() => System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { },
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        void Click()
        {
            ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(submit)).Invoke();
            Pump();
            var task = vm.SubmitInteractionCommand.ExecutionTask;
            Assert.True(task is { IsCompletedSuccessfully: true });
            card.UpdateLayout();
        }
        void Save(string name)
        {
            if (string.IsNullOrWhiteSpace(output)) return;
            card.Measure(new Size(card.Width, 700));
            card.Arrange(new Rect(0, 0, card.Width, card.DesiredSize.Height));
            card.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)(card.ActualWidth * 1.5), (int)Math.Ceiling(card.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(new VisualBrush(card), null, new Rect(0, 0, card.ActualWidth, card.ActualHeight));
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, name + ".png"));
            encoder.Save(file);
        }
    }

    sealed class NoModel : IChatCompletionClient
    {
        public Task<string> Complete(ProviderSettings provider, IEnumerable<ModelMessage> messages, bool jsonObject = false) => throw new InvalidOperationException("No model call expected.");
        public Task<string> Reply(ProviderSettings provider, IEnumerable<ChatMessage> history, string input) => throw new InvalidOperationException("No model call expected.");
        public Task Test(ProviderSettings provider) => throw new InvalidOperationException("No model call expected.");
    }

    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}

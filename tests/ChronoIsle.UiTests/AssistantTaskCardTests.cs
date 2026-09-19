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
                    new AssistantInteraction(id, 4, "NeedsConfirmation", "核对并执行", "调整时间：项目评审\n2026-09-21 14:00 → 2026-09-22 15:00\n确认后执行；修改信息后会重新核对。", [])
                };
                var output = Environment.GetEnvironmentVariable("CHRONOISLE_ASSISTANT_QA_DIR");
                using var themeService = new ThemeService(new LifePreferencesService());
                foreach (var theme in new[] { AppThemeMode.Dark, AppThemeMode.Light })
                for (var index = 0; index < cases.Length; index++)
                foreach (var width in new[] { 340d, 560d })
                {
                    themeService.Apply(theme);
                    vm.Interaction = cases[index];
                    card.Width = width;
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { },
                        System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    card.Measure(new Size(width, 600));
                    card.Arrange(new Rect(0, 0, width, card.DesiredSize.Height));
                    card.UpdateLayout();
                    Assert.True(card.ActualHeight > 70 && card.ActualHeight <= 326, $"case={index}, height={card.ActualHeight}, visibility={card.Visibility}");
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

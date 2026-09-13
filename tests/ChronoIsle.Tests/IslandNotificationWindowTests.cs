using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChronoIsle.App.Services;
using ChronoIsle.App.Views;

namespace ChronoIsle.Tests;

public sealed class IslandNotificationWindowTests
{
    [Fact]
    public async Task NotificationCard_RendersBothThemesAndHandlesReplacementAndScreenEdges()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            System.Windows.Application? app = null;
            IslandNotificationWindow? window = null;
            try
            {
                app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                foreach (var name in new[] { "DesignTokens", "Controls", "Components" })
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri($"pack://application:,,,/ChronoIsle;component/Resources/{name}.xaml")
                    });
                var theme = new ThemeService(new LifePreferencesService());
                window = new IslandNotificationWindow();
                var message = new SystemToastMessage(41, "Outlook", "项目同步会将在 10 分钟后开始",
                    "产品与设计 · 今天 14:30\n已为你准备好会议资料，请查看最新议程。",
                    "Microsoft.Outlook", new DateTimeOffset(2026, 9, 13, 14, 20, 0, TimeSpan.FromHours(8)));
                var output = Environment.GetEnvironmentVariable("CHRONOISLE_NOTIFICATION_QA_DIR");
                foreach (var mode in new[] { AppThemeMode.Dark, AppThemeMode.Light })
                {
                    theme.Preview(mode, AppAccentScheme.Emerald);
                    window.ShowMessage(message);
                    window.PositionNextTo(new Rect(900, 20, 100, 40), new Rect(0, 0, 1000, 800), false);
                    Assert.True(window.Left + window.Width <= 1000);
                    Assert.True(window.Top >= 60);
                    Assert.False(window.ShowActivated);
                    Assert.Same(message, window.CurrentMessage);
                    var title = (TextBlock)window.FindName("NotificationTitle");
                    Assert.Equal(message.Title, title.Text);
                    var body = (TextBlock)window.FindName("NotificationBody");
                    Assert.True(body.ActualHeight <= 54);
                    if (!string.IsNullOrEmpty(output))
                    {
                        Directory.CreateDirectory(output);
                        var translate = (TranslateTransform)window.FindName("NotificationTranslate");
                        translate.BeginAnimation(TranslateTransform.YProperty, null);
                        translate.Y = 0;
                        var card = (Border)window.FindName("NotificationCard");
                        var render = new RenderTargetBitmap((int)Math.Ceiling(card.ActualWidth * 2),
                            (int)Math.Ceiling(card.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
                        var visual = new DrawingVisual();
                        using (var drawing = visual.RenderOpen())
                            drawing.DrawRectangle(new VisualBrush(card), null, new Rect(0, 0, card.ActualWidth, card.ActualHeight));
                        render.Render(visual);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(render));
                        using var file = File.Create(Path.Combine(output, $"notification-{mode.ToString().ToLowerInvariant()}.png"));
                        encoder.Save(file);
                    }
                }
                var longMessage = message with { Id = 42, Title = new string('长', 140), Body = new string('文', 800) };
                window.ShowMessage(longMessage);
                window.PositionNextTo(new Rect(0, 770, 100, 30), new Rect(0, 0, 1000, 770), true);
                Assert.True(window.Left >= 0);
                Assert.True(window.Top + window.ActualHeight <= 770);
                Assert.True(((TextBlock)window.FindName("NotificationTitle")).ActualHeight <= 40);
                Assert.True(((TextBlock)window.FindName("NotificationBody")).ActualHeight <= 54);
                window.HideMessage();
                Assert.Null(window.CurrentMessage);
                window.ShowMessage(message with { Id = 43, AppName = "微信", Body = "", AppUserModelId = "" });
                Assert.Equal(43u, window.CurrentMessage!.Id);
                Assert.Equal(Visibility.Collapsed, ((TextBlock)window.FindName("NotificationBody")).Visibility);
                Assert.False(((Button)window.FindName("OpenAppButton")).IsEnabled);
                completed.SetResult();
            }
            catch (Exception exception) { completed.SetException(exception); }
            finally
            {
                window?.Close();
                app?.Shutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
}

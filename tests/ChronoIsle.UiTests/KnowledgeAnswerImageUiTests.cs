using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ChronoIsle.App.Views;

namespace ChronoIsle.UiTests;

public sealed class KnowledgeAnswerImageUiTests
{
    [KnowledgeImageUiFact]
    public async Task Chat_without_an_origin_previews_authenticated_images_and_keeps_external_images_explicit()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                var file = Environment.GetEnvironmentVariable("CHRONOISLE_CHAT_IMAGE_ANSWER")!;
                var view = new MarkdownView { Width = 850, Padding = new Thickness(18), Background = new SolidColorBrush(Color.FromRgb(20, 25, 28)) };
                view.Resources["Brush.TextPrimary"] = Brushes.WhiteSmoke;
                view.Resources["Brush.TextSecondary"] = Brushes.LightGray;
                view.Resources["Brush.Accent"] = Brushes.LightSkyBlue;
                view.Markdown = File.ReadAllText(file) + "\n\n![外部图片](https://external.invalid/never-requested.png)";
                Assert.Null(view.Origin);
                view.Measure(new Size(850, double.PositiveInfinity));
                view.Arrange(new Rect(0, 0, 850, view.DesiredSize.Height)); view.UpdateLayout();
                var buttons = Logical<Button>(view.Document).Where(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "MarkdownImage").ToArray();
                Assert.NotEmpty(buttons);
                Assert.DoesNotContain(buttons, b => b.ToolTip?.ToString()?.Contains("external.invalid") == true);
                foreach (var button in buttons) button.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (DateTime.UtcNow < deadline && buttons.Any(b => Logical<Image>(b).Single().Source is null))
                {
                    var frame = new DispatcherFrame();
                    view.Dispatcher.BeginInvoke(() => frame.Continue = false, DispatcherPriority.Background);
                    Dispatcher.PushFrame(frame); Thread.Sleep(20);
                }
                Assert.All(buttons, b => Assert.NotNull(Logical<Image>(b).Single().Source));
                view.Measure(new Size(850, double.PositiveInfinity));
                view.Arrange(new Rect(0, 0, 850, view.DesiredSize.Height)); view.UpdateLayout();
                var bitmap = new RenderTargetBitmap(850, Math.Min(4000, (int)Math.Ceiling(view.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(view);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.ChangeExtension(file, ".png")); encoder.Save(output);
                foreach (var button in buttons) button.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                completion.SetResult();
            }
            catch (Exception error) { completion.SetException(error); }
        });
        worker.SetApartmentState(ApartmentState.STA); worker.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(40));
    }

    static IEnumerable<T> Logical<T>(DependencyObject node) where T : DependencyObject
    {
        if (node is T value) yield return value;
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            foreach (var descendant in Logical<T>(child)) yield return descendant;
    }
}

public sealed class KnowledgeImageUiFactAttribute : FactAttribute
{
    public KnowledgeImageUiFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CHRONOISLE_CHAT_IMAGE_ANSWER")))
            Skip = "Explicit check using a locally saved answer from the deployed project knowledge service.";
    }
}

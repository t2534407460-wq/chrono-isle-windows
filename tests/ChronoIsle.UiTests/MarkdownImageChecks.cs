using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChronoIsle.App.Views;

namespace ChronoIsle.UiTests;

static class MarkdownImageChecks
{
    public static void Verify(Window owner, string? output)
    {
        var directory = Path.Combine(Path.GetTempPath(), "chronoisle-images", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var window = new MarkdownViewerWindow { Owner = owner };
        try
        {
            SaveImage(Path.Combine(directory, "图 # %.png"), 1600, 800);
            File.Copy(Path.Combine(directory, "图 # %.png"), Path.Combine(directory, "wiki.png"));
            var path = Path.Combine(directory, "图文.md");
            var markdown = "# 图片示例\n\n![界面截图](图%20%23%20%25.png)\n\n![[wiki.png|300]]\n\n[![带链接图片](图%20%23%20%25.png)](https://example.com)\n\n![缺失](missing.png)\n\n结束";
            File.WriteAllText(path, markdown);
            window.Show(); Await(window, window.LoadAsync(new Uri(path)));
            var buttons = ImageButtons(window);
            Assert.True(buttons.Length == 4, string.Join("\n", buttons.Select(b => b.ToolTip)) + "\n" +
                string.Join("\n", Markdig.Markdown.Parse(markdown, ChronoIsle.App.Services.Markdown.MarkdownDocuments.Pipeline).OfType<Markdig.Syntax.ParagraphBlock>().SelectMany(p => p.Inline!).Select(n => n.GetType().Name + ":" + n)));
            Until(window, () => buttons.Take(3).All(b => Descendants<Image>(b).Single().Source is not null)
                && Descendants<TextBlock>(buttons[3]).Any(t => t.Text.StartsWith("图片无法显示：")));
            Assert.All(buttons.Take(3), b => Assert.Equal(1200, ((BitmapSource)Descendants<Image>(b).Single().Source).PixelWidth));
            Save(window, output, "markdown-inline-images");
            MarkdownUiChecks.Click(window, buttons[0]);
            var preview = Application.Current.Windows.OfType<MarkdownViewerWindow>().Single(w => w != window && w != owner);
            try
            {
                Until(preview, () => preview.Title.StartsWith("图片 ·"));
                Assert.Equal(WindowStyle.None, preview.WindowStyle);
                var image = Descendants<Image>(preview).Single(i => AutomationProperties.GetAutomationId(i) == "ImagePreview");
                Assert.Equal(1600, ((BitmapSource)image.Source).PixelWidth);
                Assert.True(image.Width < 1600);
                FindButton(preview, "ImageActualSize").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); Pump(preview);
                Assert.Equal(1600, image.Width); Assert.Equal(800, image.Height);
                FindButton(preview, "ImageZoomOut").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); Pump(preview);
                Assert.Equal(1280, image.Width);
                FindButton(preview, "ImageZoomIn").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); Pump(preview);
                Assert.Equal(1600, image.Width);
                FindButton(preview, "ImageFit").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                preview.Width = 620; preview.Height = 420; Pump(preview);
                Assert.True(image.Width <= 578 && image.Height <= 250);
                Save(preview, output, "markdown-image-preview");
                Assert.Equal("Markdown · 图文.md", window.Title);
            }
            finally { preview.Close(); }
            window.Width = 620; Pump(window);
            var view = Descendants<MarkdownView>(window).Single();
            Assert.All(buttons.Take(3), b => Assert.True(b.ActualWidth <= view.ActualWidth));

            // Identical Markdown in another directory must use that note's image paths.
            var second = Path.Combine(directory, "second"); Directory.CreateDirectory(second);
            SaveImage(Path.Combine(second, "图 # %.png"), 320, 640);
            File.Copy(Path.Combine(second, "图 # %.png"), Path.Combine(second, "wiki.png"));
            var secondPath = Path.Combine(second, "图文.md"); File.WriteAllText(secondPath, markdown);
            Await(window, window.LoadAsync(new Uri(secondPath)));
            var changed = ImageButtons(window).First();
            Until(window, () => Descendants<Image>(changed).Single().Source is not null);
            Assert.Equal(320, ((BitmapSource)Descendants<Image>(changed).Single().Source).PixelWidth);

            // Optional read-only verification of the specific document supplied by the user.
            var real = Environment.GetEnvironmentVariable("CHRONOISLE_MARKDOWN_IMAGE_NOTE");
            if (!string.IsNullOrWhiteSpace(real))
            {
                var before = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(real));
                window.Width = 1000; window.Height = 900;
                Await(window, window.LoadAsync(new Uri(real)));
                var actual = ImageButtons(window); Assert.Equal(4, actual.Length);
                Until(window, () => actual.All(b => Descendants<Image>(b).Single().Source is not null));
                Assert.Equal(before, System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(real)));
                Descendants<MarkdownView>(window).Single().JumpTo("#L35"); Pump(window);
                Save(window, output, "markdown-user-document-images");
                MarkdownUiChecks.Click(window, actual[0]);
                var actualPreview = Application.Current.Windows.OfType<MarkdownViewerWindow>().Single(w => w != window && w != owner);
                try
                {
                    Until(actualPreview, () => actualPreview.Title.StartsWith("图片 ·"));
                    Assert.NotNull(Descendants<Image>(actualPreview).Single(i => AutomationProperties.GetAutomationId(i) == "ImagePreview").Source);
                    Save(actualPreview, output, "markdown-user-image-opened");
                }
                finally { actualPreview.Close(); }
                if (!string.IsNullOrWhiteSpace(output)) File.WriteAllText(Path.Combine(output, "user-image-check.txt"), "4 referenced local images decoded; document hash unchanged.");
            }
        }
        finally { window.Close(); Directory.Delete(directory, true); }
    }

    static Button FindButton(Window window, string id) => Descendants<Button>(window).Single(b => AutomationProperties.GetAutomationId(b) == id);
    static Button[] ImageButtons(Window window) => Descendants<Button>(window).Where(b => AutomationProperties.GetAutomationId(b) == "MarkdownImage").ToArray();
    static void Await(Window window, Task task) { Until(window, () => task.IsCompleted); task.GetAwaiter().GetResult(); Pump(window); }
    static void Until(Window window, Func<bool> ready)
    {
        for (var i = 0; i < 300 && !ready(); i++) { Pump(window); Thread.Sleep(10); }
        Assert.True(ready(), string.Join("\n", Descendants<TextBlock>(window).Select(t => t.Text))); Pump(window);
    }
    static void Pump(Window window)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        window.Dispatcher.BeginInvoke(() => frame.Continue = false, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        System.Windows.Threading.Dispatcher.PushFrame(frame); window.UpdateLayout();
    }
    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); if (child is T value) yield return value; foreach (var nested in Descendants<T>(child)) yield return nested; }
    }
    static void SaveImage(string path, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 100; pixels[i + 1] = (byte)(i / 4 / width % 200); pixels[i + 2] = 30; pixels[i + 3] = 255; }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    static void Save(Window window, string? output, string name)
    {
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(output, name + ".png")); encoder.Save(stream);
    }
}

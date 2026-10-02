using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using ChronoIsle.App.Services;
using ChronoIsle.App.ViewModels;
using ChronoIsle.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace ChronoIsle.UiTests;

// Runs inside the shared STA application fixture; WPF permits only one Application per process.
static class KnowledgeUiChecks
{
    public static void Verify(LifeDataService data, ThemeService theme, string? output)
    {
        var vm = new LifeViewModel(data, null!, null!, null!, null!);
        vm.BeginQuickAskConversation();
        vm.Messages.Add(new("example", vm.SelectedSession!.Id, "assistant",
            "知识库问答\n\n- 恢复数据库前需要停止演示应用。 [S1]\n  原文 [S1]：恢复数据库前需要停止演示应用。\n\n来源（本次读取快照）\n[S1] E:\\Obsidian Vault\\演示资料\\备份恢复.md · 第 1–3 行 · 备份恢复\n文件修改时间（UTC）：2026-10-02 07:00:00", DateTime.Now));
        using var services = new ServiceCollection().BuildServiceProvider();
        var window = new LifeMainWindow(vm, services)
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
            ShowInTaskbar = false, ShowActivated = false
        };
        try
        {
            window.Show();
            foreach (var mode in new[] { AppThemeMode.Dark, AppThemeMode.Light })
            foreach (var width in new[] { 840d, 1120d })
            {
                theme.Apply(mode);
                window.Width = width;
                var toggle = Descendants<CheckBox>(window).Single(c => Equals(c.Content, "知识库问答"));
                toggle.IsChecked = true;
                Pump(window);
                Assert.True(vm.IsKnowledgeMode);
                Assert.Contains(Descendants<TextBlock>(window), t => t.Text == vm.ChatDescription);
                vm.IsSending = true;
                Pump(window);
                Assert.False(toggle.IsEnabled);
                vm.IsSending = false;
                vm.IsKnowledgeMode = false;
                Pump(window);
                Assert.False(toggle.IsChecked);
                vm.IsKnowledgeMode = true;
                Pump(window);
                Assert.True(toggle.IsChecked);
                var answer = Descendants<MarkdownView>(window).Single(t => System.Windows.Automation.AutomationProperties.GetAutomationId(t) == "ChatMarkdown");
                Assert.True(answer.IsVisible);
                Assert.True(answer.IsReadOnly);
                Assert.True(answer.IsHitTestVisible);
                Assert.Equal(((SolidColorBrush)Application.Current.FindResource("Brush.TextPrimary")).Color, ((SolidColorBrush)answer.Foreground).Color);
                answer.SelectAll();
                Assert.Contains("知识库问答", answer.Selection.Text);
                answer.Selection.Select(answer.Document.ContentStart, answer.Document.ContentStart);
                Save(window, output, $"knowledge-chat-{mode}-{width}");
            }
        }
        finally { window.Close(); }

        MaximizedChatWindowChecks.Verify(vm, services, output);
        MarkdownUiChecks.Verify(vm, services, theme, output);

        var root = FindRoot();
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var settingsDocument = XDocument.Load(Path.Combine(root, "src/ChronoIsle.App/Views/LifeSettingsWindow.xaml"));
        var settings = settingsDocument.Descendants(presentation + "TextBox").Single(e => (string?)e.Attribute(x + "Name") == "KnowledgeVaultPath")
            .Ancestors(presentation + "Border").First();
        var panel = new XElement(settings);
        foreach (var attribute in panel.DescendantsAndSelf().Attributes("Click").ToArray()) attribute.Remove();
        panel.Add(new XAttribute(XNamespace.Xmlns + "x", x.NamespaceName));
        var settingsControl = (FrameworkElement)XamlReader.Parse(panel.ToString());
        settingsControl.Resources.Add(typeof(Button), new Style(typeof(Button), (Style)Application.Current.FindResource("Button.Secondary")));
        foreach (var mode in new[] { AppThemeMode.Dark, AppThemeMode.Light })
        foreach (var width in new[] { 500d, 640d })
        {
            theme.Apply(mode);
            settingsControl.Width = width;
            settingsControl.Measure(new Size(width, double.PositiveInfinity));
            settingsControl.Arrange(new Rect(0, 0, width, settingsControl.DesiredSize.Height));
            settingsControl.UpdateLayout();
            Assert.Contains(Descendants<TextBlock>(settingsControl), t => t.Text.Contains("命中的原文片段"));
            Assert.True(settingsControl.ActualHeight < 610);
            Save(settingsControl, output, $"knowledge-settings-{mode}-{width}");
        }

        var island = XDocument.Load(Path.Combine(root, "src/ChronoIsle.App/Views/LifeIslandWindow.xaml"));
        var checkboxXml = new XElement(island.Descendants(presentation + "CheckBox").Single(e => (string?)e.Attribute(x + "Name") == "QuickAskKnowledgeMode"));
        checkboxXml.Add(new XAttribute(XNamespace.Xmlns + "x", x.NamespaceName));
        var quickToggle = (CheckBox)XamlReader.Parse(checkboxXml.ToString());
        quickToggle.DataContext = vm;
        quickToggle.Measure(new Size(340, 60));
        quickToggle.Arrange(new Rect(0, 0, 340, 40));
        Pump(quickToggle);
        Assert.True(quickToggle.IsChecked);
        quickToggle.IsChecked = false;
        Assert.False(vm.IsKnowledgeMode);
        Assert.True(quickToggle.DesiredSize.Width <= 340);
        Assert.Contains("QuickAskKnowledgeMode.DataContext = assistant;", File.ReadAllText(Path.Combine(root, "src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs")));
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
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(output, name + ".png"));
        encoder.Save(file);
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

    static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ChronoIsle.sln"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException();
    }
}

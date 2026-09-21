using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Xml.Linq;
using ChronoIsle.App.Views;

namespace ChronoIsle.Tests;

public sealed class CollapsedHeaderLayoutTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Header_ContentChangesKeepWidthStableAndCommonTelemetryUntrimmed(bool showAll)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Window? window = null;
            try
            {
                var workspace = new DirectoryInfo(AppContext.BaseDirectory);
                while (workspace is not null && !File.Exists(Path.Combine(workspace.FullName, "ChronoIsle.sln")))
                    workspace = workspace.Parent;
                var document = XDocument.Load(Path.Combine(workspace!.FullName,
                    "src", "ChronoIsle.App", "Views", "LifeIslandWindow.xaml"));
                XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
                XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
                var header = new XElement(document.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "Header"));
                foreach (var child in header.Elements().Where(e => e.Name.LocalName != "Grid.ColumnDefinitions" &&
                             (string?)e.Attribute(x + "Name") is not "DefaultHeaderLeft" and not "ClockGroup").ToArray())
                    child.Remove();
                foreach (var attribute in header.DescendantsAndSelf().Attributes().Where(a =>
                             a.Name.LocalName.StartsWith("Preview", StringComparison.Ordinal)).ToArray())
                    attribute.Remove();
                var buttonStyle = new XElement(document.Descendants(ui + "Style")
                    .Single(e => (string?)e.Attribute(x + "Key") == "CollapsedHeaderButton"));
                var root = new XElement(ui + "Window", new XAttribute(XNamespace.Xmlns + "x", x),
                    new XAttribute("Width", "294"), new XAttribute("SizeToContent", "Height"),
                    new XAttribute("ShowActivated", "False"), new XAttribute("ShowInTaskbar", "False"),
                    new XAttribute("WindowStyle", "None"), new XAttribute("AllowsTransparency", "True"),
                    new XAttribute("Opacity", "0"),
                    new XAttribute("UseLayoutRounding", "True"),
                    new XAttribute("TextOptions.TextFormattingMode", "Display"),
                    new XAttribute("FontFamily", "Microsoft YaHei UI, Segoe UI Variable Text, Segoe UI"),
                    new XAttribute("FontSize", "13"),
                    new XElement(ui + "Window.Resources", new XElement(ui + "ResourceDictionary",
                        new XElement(ui + "ResourceDictionary.MergedDictionaries",
                            new[] { "DesignTokens", "Controls" }.Select(name => new XElement(ui + "ResourceDictionary",
                                new XAttribute("Source", $"pack://application:,,,/ChronoIsle;component/Resources/{name}.xaml")))),
                        buttonStyle)), new XElement(ui + "Grid", new XAttribute("Margin", "8,8,8,11"),
                        new XElement(ui + "Border", new XAttribute("BorderThickness", "1"), header)));
                window = (Window)XamlReader.Parse(root.ToString());
                FrameworkElement Element(string name) => (FrameworkElement)window.FindName(name);
                foreach (var name in new[] { "Summary", "NetworkSpeedSummary", "ExpandIndicator" })
                    Element(name).Visibility = showAll ? Visibility.Visible : Visibility.Collapsed;
                Element("NetworkLatencySummary").Visibility = Visibility.Visible;
                window.Show();
                double? initialWidth = null;
                foreach (var (cpu, memory, fps, latency, clock, summary, speed, trimOverflow) in new[]
                {
                    ("CPU 9%", "内存 9%", "FPS --", "-- ms", "11:11:11", "今天暂无安排", "↑ 0 B/s  ↓ 0 B/s", false),
                    ("CPU 100%", "内存 100%", "FPS 999", "999 ms", "20:48:58", new string('长', 100), "↑ 999.9 MB/s  ↓ 999.9 MB/s", false),
                    ("CPU 35%", "内存 65%", "FPS 9999", "9999 ms", "16:47:32", "短", "↑ 1 KB/s  ↓ 1 KB/s", true),
                    ("CPU 35%", "内存 65%", "FPS 60", "44 ms", "16:47:32", "短", "↑ 1 KB/s  ↓ 1 KB/s", false)
                })
                {
                    ((TextBlock)Element("CpuUsageSummary")).Text = cpu;
                    ((TextBlock)Element("MemoryUsageSummary")).Text = memory;
                    ((TextBlock)Element("FpsSummary")).Text = fps;
                    ((TextBlock)Element("NetworkLatencySummary")).Text = latency;
                    ((TextBlock)Element("Clock")).Text = clock;
                    ((TextBlock)Element("Summary")).Text = summary;
                    ((TextBlock)Element("NetworkSpeedSummary")).Text = speed;
                    var left = Element("DefaultHeaderLeft");
                    var right = Element("ClockGroup");
                    left.Measure(new Size(double.PositiveInfinity, 43));
                    right.Measure(new Size(double.PositiveInfinity, 43));
                    var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window).DpiScaleX;
                    var width = LifeIslandWindow.HorizontalLayoutThickness(new Thickness(8, 8, 8, 11), dpi) +
                                LifeIslandWindow.HorizontalLayoutThickness(new Thickness(1), dpi) +
                                left.DesiredSize.Width + right.DesiredSize.Width;
                    initialWidth ??= width;
                    Assert.Equal(initialWidth.Value, width, 3);
                    if (!showAll) Assert.InRange(width, 96, 440);
                    window.Width = width;
                    window.UpdateLayout();
                    foreach (var name in new[] { "CpuUsageSummary", "MemoryUsageSummary", "FpsSummary", "NetworkLatencySummary", "Clock" })
                    {
                        var text = (TextBlock)Element(name);
                        var natural = new TextBlock { Text = text.Text, FontFamily = text.FontFamily, FontSize = text.FontSize, FontWeight = text.FontWeight };
                        System.Windows.Media.TextOptions.SetTextFormattingMode(natural, System.Windows.Media.TextFormattingMode.Display);
                        natural.Measure(new Size(double.PositiveInfinity, 43));
                        if (trimOverflow && name is "FpsSummary" or "NetworkLatencySummary")
                        {
                            Assert.True(text.ActualWidth < natural.DesiredSize.Width);
                            Assert.Equal(TextTrimming.CharacterEllipsis, text.TextTrimming);
                            continue;
                        }
                        Assert.True(text.ActualWidth >= natural.DesiredSize.Width,
                            $"{text.Text}: allocated={text.ActualWidth}, required={natural.DesiredSize.Width}, header={width}, dpi={System.Windows.Media.VisualTreeHelper.GetDpi(window).DpiScaleX}");
                    }
                }
                Element("DefaultHeaderLeft").Measure(new Size(double.PositiveInfinity, 43));
                var visibleLeftWidth = Element("DefaultHeaderLeft").DesiredSize.Width;
                Element("CpuUsageSummary").Visibility = Visibility.Collapsed;
                window.UpdateLayout();
                Element("DefaultHeaderLeft").Measure(new Size(double.PositiveInfinity, 43));
                Assert.True(Element("DefaultHeaderLeft").DesiredSize.Width < visibleLeftWidth,
                    $"Disabling a widget must release its reserved width: before={visibleLeftWidth}, after={Element("DefaultHeaderLeft").DesiredSize.Width}.");
                completion.SetResult();
            }
            catch (Exception exception) { completion.SetException(exception); }
            finally { window?.Close(); System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
}

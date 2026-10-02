using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChronoIsle.App.Services.Markdown;
using Control = System.Windows.Controls.Control;

namespace ChronoIsle.App.Views;

public sealed partial class MarkdownViewerWindow : Window
{
    readonly MarkdownView viewer = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    readonly TextBox source = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    readonly TextBlock location = new() { TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 12, 0, 14) };
    readonly TextBlock heading = new() { Text = "Markdown 查看器", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) };
    readonly CheckBox showSource = new() { Content = "查看 Markdown 原文", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 0, 0) };
    readonly System.Windows.Controls.Image image = new() { Stretch = Stretch.Uniform };
    readonly ScrollViewer imageScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed };
    readonly WrapPanel imageTools = new() { Orientation = System.Windows.Controls.Orientation.Horizontal, Visibility = Visibility.Collapsed };
    readonly TextBlock zoomLabel = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    readonly CancellationTokenSource lifetime = new();
    CancellationTokenSource? loading;
    Uri? current;
    HwndSource? windowSource;
    bool showingImage;
    double zoom; // Zero fits the image inside the available viewport.
    public MarkdownViewerWindow()
    {
        InitializeComponent();
        var root = new DockPanel { Margin = new Thickness(24, 8, 24, 24) };
        location.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        location.SetBinding(ToolTipProperty, new System.Windows.Data.Binding(nameof(TextBlock.Text)) { Source = location });
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        header.Children.Add(heading);
        var toolbar = new WrapPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        var open = new Button { Content = "打开 Markdown 文件…", Padding = new Thickness(12, 7, 12, 7) };
        open.SetResourceReference(StyleProperty, "Button.Secondary");
        open.Click += async (_, _) => { var picker = new Microsoft.Win32.OpenFileDialog { Filter = "Markdown|*.md;*.markdown", CheckFileExists = true }; if (picker.ShowDialog(this) == true) await LoadAsync(new Uri(picker.FileName)); };
        toolbar.Children.Add(open);
        showSource.Checked += (_, _) => UpdateVisibility();
        showSource.Unchecked += (_, _) => UpdateVisibility();
        toolbar.Children.Add(showSource); header.Children.Add(toolbar);
        foreach (var (label, id, value) in new[] { ("适应窗口", "ImageFit", 0d), ("原始大小", "ImageActualSize", 1d), ("−", "ImageZoomOut", -1d), ("＋", "ImageZoomIn", -2d) })
        {
            var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 0) };
            button.SetResourceReference(StyleProperty, "Button.Secondary");
            System.Windows.Automation.AutomationProperties.SetAutomationId(button, id);
            button.Click += (_, _) => { zoom = value >= 0 ? value : Math.Clamp((zoom > 0 ? zoom : FitScale()) * (value == -1 ? .8 : 1.25), .05, 4); ResizeImage(); };
            imageTools.Children.Add(button);
        }
        imageTools.Children.Add(zoomLabel); header.Children.Add(imageTools); header.Children.Add(location);
        var body = new Grid(); body.Children.Add(viewer); body.Children.Add(source); body.Children.Add(imageScroll); source.Visibility = Visibility.Collapsed;
        imageScroll.Content = image;
        System.Windows.Automation.AutomationProperties.SetAutomationId(image, "ImagePreview");
        imageScroll.SizeChanged += (_, _) => { if (showingImage && zoom == 0) ResizeImage(); };
        toolbar.SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(Visibility)) { Source = showSource });
        source.SetResourceReference(Control.BackgroundProperty, "Brush.Card"); source.SetResourceReference(Control.ForegroundProperty, "Brush.TextPrimary");
        viewer.Navigate = address => OpenLink(this, address, current);
        var page = new Border { Padding = new Thickness(20), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Child = body };
        page.SetResourceReference(Border.BackgroundProperty, "Brush.Card");
        page.SetResourceReference(Border.BorderBrushProperty, "Brush.StrokeSoft");
        root.Children.Add(page); DocumentHost.Child = root;
        Closed += (_, _) => { lifetime.Cancel(); lifetime.Dispose(); };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        windowSource?.AddHook(WindowWorkArea.ConstrainMaximizedBounds);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (windowSource is { IsDisposed: false }) windowSource.RemoveHook(WindowWorkArea.ConstrainMaximizedBounds);
        windowSource = null;
        base.OnClosed(e);
    }

    void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2) ToggleMaximize();
        else DragMove();
    }

    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    void Close_Click(object sender, RoutedEventArgs e) => Close();
    void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    void UpdateVisibility()
    {
        showSource.Visibility = showingImage ? Visibility.Collapsed : Visibility.Visible;
        imageTools.Visibility = imageScroll.Visibility = showingImage ? Visibility.Visible : Visibility.Collapsed;
        source.Visibility = !showingImage && showSource.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        viewer.Visibility = !showingImage && showSource.IsChecked != true ? Visibility.Visible : Visibility.Collapsed;
    }

    double FitScale() => image.Source is BitmapSource bitmap
        ? Math.Min(1, Math.Min(Math.Max(1, imageScroll.ActualWidth - 20) / bitmap.PixelWidth, Math.Max(1, imageScroll.ActualHeight - 20) / bitmap.PixelHeight)) : 1;

    void ResizeImage()
    {
        if (image.Source is not BitmapSource bitmap) return;
        var scale = zoom > 0 ? zoom : FitScale();
        image.Width = bitmap.PixelWidth * scale; image.Height = bitmap.PixelHeight * scale;
        zoomLabel.Text = zoom > 0 ? $"{zoom:P0}" : "适应窗口";
    }

    public async Task LoadAsync(Uri uri)
    {
        loading?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        loading = request;
        var token = request.Token;
        location.Text = "正在读取…";
        try
        {
            var isImage = MarkdownImages.IsImage(uri);
            if (isImage)
            {
                var bitmap = await MarkdownImages.ReadAsync(uri, token: token);
                if (token.IsCancellationRequested) return;
                image.Source = bitmap; zoom = 0;
            }
            else
            {
                var text = await MarkdownDocuments.ReadAsync(uri, token);
                if (token.IsCancellationRequested) return;
                viewer.Origin = uri; viewer.Markdown = text; source.Text = text;
            }
            current = uri; showingImage = isImage;
            heading.Text = isImage ? "图片预览" : "Markdown 查看器";
            UpdateVisibility();
            location.Text = uri.IsFile ? uri.LocalPath + "  · 当前文件内容" : uri.AbsoluteUri;
            Title = (isImage ? "图片 · " : "Markdown · ") + Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));
            await Dispatcher.InvokeAsync(() => { if (isImage) ResizeImage(); else viewer.JumpTo(uri.Fragment); }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        catch (OperationCanceledException) { if (!token.IsCancellationRequested) location.Text = "读取超时，请重试。"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Net.Http.HttpRequestException or System.Text.DecoderFallbackException or ArgumentException)
        { if (!token.IsCancellationRequested) location.Text = (MarkdownImages.IsImage(uri) ? "无法打开图片：" : "无法打开文档：") + error.Message; }
        finally { if (ReferenceEquals(loading, request)) loading = null; }
    }

    public static void OpenLink(Window? owner, string address, Uri? origin = null)
    {
        try
        {
            var target = MarkdownDocuments.Resolve(address, origin);
            if (MarkdownImages.IsImage(target))
            {
                target = MarkdownImages.Resolve(address, origin);
                var window = new MarkdownViewerWindow { Owner = owner };
                window.Show();
                _ = window.LoadAsync(target);
            }
            else if (target.IsFile || MarkdownDocuments.IsMarkdown(target))
            {
                var window = owner as MarkdownViewerWindow;
                if (window is null) { window = new MarkdownViewerWindow { Owner = owner }; window.Show(); }
                if (window.current is not null && Uri.Compare(window.current, target, UriComponents.AbsoluteUri & ~UriComponents.Fragment, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0)
                    window.viewer.JumpTo(target.Fragment);
                else _ = window.LoadAsync(target);
            }
            else Process.Start(new ProcessStartInfo(target.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception error) when (error is IOException or ArgumentException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { System.Windows.MessageBox.Show(owner, error.Message, "无法打开链接", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
}

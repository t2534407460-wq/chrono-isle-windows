using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Control = System.Windows.Controls.Control;

namespace ChronoIsle.App.Views;

/// <summary>Shared chrome and fixed actions for the application's small dialogs.</summary>
internal static class DialogLayout
{
    public static Window Create(string title, Window? owner, FrameworkElement content, FrameworkElement actions,
        double width = 520, double height = 480)
    {
        var dialog = new Window
        {
            Title = title, Owner = owner, Width = width, Height = height,
            MinWidth = Math.Min(width, 400), MinHeight = Math.Min(height, 280),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ShowInTaskbar = false, ResizeMode = ResizeMode.CanResize
        };
        dialog.SetResourceReference(FrameworkElement.StyleProperty, "Window.Display");
        dialog.SetResourceReference(Control.ForegroundProperty, "Brush.TextPrimary");
        WindowChrome.SetWindowChrome(dialog, new WindowChrome
        {
            CaptionHeight = 0, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(16), UseAeroCaptionButtons = false
        });
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new Grid { Margin = new Thickness(20, 0, 12, 0), Background = Brushes.Transparent };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var caption = new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 12, 0) };
        header.Children.Add(caption);
        header.MouseLeftButtonDown += (_, e) => { if (e.ChangedButton == MouseButton.Left) dialog.DragMove(); };
        var close = new Button { Content = "×", ToolTip = "关闭" };
        close.SetResourceReference(FrameworkElement.StyleProperty, "WindowCloseControl");
        close.Click += (_, _) => dialog.Close();
        Grid.SetColumn(close, 1); header.Children.Add(close); layout.Children.Add(header);
        var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 8, 0), Margin = new Thickness(20, 8, 12, 16) };
        Grid.SetRow(scroll, 1); layout.Children.Add(scroll);
        var footer = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(20, 14, 20, 16), Child = actions };
        footer.SetResourceReference(Border.BorderBrushProperty, "Brush.StrokeSoft");
        Grid.SetRow(footer, 2); layout.Children.Add(footer);
        var shell = new Border { CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), Child = layout };
        shell.SetResourceReference(Border.BackgroundProperty, "Brush.Card");
        shell.SetResourceReference(Border.BorderBrushProperty, "Brush.Stroke");
        RenderOptions.SetClearTypeHint(shell, ClearTypeHint.Enabled);
        dialog.Content = shell;
        HwndSource? source = null;
        dialog.SourceInitialized += (_, _) =>
        {
            source = HwndSource.FromHwnd(new WindowInteropHelper(dialog).Handle);
            source?.AddHook(WindowWorkArea.ConstrainMaximizedBounds);
        };
        dialog.Closed += (_, _) => { if (source is { IsDisposed: false }) source.RemoveHook(WindowWorkArea.ConstrainMaximizedBounds); };
        return dialog;
    }
}

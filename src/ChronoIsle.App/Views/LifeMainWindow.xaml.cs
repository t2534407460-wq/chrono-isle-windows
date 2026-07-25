using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace ChronoIsle.App.Views;

public partial class LifeMainWindow : Window
{
    private readonly IServiceProvider services;

    public LifeMainWindow(ChronoIsle.App.ViewModels.LifeViewModel vm, IServiceProvider serviceProvider)
    {
        InitializeComponent();
        DataContext = vm;
        services = serviceProvider;
    }

    public void OpenSettings()
    {
        ((App)Application.Current).OpenLifeSettings();
    }

    public void SubmitQuickInput(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || DataContext is not ChronoIsle.App.ViewModels.LifeViewModel vm) return;
        vm.ChatInput = input.Trim();
        if (vm.SendCommand.CanExecute(null)) vm.SendCommand.Execute(null);
    }

    void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || IsTitleBarControl(e.OriginalSource as DependencyObject)) return;
        if (e.ClickCount == 2) ToggleMaximize();
        else DragMove();
    }

    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    void Close_Click(object sender, RoutedEventArgs e) => Close();
    void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    void Manage_Click(object sender, RoutedEventArgs e) => OpenManagement();

    public void OpenManagement(ItemNavigationTarget? target = null)
    {
        ((App)Application.Current).OpenLifeManagement(target);
    }

    void Today_Click(object sender, RoutedEventArgs e) => services.GetRequiredService<LifeIslandWindow>().OpenTodayPanel();
    void Calendar_Click(object sender, RoutedEventArgs e) => services.GetRequiredService<LifeIslandWindow>().OpenCalendarPanel();

    static bool IsTitleBarControl(DependencyObject? source)
    {
        for (var current = source; current is not null; current = current switch
        {
            FrameworkElement element when element.Parent is not null => element.Parent,
            FrameworkContentElement element => element.Parent,
            _ => VisualTreeHelper.GetParent(current)
        })
        {
            if (current is Button) return true;
        }
        return false;
    }
    void ChatInput_TextChanged(object sender, TextChangedEventArgs e) => ResizeChatInput(sender as TextBox);

    void ChatInput_SizeChanged(object sender, SizeChangedEventArgs e) => ResizeChatInput(sender as TextBox);

    static void ResizeChatInput(TextBox? box)
    {
        if (box is null || !box.IsLoaded) return;
        box.Dispatcher.BeginInvoke(() =>
        {
            var lineHeight = Math.Max(20d, box.FontSize * 1.5d);
            var lines = Math.Max(1, box.LineCount);
            var desiredHeight = Math.Clamp(lines * lineHeight + box.Padding.Top + box.Padding.Bottom, box.MinHeight, box.MaxHeight);
            if (double.IsNaN(box.Height) || Math.Abs(box.Height - desiredHeight) > .1)
                box.Height = desiredHeight;
            box.VerticalScrollBarVisibility = desiredHeight >= box.MaxHeight
                ? ScrollBarVisibility.Auto
                : ScrollBarVisibility.Disabled;
        }, DispatcherPriority.Background);
    }
}

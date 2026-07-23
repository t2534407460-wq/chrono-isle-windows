using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;

namespace OpenIsland.App.Views;

public partial class LifeMainWindow : Window
{
    private readonly IServiceProvider services;

    public LifeMainWindow(OpenIsland.App.ViewModels.LifeViewModel vm, IServiceProvider serviceProvider)
    {
        InitializeComponent();
        DataContext = vm;
        services = serviceProvider;
    }

    public void OpenSettings()
    {
        services.GetRequiredService<LifeIslandWindow>().CollapsePanel();
        var settings = services.GetRequiredService<LifeSettingsWindow>();
        settings.Owner = this;
        settings.ShowDialog();
    }

    public void SubmitQuickInput(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || DataContext is not OpenIsland.App.ViewModels.LifeViewModel vm) return;
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

    void Manage_Click(object sender, RoutedEventArgs e)
    {
        services.GetRequiredService<LifeIslandWindow>().CollapsePanel();
        var window = services.GetRequiredService<LifeManagementWindow>();
        window.Owner = this;
        window.ShowDialog();
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
    void ChatInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox box) return;
        var lines = Math.Max(1, box.LineCount);
        box.Height = Math.Min(120, Math.Max(40, lines * 22 + 12));
    }
}

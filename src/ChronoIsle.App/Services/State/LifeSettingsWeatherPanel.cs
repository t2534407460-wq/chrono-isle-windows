using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ChronoIsle.App.Views;
using ChronoIsle.App.Services.State;

namespace ChronoIsle.App.Views;

public partial class LifeSettingsWindow
{
    Border? weatherPanel;

    public void EnableWeatherSettings()
    {
        if (weatherPanel is not null) return;
        var root = Content as Grid;
        var stack = root?.Children.OfType<ScrollViewer>().FirstOrDefault()?.Content as StackPanel;
        if (stack is null) return;
        var store = new WeatherSettingsStore();
        var city = new TextBox { Text = store.Load().City, MinWidth = 220, Margin = new Thickness(0, 6, 8, 0) };
        var feedback = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(157, 214, 157)), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var save = new Button { Content = "保存城市", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 6, 0, 0) };
        save.Click += (_, _) =>
        {
            store.Save(new(city.Text));
            feedback.Text = string.IsNullOrWhiteSpace(city.Text) ? "已关闭天气显示。" : "已保存，灵动岛将在后台刷新天气。";
        };
        var form = new StackPanel();
        form.Children.Add(new TextBlock { Text = "天气（可选）", FontSize = 18, FontWeight = FontWeights.SemiBold });
        form.Children.Add(new TextBlock { Text = "仅在空闲态显示。请输入城市名称；没有网络或未设置城市时不会影响本地待办和提醒。", Foreground = new SolidColorBrush(Color.FromRgb(165, 165, 171)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 4) });
        var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        row.Children.Add(city); row.Children.Add(save); row.Children.Add(feedback);
        form.Children.Add(row);
        weatherPanel = new Border { Background = new SolidColorBrush(Color.FromRgb(36, 36, 40)), CornerRadius = new CornerRadius(12), Padding = new Thickness(18), Margin = new Thickness(0, 14, 0, 0), Child = form };
        stack.Children.Add(weatherPanel);
    }
}

internal static class LifeSettingsWeatherPanelBootstrap
{
    static Timer? timer;
    [ModuleInitializer]
    internal static void Initialize() => timer = new Timer(_ => TryAttach(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

    static void TryAttach()
    {
        var app = Application.Current;
        if (app?.Dispatcher.HasShutdownStarted != false) return;
        app.Dispatcher.BeginInvoke(() =>
        {
            foreach (var settings in app.Windows.OfType<LifeSettingsWindow>()) settings.EnableWeatherSettings();
        });
    }
}

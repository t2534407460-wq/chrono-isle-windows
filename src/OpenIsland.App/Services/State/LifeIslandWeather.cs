using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using OpenIsland.App.Services.State;
using OpenIsland.App.Views;

namespace OpenIsland.App.Views;

public partial class LifeIslandWindow
{
    DispatcherTimer? weatherTimer;
    readonly OpenMeteoWeatherService weatherService = new();
    bool weatherEnabled;

    public void EnableWeatherState()
    {
        if (weatherEnabled) return;
        weatherEnabled = true;
        weatherTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        weatherTimer.Tick += async (_, _) => await RefreshWeatherStateAsync();
        WeatherSettingsStore.Changed += OnWeatherSettingsChanged;
        weatherTimer.Start();
        _ = RefreshWeatherStateAsync();
    }

    void OnWeatherSettingsChanged() => Dispatcher.BeginInvoke(async () => await RefreshWeatherStateAsync());

    async Task RefreshWeatherStateAsync()
    {
        var city = new WeatherSettingsStore().Load().City;
        if (string.IsNullOrWhiteSpace(city))
        {
            islandState.Clear("weather:current", DateTimeOffset.UtcNow);
            return;
        }
        try
        {
            var weather = await weatherService.GetCurrentAsync(city);
            if (weather is null)
            {
                islandState.Clear("weather:current", DateTimeOffset.UtcNow);
                return;
            }
            islandState.Publish(new IslandStateSnapshot("weather:current", 5, weather.DisplayText,
                DateTimeOffset.UtcNow.AddMinutes(31), TimeSpan.FromSeconds(3), 10, IslandAnimationLevel.None), DateTimeOffset.UtcNow);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or KeyNotFoundException)

        {
            // Network weather is optional; keep tasks, reminders and the ordinary idle summary available.
            islandState.Clear("weather:current", DateTimeOffset.UtcNow);
        }
    }
}

internal static class LifeIslandWeatherBootstrap
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
            foreach (var island in app.Windows.OfType<LifeIslandWindow>()) island.EnableWeatherState();
        });
    }
}

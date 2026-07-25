using System.Net.Http;
using System.Text.Json;

namespace ChronoIsle.App.Services.State;

public sealed record WeatherSettings(string City);
public sealed record WeatherSnapshot(string City, double TemperatureCelsius, int WeatherCode, DateTimeOffset FetchedAtUtc)
{
    public string DisplayText => $"{City} · {Math.Round(TemperatureCelsius):0}°C · {WeatherCodeText(WeatherCode)}";

    static string WeatherCodeText(int code) => code switch
    {
        0 => "晴",
        1 or 2 => "少云",
        3 => "阴",
        45 or 48 => "雾",
        51 or 53 or 55 or 56 or 57 => "毛毛雨",
        61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => "雨",
        71 or 73 or 75 or 77 or 85 or 86 => "雪",
        95 or 96 or 99 => "雷雨",
        _ => "天气未知"
    };
}

public sealed class WeatherSettingsStore
{
    readonly string path;
    public static event Action? Changed;

    public WeatherSettingsStore(string? path = null) => this.path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChronoIsle", "weather-settings.json");

    public WeatherSettings Load()
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<WeatherSettings>(File.ReadAllText(path)) ?? new("") : new(""); }
        catch (JsonException) { return new(""); }
    }

    public void Save(WeatherSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Weather settings path has no directory."));
        File.WriteAllText(path, JsonSerializer.Serialize(settings with { City = settings.City.Trim() }));
        Changed?.Invoke();
    }
}

/// <summary>Retrieves only a user-selected city. A failed network request never changes task or reminder state.</summary>
public sealed class OpenMeteoWeatherService
{
    readonly HttpClient http;

    public OpenMeteoWeatherService(HttpClient? http = null) => this.http = http ?? new HttpClient();

    public async Task<WeatherSnapshot?> GetCurrentAsync(string city, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(city)) return null;
        var geocode = "https://geocoding-api.open-meteo.com/v1/search?name=" + Uri.EscapeDataString(city.Trim()) + "&count=1&language=zh&format=json";
        using var geocodeResponse = await http.GetAsync(geocode, cancellationToken);
        geocodeResponse.EnsureSuccessStatusCode();
        using var geocodeJson = JsonDocument.Parse(await geocodeResponse.Content.ReadAsStringAsync(cancellationToken));
        if (!geocodeJson.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) return null;
        var location = results[0];
        var latitude = location.GetProperty("latitude").GetDouble();
        var longitude = location.GetProperty("longitude").GetDouble();
        var resolvedName = location.TryGetProperty("name", out var name) ? name.GetString() ?? city.Trim() : city.Trim();
        var forecast = $"https://api.open-meteo.com/v1/forecast?latitude={latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}&longitude={longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}&current=temperature_2m,weather_code&timezone=auto";
        using var forecastResponse = await http.GetAsync(forecast, cancellationToken);
        forecastResponse.EnsureSuccessStatusCode();
        using var forecastJson = JsonDocument.Parse(await forecastResponse.Content.ReadAsStringAsync(cancellationToken));
        var current = forecastJson.RootElement.GetProperty("current");
        return new(resolvedName, current.GetProperty("temperature_2m").GetDouble(), current.GetProperty("weather_code").GetInt32(), DateTimeOffset.UtcNow);
    }
}

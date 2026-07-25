using System.Net;
using System.Net.Http;
using System.Text;
using ChronoIsle.App.Services.State;

namespace ChronoIsle.Tests;

public sealed class WeatherServiceTests
{
    [Fact]
    public async Task Selected_city_is_geocoded_then_rendered_as_current_weather()
    {
        using var http = new HttpClient(new WeatherHandler());
        var service = new OpenMeteoWeatherService(http);

        var result = await service.GetCurrentAsync("上海");

        Assert.NotNull(result);
        Assert.Equal("上海", result.City);
        Assert.Equal(28.4, result.TemperatureCelsius);
        Assert.Contains("雨", result.DisplayText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_city_never_starts_a_network_request()
    {
        var handler = new WeatherHandler();
        using var http = new HttpClient(handler);

        var result = await new OpenMeteoWeatherService(http).GetCurrentAsync(" ");

        Assert.Null(result);
        Assert.Equal(0, handler.Count);
    }

    sealed class WeatherHandler : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            var payload = request.RequestUri!.Host.StartsWith("geocoding", StringComparison.Ordinal)
                ? "{\"results\":[{\"name\":\"上海\",\"latitude\":31.23,\"longitude\":121.47}]}"
                : "{\"current\":{\"temperature_2m\":28.4,\"weather_code\":61}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
        }
    }
}

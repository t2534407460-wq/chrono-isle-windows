using ChronoIsle.App.Services;
using System.Reflection;

namespace ChronoIsle.Tests;

public sealed class ForegroundFpsServiceTests
{
    [Fact]
    public void LegacySessionCleanup_SelectsOnlyPresentMonSessionNames()
    {
        var method = typeof(ForegroundFpsService).GetMethod(
            "GetLegacyPresentMonSessionNames",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var names = Assert.IsAssignableFrom<IEnumerable<string>>(method!.Invoke(null,
            new object?[] { new[] { "ChronoIsleFps-25036", "ChronoIsleFps90160", "Eventlog-Security" } })!);

        Assert.Equal(["ChronoIsleFps-25036"], names);
    }

    [Fact]
    public void FrameRate_UsesTheLatestEventSecondInsteadOfWallClockTime()
    {
        var now = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new ForegroundFrameSample(now.AddMilliseconds(-1800)),
            new ForegroundFrameSample(now.AddMilliseconds(-1300)),
            new ForegroundFrameSample(now.AddMilliseconds(-800))
        };

        Assert.Equal(2d, ForegroundFrameRate.Calculate(samples, now));
    }

    [Fact]
    public void FrameRate_ReturnsNullWhenTheNewestEventIsStale()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.Null(ForegroundFrameRate.Calculate(
            [
                new ForegroundFrameSample(now.AddSeconds(-4)),
                new ForegroundFrameSample(now.AddSeconds(-3))
            ], now));
    }
}

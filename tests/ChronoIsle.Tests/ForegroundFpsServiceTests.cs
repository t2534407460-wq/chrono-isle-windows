using ChronoIsle.App.Services;

namespace ChronoIsle.Tests;

public sealed class ForegroundFpsServiceTests
{
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

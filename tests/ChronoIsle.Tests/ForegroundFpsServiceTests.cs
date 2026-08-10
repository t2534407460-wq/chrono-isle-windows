using ChronoIsle.App.Services;

namespace ChronoIsle.Tests;

public sealed class ForegroundFpsServiceTests
{
    [Fact]
    public void Parser_ReadsMsBetweenPresentsFromPresentMonCsv()
    {
        var parser = new PresentMonOutputParser();

        Assert.Null(parser.TryReadMillisecondsBetweenPresents("Application,ProcessID,MsBetweenPresents"));
        Assert.Equal(16.67d, parser.TryReadMillisecondsBetweenPresents("game.exe,42,16.67"));
        Assert.Null(parser.TryReadMillisecondsBetweenPresents("game.exe,42,NA"));
    }

    [Fact]
    public void FrameRate_UsesOnlySamplesFromTheLastSecond()
    {
        var now = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new PresentMonFrameSample(now.AddMilliseconds(-800), 16.67),
            new PresentMonFrameSample(now.AddMilliseconds(-400), 16.67),
            new PresentMonFrameSample(now.AddMilliseconds(-1200), 8.33)
        };

        Assert.InRange(PresentMonFrameRate.Calculate(samples, now)!.Value, 59.8, 60.2);
    }

    [Fact]
    public void FrameRate_ReturnsNullWhenThereIsNoRecentValidFrame()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.Null(PresentMonFrameRate.Calculate(
            [new PresentMonFrameSample(now.AddSeconds(-2), 16.67)], now));
    }
}

using ChronoIsle.App.Views;
using System.Reflection;

namespace ChronoIsle.Tests;

public sealed class AudioSpectrumDisplayTests
{
    [Fact]
    public void SpectrumDisplayHeights_UsesOverallAudioActivityToMoveEveryBar()
    {
        var silent = LifeIslandWindow.SpectrumDisplayHeights([0d, 0, 0, 0, 0, 0, 0], 5);
        var quiet = LifeIslandWindow.SpectrumDisplayHeights([.01, .03, .08, .05, .12, .04, .02], 5);
        var loud = LifeIslandWindow.SpectrumDisplayHeights([.04, .12, .32, .20, .48, .16, .08], 5);

        Assert.All(silent, height => Assert.Equal(4, height));
        Assert.NotEqual(quiet, loud);
        Assert.All(Enumerable.Zip(quiet, loud), pair => Assert.True(pair.Second > pair.First));
        Assert.All(quiet, height => Assert.InRange(height, 4, 20));
        Assert.All(loud, height => Assert.InRange(height, 4, 20));
        Assert.Equal(5, loud.Length);
        Assert.NotEqual(loud[0], loud[2]);
    }

    [Fact]
    public void SpectrumAnimation_RefreshesAtSixtyFpsAndFollowsBandChangesQuickly()
    {
        var serviceType = typeof(ChronoIsle.App.Services.Media.AudioSpectrumService);
        var interval = serviceType.GetField(
            "SpectrumPublishIntervalMilliseconds",
            BindingFlags.NonPublic | BindingFlags.Static);
        var smooth = serviceType.GetMethod(
            "SmoothSpectrumLevel",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.Equal(1024, ChronoIsle.App.Services.Media.AudioSpectrumAnalyzer.SampleCount);
        Assert.NotNull(interval);
        Assert.Equal(16, interval!.GetRawConstantValue());
        Assert.NotNull(smooth);
        Assert.Equal(.788, (double)smooth!.Invoke(null, [.2d, .8d])!, 3);
        Assert.Equal(.368, (double)smooth.Invoke(null, [.8d, .2d])!, 3);
    }
}

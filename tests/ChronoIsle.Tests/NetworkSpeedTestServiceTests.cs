using ChronoIsle.App.Services;

namespace ChronoIsle.Tests;

public sealed class NetworkSpeedTestServiceTests
{
    [Fact]
    public void SelectNode_UsesLowestMedianAcrossSuccessfulDomesticCandidates()
    {
        var tsinghua = new NetworkSpeedTestNode("清华大学", new Uri("https://iptv.tsinghua.edu.cn/st/"));
        var wuhan = new NetworkSpeedTestNode("武汉大学图书馆", new Uri("https://www.lib.whu.edu.cn/speedtest/backend/"));
        var unavailable = new NetworkSpeedTestNode("空样本节点", new Uri("https://empty.example/"));
        var selected = NetworkSpeedTestService.SelectNode(
        [
            new NetworkSpeedTestNodeProbe(unavailable, []),
            new NetworkSpeedTestNodeProbe(tsinghua, [10, 100]),
            new NetworkSpeedTestNodeProbe(wuhan, [60, 60])
        ]);

        Assert.Equal(tsinghua, selected);
    }

    [Theory]
    [InlineData(12_500_000L, 2.0, 50.0)]
    [InlineData(0L, 5.0, 0.0)]
    [InlineData(1L, 0.0, 0.0)]
    public void ToMegabitsPerSecond_UsesActualBytesAndElapsedTime(long bytes, double seconds, double expected)
    {
        Assert.Equal(expected, NetworkSpeedTestService.ToMegabitsPerSecond(bytes, TimeSpan.FromSeconds(seconds)), 3);
    }
}

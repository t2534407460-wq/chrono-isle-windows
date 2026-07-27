using ChronoIsle.App.Services.State;

namespace ChronoIsle.Tests;

public sealed class CollapsedIslandDisplayPolicyTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 2)]
    [InlineData(9, 2)]
    [InlineData(10, 3)]
    [InlineData(14, 3)]
    [InlineData(15, 4)]
    [InlineData(19, 4)]
    [InlineData(20, 1)]
    public void AllSourcesEnabled_RotatesEveryFiveSeconds(
        long elapsedSeconds,
        int expected)
    {
        var actual = CollapsedIslandDisplayPolicy.SelectSummary(
            showAgenda: true,
            showNetworkSpeed: true,
            showCpuUsage: true,
            showMemoryUsage: true,
            telemetryEnabled: true,
            elapsedSeconds: elapsedSeconds);

        Assert.Equal((CollapsedSummaryKind)expected, actual);
    }

    [Fact]
    public void TelemetryDisabled_ExcludesNetworkSpeedWithoutChangingAgenda()
    {
        Assert.Equal(
            CollapsedSummaryKind.Agenda,
            CollapsedIslandDisplayPolicy.SelectSummary(true, true, true, true, false, 5));
        Assert.Equal(
            CollapsedSummaryKind.None,
            CollapsedIslandDisplayPolicy.SelectSummary(false, true, true, true, false, 5));
    }

    [Theory]
    [InlineData(false, false, true, false, 3)]
    [InlineData(false, false, false, true, 4)]
    [InlineData(false, true, true, false, 2)]
    public void EnabledTelemetrySources_AreSelectedWithoutAgenda(
        bool showAgenda,
        bool showNetworkSpeed,
        bool showCpuUsage,
        bool showMemoryUsage,
        int expected)
    {
        Assert.Equal(
            (CollapsedSummaryKind)expected,
            CollapsedIslandDisplayPolicy.SelectSummary(
                showAgenda,
                showNetworkSpeed,
                showCpuUsage,
                showMemoryUsage,
                telemetryEnabled: true,
                elapsedSeconds: 0));
    }

    [Theory]
    [InlineData(40, 96)]
    [InlineData(240, 240)]
    [InlineData(500, 441)]
    public void CollapsedWidth_IsClampedToInteractiveBounds(double desired, double expected)
    {
        Assert.Equal(expected, CollapsedIslandDisplayPolicy.ClampWidth(desired));
    }

    [Theory]
    [InlineData(320, 400, false, 320)]
    [InlineData(320, 400, true, 400)]
    [InlineData(320, 240, true, 320)]
    [InlineData(320, 600, true, 441)]
    public void HoverWidth_KeepsCompactWidthOrExpandsToBoundedFullContent(
        double compactWidth,
        double fullContentWidth,
        bool pointerHover,
        double expected)
    {
        Assert.Equal(
            expected,
            CollapsedIslandDisplayPolicy.SelectWidth(
                compactWidth,
                fullContentWidth,
                pointerHover));
    }
}

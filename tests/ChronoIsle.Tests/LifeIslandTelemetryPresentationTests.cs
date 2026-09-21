using System.Reflection;
using ChronoIsle.App.Views;
using ChronoIsle.App.Services;

namespace ChronoIsle.Tests;

public sealed class LifeIslandTelemetryPresentationTests
{
    [Theory]
    [InlineData(NetworkHealth.Connected, null, "Brush.Danger")]
    [InlineData(NetworkHealth.Unstable, null, "Brush.Danger")]
    [InlineData(NetworkHealth.Offline, null, "Brush.Danger")]
    [InlineData(NetworkHealth.Connected, 20L, "Brush.Success")]
    [InlineData(NetworkHealth.Unstable, 500L, "Brush.Warning")]
    [InlineData(NetworkHealth.Offline, 20L, "Brush.Danger")]
    public void NetworkIcon_MissingLatencyIsRed(NetworkHealth health, long? latency, string expected)
    {
        Assert.Equal(expected, LifeIslandWindow.NetworkStatusBrushKey(health, latency));
    }

    [Theory]
    [InlineData(70, "Brush.TextSecondary")]
    [InlineData(70.01, "Brush.Warning")]
    [InlineData(90, "Brush.Warning")]
    [InlineData(90.01, "Brush.Danger")]
    public void CollapsedTelemetrySummary_UsesExpectedBrushAtUsageThresholds(
        double percent,
        string expectedBrushKey)
    {
        var method = typeof(LifeIslandWindow).GetMethod(
            "TelemetrySummaryBrushKey",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        Assert.Equal(expectedBrushKey, method!.Invoke(null, [percent]));
    }
}

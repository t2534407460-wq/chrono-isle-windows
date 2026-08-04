using System.Reflection;
using ChronoIsle.App.Views;

namespace ChronoIsle.Tests;

public sealed class LifeIslandTelemetryPresentationTests
{
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

using ChronoIsle.App.Services.State;

namespace ChronoIsle.Tests;

public sealed class CollapsedIslandDisplayPolicyTests
{
    [Theory]
    [InlineData(40, 96)]
    [InlineData(240, 240)]
    [InlineData(500, 441)]
    public void CollapsedWidth_IsClampedToInteractiveBounds(double desired, double expected)
    {
        Assert.Equal(expected, CollapsedIslandDisplayPolicy.ClampWidth(desired));
    }
}

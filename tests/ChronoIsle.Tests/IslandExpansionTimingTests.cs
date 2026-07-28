using ChronoIsle.App.Views;

namespace ChronoIsle.Tests;

public sealed class IslandExpansionTimingTests
{
    [Theory]
    [InlineData(true, true, 140)]
    [InlineData(false, true, 230)]
    [InlineData(true, false, 190)]
    [InlineData(false, false, 190)]
    public void AnimationDuration_SeparatesTaskbarExpansionFromClickDelay(
        bool taskbarDocked,
        bool expand,
        int expectedMilliseconds)
    {
        Assert.Equal(
            expectedMilliseconds,
            LifeIslandWindow.ExpansionDurationMilliseconds(taskbarDocked, expand));
    }
}

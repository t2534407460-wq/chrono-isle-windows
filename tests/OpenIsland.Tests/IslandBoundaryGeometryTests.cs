using System.Windows;
using OpenIsland.App.Services.State;

namespace OpenIsland.Tests;

public sealed class IslandBoundaryGeometryTests
{
    [Theory]
    [InlineData(-80, -10, 0, 0)]
    [InlineData(1800, 1060, 1626, 1037)]
    public void WindowBounds_KeepTheWholeIslandOnScreen(
        double left,
        double top,
        double expectedLeft,
        double expectedTop)
    {
        var bounds = new Rect(0, 0, 1920, 1080);

        var result = IslandPlacementGeometry.ClampRectToBounds(
            new Rect(left, top, 294, 43),
            bounds);

        Assert.Equal(expectedLeft, result.Left);
        Assert.Equal(expectedTop, result.Top);
    }

    [Fact]
    public void WindowBounds_SupportNegativeMonitorCoordinates()
    {
        var bounds = new Rect(-1200, 0, 1200, 1920);

        var result = IslandPlacementGeometry.ClampRectToBounds(
            new Rect(-1300, 1900, 294, 43),
            bounds);

        Assert.Equal(-1200, result.Left);
        Assert.Equal(1877, result.Top);
    }

    [Fact]
    public void TaskbarIcons_MoveIslandIntoNearestAvailableGap()
    {
        var bounds = new Rect(0, 0, 1920, 48);
        var occupied = new[]
        {
            new Rect(800, 0, 300, 48),
            new Rect(1700, 0, 220, 48)
        };

        var left = IslandPlacementGeometry.LeftAvoidingOccupiedRanges(
            900,
            294,
            bounds,
            occupied,
            padding: 6);

        Assert.Equal(1106, left);
    }

    [Fact]
    public void TaskbarIcons_KeepDesiredPositionWhenItIsAlreadyClear()
    {
        var bounds = new Rect(0, 0, 1920, 48);
        var occupied = new[] { new Rect(800, 0, 300, 48) };

        var left = IslandPlacementGeometry.LeftAvoidingOccupiedRanges(
            200,
            294,
            bounds,
            occupied,
            padding: 6);

        Assert.Equal(200, left);
    }

    [Fact]
    public void FullTaskbar_FallsBackToBoundaryOnlyFreeMovement()
    {
        var bounds = new Rect(0, 0, 1000, 48);
        var occupied = new[]
        {
            new Rect(0, 0, 430, 48),
            new Rect(570, 0, 430, 48)
        };

        var left = IslandPlacementGeometry.LeftAvoidingOccupiedRanges(
            350,
            294,
            bounds,
            occupied,
            padding: 6);

        Assert.Equal(350, left);
    }
}

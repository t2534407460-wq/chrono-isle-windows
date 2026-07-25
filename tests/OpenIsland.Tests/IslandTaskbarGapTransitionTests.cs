using System.Windows;
using OpenIsland.App.Services.State;

namespace OpenIsland.Tests;

public sealed class IslandTaskbarGapTransitionTests
{
    [Fact]
    public void PointerCrossingIconCluster_SelectsTheGapOnTheOtherSide()
    {
        var bounds = new Rect(0, 0, 1920, 48);
        var occupied = new[] { new Rect(800, 0, 300, 48) };

        var leftSide = IslandPlacementGeometry.PlaceAvoidingOccupiedRanges(
            500,
            294,
            bounds,
            occupied,
            padding: 6);
        var rightSide = IslandPlacementGeometry.PlaceAvoidingOccupiedRanges(
            1200,
            294,
            bounds,
            occupied,
            padding: 6);

        Assert.Equal(0, leftSide.GapIndex);
        Assert.Equal(1, rightSide.GapIndex);
        Assert.Equal(500, leftSide.Left);
        Assert.Equal(1200, rightSide.Left);
    }

    [Fact]
    public void PointerInsideIconCluster_SwitchesAtTheNearestGapBoundary()
    {
        var bounds = new Rect(0, 0, 1920, 48);
        var occupied = new[] { new Rect(800, 0, 300, 48) };

        var nearerLeft = IslandPlacementGeometry.PlaceAvoidingOccupiedRanges(
            700,
            294,
            bounds,
            occupied,
            padding: 6);
        var nearerRight = IslandPlacementGeometry.PlaceAvoidingOccupiedRanges(
            900,
            294,
            bounds,
            occupied,
            padding: 6);

        Assert.Equal(0, nearerLeft.GapIndex);
        Assert.Equal(500, nearerLeft.Left);
        Assert.Equal(1, nearerRight.GapIndex);
        Assert.Equal(1106, nearerRight.Left);
    }

    [Fact]
    public void NoFittingGap_ReportsFreeMovementFallback()
    {
        var bounds = new Rect(0, 0, 1000, 48);
        var occupied = new[]
        {
            new Rect(0, 0, 430, 48),
            new Rect(570, 0, 430, 48)
        };

        var placement = IslandPlacementGeometry.PlaceAvoidingOccupiedRanges(
            350,
            294,
            bounds,
            occupied,
            padding: 6);

        Assert.Equal(-1, placement.GapIndex);
        Assert.Equal(350, placement.Left);
    }
}

using System.Text.Json;
using System.Windows;
using OpenIsland.App;
using OpenIsland.App.Services.State;

namespace OpenIsland.Tests;

public sealed class IslandPlacementGeometryTests
{
    [Fact]
    public void BottomTaskbar_IsDerivedFromMonitorBoundsAndWorkArea()
    {
        var bounds = new Rect(0, 0, 1920, 1080);
        var workArea = new Rect(0, 0, 1920, 1032);

        var found = IslandPlacementGeometry.TryGetBottomTaskbar(bounds, workArea, out var taskbar);

        Assert.True(found);
        Assert.Equal(new Rect(0, 1032, 1920, 48), taskbar);
    }

    [Theory]
    [InlineData(27, false, true)]
    [InlineData(29, false, false)]
    [InlineData(47, true, true)]
    [InlineData(49, true, false)]
    public void SnapDecision_UsesSeparateEntryAndExitThresholds(double distance, bool alreadySnapped, bool expected)
    {
        Assert.Equal(expected, IslandPlacementGeometry.ShouldSnap(distance, alreadySnapped, 28, 48));
    }

    [Fact]
    public void HorizontalRatio_KeepsCollapsedAndExpandedWindowsOnScreen()
    {
        var bounds = new Rect(0, 0, 1920, 1080);

        Assert.Equal(0, IslandPlacementGeometry.LeftForHorizontalRatio(0, 294, bounds));
        Assert.Equal(813, IslandPlacementGeometry.LeftForHorizontalRatio(0.5, 294, bounds));
        Assert.Equal(1626, IslandPlacementGeometry.LeftForHorizontalRatio(1, 294, bounds));
        Assert.Equal(650, IslandPlacementGeometry.LeftForHorizontalRatio(0.5, 620, bounds));
    }

    [Fact]
    public void HorizontalRatio_SupportsNegativeMonitorCoordinates()
    {
        var bounds = new Rect(-1920, 0, 1920, 1080);
        var center = -1440d;

        var ratio = IslandPlacementGeometry.NormalizeHorizontalCenter(center, bounds);
        var left = IslandPlacementGeometry.LeftForHorizontalRatio(ratio, 294, bounds);

        Assert.Equal(0.25, ratio);
        Assert.Equal(-1587, left);
    }

    [Fact]
    public void HeaderAnchor_MovesExpandedWindowUpByContentHeight()
    {
        const double targetHeaderTop = 1034;

        var collapsedTop = IslandPlacementGeometry.WindowTopForHeaderAnchor(targetHeaderTop, 1);
        var expandedTop = IslandPlacementGeometry.WindowTopForHeaderAnchor(targetHeaderTop, 621);

        Assert.Equal(1033, collapsedTop);
        Assert.Equal(413, expandedTop);
        Assert.Equal(620, collapsedTop - expandedTop);
    }

    [Fact]
    public void LegacyPreferences_DefaultToTopPlacement()
    {
        const string json = """{"WindowsNotifications":true,"AssistantPersona":"Direct"}""";

        var preferences = JsonSerializer.Deserialize<LifePreferences>(json);

        Assert.NotNull(preferences);
        Assert.False(preferences.IslandTaskbarDocked);
        Assert.Null(preferences.IslandTaskbarMonitor);
        Assert.Null(preferences.IslandTaskbarHorizontalRatio);
    }

    [Fact]
    public void UpdatingOtherPreferences_PreservesTaskbarPlacement()
    {
        var current = new LifePreferences(
            true,
            "Direct",
            IslandTaskbarDocked: true,
            IslandTaskbarMonitor: @"\\.\DISPLAY2",
            IslandTaskbarHorizontalRatio: 0.75);

        var updated = current with { WindowsNotifications = false, AssistantPersona = "Friendly" };

        Assert.True(updated.IslandTaskbarDocked);
        Assert.Equal(@"\\.\DISPLAY2", updated.IslandTaskbarMonitor);
        Assert.Equal(0.75, updated.IslandTaskbarHorizontalRatio);
    }
}

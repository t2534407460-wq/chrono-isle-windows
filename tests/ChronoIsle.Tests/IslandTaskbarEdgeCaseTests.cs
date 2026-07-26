using System.Windows;
using ChronoIsle.App.Services.State;

namespace ChronoIsle.Tests;

public sealed class IslandTaskbarEdgeCaseTests
{
    [Theory]
    [MemberData(nameof(UnsupportedTaskbarLayouts))]
    public void NonBottomOrAutoHiddenTaskbar_IsNotDetected(Rect bounds, Rect workArea)
    {
        Assert.False(IslandPlacementGeometry.TryGetBottomTaskbar(bounds, workArea, out _));
    }

    [Fact]
    public void AutoHiddenTaskbar_UsesCurrentHeaderToCalculateExpansionHeight()
    {
        var bounds = new Rect(0, 0, 1920, 1080);

        var height = IslandPlacementGeometry.TaskbarExpandedContentMaxHeight(
            bounds,
            taskbar: null,
            currentHeaderTop: 1034,
            headerHeight: 43,
            margin: 12);

        Assert.Equal(1022, height);
    }

    [Fact]
    public void InvalidSavedRatio_FallsBackToTaskbarCenter()
    {
        var bounds = new Rect(0, 0, 1920, 1080);

        var left = IslandPlacementGeometry.LeftForHorizontalRatio(double.NaN, 294, bounds);

        Assert.Equal(813, left);
    }

    public static TheoryData<Rect, Rect> UnsupportedTaskbarLayouts => new()
    {
        // 自动隐藏：工作区与显示器边界相同，没有足够的常驻任务栏带。
        { new Rect(0, 0, 1920, 1080), new Rect(0, 0, 1920, 1080) },
        // 左侧任务栏：只缩小工作区宽度，不形成底部横向任务栏带。
        { new Rect(0, 0, 1920, 1080), new Rect(48, 0, 1872, 1080) },
        // 顶部任务栏：工作区顶部下移，但底边不变。
        { new Rect(0, 0, 1920, 1080), new Rect(0, 48, 1920, 1032) }
    };
}

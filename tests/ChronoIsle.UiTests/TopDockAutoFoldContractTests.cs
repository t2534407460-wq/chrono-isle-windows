using System.IO;

namespace ChronoIsle.UiTests;

public sealed class TopDockAutoFoldContractTests
{
    [Fact]
    public void TopDockedIsland_FoldsToSemanticIslandIndicatorStripWhenPointerLeaves()
    {
        var workspace = FindWorkspace();
        var source = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "Views",
            "LifeIslandWindow.xaml.cs"));
        var xaml = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "Views",
            "LifeIslandWindow.xaml"));

        Assert.Contains("Background=\"{DynamicResource Brush.Island}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<Border x:Name=\"TopDockStatusLight\" Height=\"6\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<Border x:Name=\"TopDockStatusPulse\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"{Binding Fill, ElementName=StatusLight}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("LinearGradientBrush", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("TopDockMarqueeTransform", xaml, StringComparison.Ordinal);
        Assert.Contains("CornerRadius=\"0,0,4,4\"", xaml, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment=\"Stretch\" VerticalAlignment=\"Bottom\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsHitTestVisible=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Grid.ColumnSpan=\"2\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("DropShadowEffect", xaml, StringComparison.Ordinal);
        Assert.Contains("MainBorder.Background = Brushes.Transparent;", source, StringComparison.Ordinal);
        Assert.Contains("TopDockStatusLight.Visibility = Visibility.Collapsed;", source, StringComparison.Ordinal);
        Assert.Contains("const double TopDockVisibleHeight = 6;", source, StringComparison.Ordinal);
        Assert.Contains("if (placement == IslandPlacement.Top) SetTopDockFolded(true);", source, StringComparison.Ordinal);
        Assert.Contains("SetTopDockFolded(false);", source, StringComparison.Ordinal);
        Assert.Contains("Duration = TimeSpan.FromMilliseconds(60)", source, StringComparison.Ordinal);
        Assert.Contains("Duration = TimeSpan.FromMilliseconds(90)", source, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMilliseconds(1800)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FoldedStatusPulseHalfCycle", source, StringComparison.Ordinal);
        Assert.Contains("AutoReverse = true", source, StringComparison.Ordinal);
        Assert.Contains("RepeatBehavior = RepeatBehavior.Forever", source, StringComparison.Ordinal);
        Assert.Contains("TopDockStatusPulse.BeginAnimation(OpacityProperty, null);", source, StringComparison.Ordinal);
        Assert.Contains("if (expanded) Collapse();\r\n            else SetTopDockFolded(true);", source, StringComparison.Ordinal);
    }

    static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ChronoIsle.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("ChronoIsle.sln was not found from the UI test host.");
    }
}

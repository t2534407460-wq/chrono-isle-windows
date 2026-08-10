using System.IO;

namespace ChronoIsle.UiTests;

public sealed class IslandDoubleClickResetContractTests
{
    [Fact]
    public void DoubleClick_CancelsPendingSingleClickAndRestoresDefaultPlacement()
    {
        var workspace = FindWorkspace();
        var source = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "Views",
            "LifeIslandWindow.xaml.cs"));

        Assert.Contains("const int HeaderDoubleClickMilliseconds = 280;", source, StringComparison.Ordinal);
        Assert.Contains("IsHeaderDoubleClick(pointer)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("e.ClickCount >= 2", source, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Windows.Forms.SystemInformation.DoubleClickTime", source, StringComparison.Ordinal);
        Assert.Contains("headerSingleClickTimer.Tick += (_, _) =>", source, StringComparison.Ordinal);
        Assert.Contains("headerSingleClickTimer.Stop();", source, StringComparison.Ordinal);
        Assert.Contains("ScheduleHeaderSingleClick();", source, StringComparison.Ordinal);
        Assert.Contains("dragStartScreenPixels", source, StringComparison.Ordinal);
        Assert.DoesNotContain("dragStart = e.GetPosition(this)", source, StringComparison.Ordinal);
        Assert.Contains("ResetToDefaultPlacement();", source, StringComparison.Ordinal);
        Assert.Contains("PositionAtTopCenter(initialScreen);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CaptureStartupPlacement();", source, StringComparison.Ordinal);

        var mouseUpStart = source.IndexOf("void Header_MouseUp", StringComparison.Ordinal);
        var interactiveStart = source.IndexOf("static bool IsInteractiveSource", mouseUpStart, StringComparison.Ordinal);
        Assert.True(mouseUpStart >= 0 && interactiveStart > mouseUpStart);
        var mouseUp = source[mouseUpStart..interactiveStart];
        Assert.Contains("ScheduleHeaderSingleClick();", mouseUp, StringComparison.Ordinal);
        Assert.DoesNotContain("ToggleExpanded();", mouseUp, StringComparison.Ordinal);

        Assert.Contains("++expandedContentAnimationVersion;", source, StringComparison.Ordinal);
        Assert.Contains("if (contentAnimationVersion != expandedContentAnimationVersion) return;", source, StringComparison.Ordinal);
        Assert.Contains("ExpandedScrollViewer.MinHeight = 0;", source, StringComparison.Ordinal);
        Assert.Contains("ExpandedContent.Visibility = Visibility.Collapsed;", source, StringComparison.Ordinal);
    }

    static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ChronoIsle.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("ChronoIsle.sln was not found from the UI test host.");
    }
}

using System.IO;

namespace ChronoIsle.UiTests;

public sealed class MusicLineLyricsContractTests
{
    [Fact]
    public void MusicTakeover_UsesTrackViewUntilTheMusicLineIsHovered()
    {
        var workspace = FindWorkspace();
        var island = File.ReadAllText(Path.Combine(
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

        Assert.Contains("void CollapsedMediaTrack_MouseEnter", island, StringComparison.Ordinal);
        Assert.Contains("void CollapsedMediaTrack_MouseLeave", island, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateMediaPlayPauseIcons(!current.IsPlaying);", island, StringComparison.Ordinal);
        Assert.Contains("CollapsedMediaControls.Opacity = 0;", island, StringComparison.Ordinal);
        Assert.Contains("CollapsedMediaControlsTranslate.Y = 12;", island, StringComparison.Ordinal);
        Assert.Contains("if (visible == collapsedMediaControlsVisible) return;", island, StringComparison.Ordinal);
        Assert.Contains("CollapsedMediaControlsTranslate.Y = 0;", island, StringComparison.Ordinal);
        Assert.Contains("Transition(12, 0, 160)", island, StringComparison.Ordinal);
        Assert.Contains("Transition(0, 1, 160)", island, StringComparison.Ordinal);
        Assert.Contains("AnimateCollapsedMediaControls(true);", island, StringComparison.Ordinal);
        Assert.Contains("AnimateCollapsedMediaControls(false);", island, StringComparison.Ordinal);
        Assert.Contains("var controlsLift = Transition(0, -12, 140);", island, StringComparison.Ordinal);
        Assert.Contains("controlsLift.Completed", island, StringComparison.Ordinal);
        Assert.Contains("bool musicModeActive;", island, StringComparison.Ordinal);
        Assert.Contains("if (currentMusic?.IsPlaying == true) musicModeActive = true;", island, StringComparison.Ordinal);
        Assert.Contains("var collapsedMedia = musicModeActive ? currentMusic : null;", island, StringComparison.Ordinal);
        Assert.Contains("currentPreferences.IslandShowMusicMode &&", island, StringComparison.Ordinal);
        Assert.Contains("media.Current is { IsMusic: true };", island, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CollapsedMediaTrack\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"Transparent\" MouseEnter=\"CollapsedMediaTrack_MouseEnter\" MouseLeave=\"CollapsedMediaTrack_MouseLeave\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CollapsedMediaControls\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CollapsedMediaTrackButton\" Grid.Column=\"2\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"实时音轨，悬浮显示播放控制\"", xaml, StringComparison.Ordinal);
        Assert.Equal(5, Enumerable.Range(0, 5).Count(index => xaml.Contains($"x:Name=\"CollapsedSpectrum{index}\"", StringComparison.Ordinal)));
        Assert.DoesNotContain("x:Name=\"CollapsedSpectrum5\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"CollapsedMediaStatusLight\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource CollapsedHeaderButton}\" Background=\"Transparent\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Width=\"70\" Height=\"34\" CornerRadius=\"15\" Background=\"Transparent\"", xaml, StringComparison.Ordinal);
        Assert.Equal(5, Enumerable.Range(0, 5).Count(index => xaml.Contains($"x:Name=\"CollapsedSpectrum{index}\" Width=\"4\" Height=\"4\"", StringComparison.Ordinal)));
        Assert.Contains("Orientation=\"Horizontal\" HorizontalAlignment=\"Center\" VerticalAlignment=\"Center\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Height=\"34\" Margin=\"0\" UseLayoutRounding=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Equal(5, Enumerable.Range(0, 5).Count(index => xaml.Contains($"x:Name=\"CollapsedSpectrum{index}\" Width=\"4\" Height=\"4\" CornerRadius=\"2\" Margin=\"2,0\" Background=\"{{DynamicResource Brush.Accent}}\"", StringComparison.Ordinal)));
        Assert.Contains("Grid.Column=\"2\" Orientation=\"Horizontal\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CollapsedMediaTrackButton.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;", island, StringComparison.Ordinal);
        Assert.DoesNotContain("CollapsedMediaTrack.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;", island, StringComparison.Ordinal);
        Assert.Contains("SpectrumDisplayHeights(spectrum, bars.Length)", island, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplySpectrumPalette", island, StringComparison.Ordinal);
        Assert.Contains("audioSpectrum.RefreshCaptureDevice();", island, StringComparison.Ordinal);
        Assert.DoesNotContain("BeginAnimation(HeightProperty", island, StringComparison.Ordinal);
        Assert.DoesNotContain("MediaDashboardTab", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("LyricCurrent", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void MusicTakeover_CanBeToggledFromTheContextMenu()
    {
        var workspace = FindWorkspace();
        var island = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "Views",
            "LifeIslandWindow.xaml.cs"));
        var models = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "LifeModels.cs"));

        Assert.Contains("bool IslandShowMusicMode = true", models, StringComparison.Ordinal);
        Assert.Contains("case IslandQuickAction.ToggleMusicMode: ToggleMusicMode(); break;", island, StringComparison.Ordinal);
        Assert.Contains("collapsedPreferences = current with { IslandShowMusicMode = !current.IslandShowMusicMode };", island, StringComparison.Ordinal);
        Assert.Contains("if (!collapsedPreferences.IslandShowMusicMode) musicModeActive = false;", island, StringComparison.Ordinal);
        Assert.Contains("Refresh();", island, StringComparison.Ordinal);
    }

    [Fact]
    public void MusicTakeover_DoesNotStartTheLegacyLyricsModule()
    {
        var workspace = FindWorkspace();
        var island = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "Views",
            "LifeIslandWindow.xaml.cs"));
        var app = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "App.xaml.cs"));
        var settings = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "Views",
            "LifeSettingsWindow.xaml"));

        Assert.Contains("collection.AddSingleton<LyricsService>();", app, StringComparison.Ordinal);
        Assert.DoesNotContain("SourceLyricTimeline", island, StringComparison.Ordinal);
        Assert.Contains("LyricsEnabled", settings, StringComparison.Ordinal);
        Assert.Contains("LyricsOffset", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void MusicControls_HandleAClosedSystemSessionWithoutEscapingToTheUiDispatcher()
    {
        var workspace = FindWorkspace();
        var media = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "Services",
            "MediaSessionService.cs"));

        Assert.Contains("async Task ControlAsync(", media, StringComparison.Ordinal);
        Assert.Contains("catch (Exception exception)", media, StringComparison.Ordinal);
        Assert.Contains("var handled = session is not null && await operation(session);", media, StringComparison.Ordinal);
        Assert.Contains("if (!handled) desktop.Control(desktopCommand, Current?.SourceAppId);", media, StringComparison.Ordinal);
        Assert.Contains("activeSession = systemSessionIsMusic ? session : null;", media, StringComparison.Ordinal);
        Assert.Contains("desktop.Control(desktopCommand, Current?.SourceAppId);", media, StringComparison.Ordinal);
        Assert.Contains("await RefreshAsync();", media, StringComparison.Ordinal);
    }

    [Fact]
    public void NetEaseMusicControls_UseTheVerifiedGlobalShortcutSender()
    {
        var workspace = FindWorkspace();
        var desktop = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "Services",
            "DesktopMusicSessionDetector.cs"));

        Assert.Contains("static extern void keybd_event", desktop, StringComparison.Ordinal);
        Assert.Contains("keybd_event((byte)ControlKey, 0, 0, UIntPtr.Zero);", desktop, StringComparison.Ordinal);
        Assert.Contains("keybd_event((byte)ControlKey, 0, KeyUp, UIntPtr.Zero);", desktop, StringComparison.Ordinal);
        Assert.Contains("command != DesktopMediaCommand.TogglePlayPause", desktop, StringComparison.Ordinal);
        Assert.Contains("_ => MediaPlayPause", desktop, StringComparison.Ordinal);
        Assert.Contains("keybd_event((byte)virtualKey, 0, 0, UIntPtr.Zero);", desktop, StringComparison.Ordinal);
        Assert.Contains("keybd_event((byte)virtualKey, 0, KeyUp, UIntPtr.Zero);", desktop, StringComparison.Ordinal);
        Assert.DoesNotContain("SendInput((uint)inputs.Length", desktop, StringComparison.Ordinal);
    }

    [Fact]
    public void CollapsedMusicControls_ArePinnedAfterClickAndExcludedFromHeaderGestures()
    {
        var workspace = FindWorkspace();
        var island = File.ReadAllText(Path.Combine(
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

        Assert.Contains("Click=\"CollapsedMediaTrackButton_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("bool collapsedMediaControlsPinned;", island, StringComparison.Ordinal);
        Assert.Contains("void CollapsedMediaTrackButton_Click", island, StringComparison.Ordinal);
        Assert.Contains("if (!collapsedMediaControlsPinned) AnimateCollapsedMediaControls(false);", island, StringComparison.Ordinal);
        Assert.Contains("if (IsCollapsedMediaControlSource(e.OriginalSource as DependencyObject)) return;", island, StringComparison.Ordinal);
        Assert.Contains("static bool IsCollapsedMediaControlSource", island, StringComparison.Ordinal);
    }

    static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ChronoIsle.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("ChronoIsle.sln was not found from the UI test host.");
    }
}

using ChronoIsle.App.Services.Media;

namespace ChronoIsle.Tests;

public sealed class MineradioMetadataFallbackTests
{
    [Theory]
    [InlineData("Mineradio", "Mineradio", "")]
    [InlineData("Mineradio", "", "")]
    public void GenericMineradioWindowMetadata_UsesPlaybackSnapshot(
        string processName,
        string title,
        string artist)
    {
        Assert.True(DesktopMusicSessionDetector.ShouldUseMineradioSnapshot(
            processName,
            title,
            artist));
    }

    [Fact]
    public void ParsedMineradioWindowMetadata_DoesNotNeedPlaybackSnapshot()
    {
        Assert.False(DesktopMusicSessionDetector.ShouldUseMineradioSnapshot(
            "Mineradio",
            "认真地老去",
            "张希 / 曹方"));
    }

    [Fact]
    public void GenericMineradioSystemTitle_YieldsToRicherLocalMetadata()
    {
        var desktop = new DesktopMusicSession(
            "Mineradio",
            "认真地老去",
            "张希 / 曹方",
            false,
            null,
            null,
            DateTimeOffset.UtcNow);

        Assert.True(MediaSessionService.ShouldPreferActiveDesktop(
            true,
            false,
            desktop,
            "unknown.desktop.player",
            "Mineradio"));
        Assert.False(MediaSessionService.ShouldPreferActiveDesktop(
            true,
            false,
            desktop,
            "com.bytedance.douyin",
            "抖音"));
    }
}

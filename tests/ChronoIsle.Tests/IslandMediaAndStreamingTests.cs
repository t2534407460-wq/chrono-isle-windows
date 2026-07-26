using System.Drawing;
using System.Net;
using System.Net.Http;
using System.Text;
using ChronoIsle.App;
using ChronoIsle.App.Services;
using ChronoIsle.App.Services.Media;

namespace ChronoIsle.Tests;

public sealed class IslandMediaAndStreamingTests
{
    [Fact]
    public void LrcParser_ParsesFractionsAndMultipleTimestamps()
    {
        var lines = LrcParser.Parse("""
            [ar:Artist]
            [00:01.20]第一句
            [00:02.345][00:03.4]重复时间句
            """);

        Assert.Equal(3, lines.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(1200), lines[0].Timestamp);
        Assert.Equal(TimeSpan.FromMilliseconds(2345), lines[1].Timestamp);
        Assert.Equal(TimeSpan.FromMilliseconds(3400), lines[2].Timestamp);
        Assert.Equal("重复时间句", lines[2].Text);
    }

    [Fact]
    public void LrcParser_ConvertsPlainLyricsIntoProgressiveLines()
    {
        var lines = LrcParser.ParsePlain("""
            第一句

            第二句
            第三句
            """, 180);

        Assert.Equal(["第一句", "第二句", "第三句"], lines.Select(line => line.Text));
        Assert.True(lines[0].Timestamp >= TimeSpan.Zero);
        Assert.True(lines[0].Timestamp < lines[1].Timestamp);
        Assert.True(lines[1].Timestamp < lines[2].Timestamp);
    }

    [Fact]
    public void LyricsDocument_UsesCalibrationOffset()
    {
        var document = new LyricsDocument("track",
        [
            new LyricLine(TimeSpan.FromSeconds(1), "一"),
            new LyricLine(TimeSpan.FromSeconds(2), "二")
        ], "test", DateTimeOffset.UtcNow);

        Assert.Equal(0, document.CurrentLineIndex(TimeSpan.FromMilliseconds(1600)));
        Assert.Equal(1, document.CurrentLineIndex(TimeSpan.FromMilliseconds(1600), 500));
    }

    [Fact]
    public void LrcParser_ConvertsTraditionalChineseToSimplified()
    {
        var line = Assert.Single(LrcParser.Parse("[00:01.00]我看過沙漠下暴雨"));

        Assert.Equal("我看过沙漠下暴雨", line.Text);
    }

    [Fact]
    public void LyricsSourceMatcher_UsesNextLineToResolveRepeatedLyrics()
    {
        LyricLine[] lines =
        [
            new(TimeSpan.FromSeconds(1), "重复副歌"),
            new(TimeSpan.FromSeconds(2), "第一段后句"),
            new(TimeSpan.FromSeconds(3), "重复副歌"),
            new(TimeSpan.FromSeconds(4), "第二段后句")
        ];

        var index = LyricsSourceMatcher.FindCurrentLine(lines, "重 复 副 歌\n第 二 段 后 句");

        Assert.Equal(2, index);
    }

    [Fact]
    public void SourceLyricTimeline_KeepsMovingWithinSameLineAndReanchorsAfterConfirmedSeek()
    {
        var start = new DateTimeOffset(2026, 7, 27, 10, 0, 0, TimeSpan.Zero);
        var document = new LyricsDocument("track",
        [
            new LyricLine(TimeSpan.FromSeconds(10), "第一句"),
            new LyricLine(TimeSpan.FromSeconds(20), "第二句"),
            new LyricLine(TimeSpan.FromSeconds(50), "跳转后句"),
            new LyricLine(TimeSpan.FromSeconds(60), "后一句")
        ], "test", start, 100);
        var timeline = new SourceLyricTimeline();

        Assert.Null(timeline.Update("track", "第一句\n第二句", true, start, document, start));
        Assert.Equal(TimeSpan.FromSeconds(11),
            timeline.Update("track", "第一句\n第二句", true, start.AddSeconds(1), document, start.AddSeconds(1)));
        Assert.Equal(TimeSpan.FromSeconds(14),
            timeline.Update("track", "第一句\n第二", true, start.AddSeconds(4), document, start.AddSeconds(4)));
        Assert.Equal(TimeSpan.FromSeconds(15),
            timeline.Update("track", "跳转后句\n后一句", true, start.AddSeconds(5), document, start.AddSeconds(5)));
        Assert.Equal(TimeSpan.FromSeconds(51),
            timeline.Update("track", "跳转后句\n后一句", true, start.AddSeconds(6), document, start.AddSeconds(6)));
        Assert.Equal(TimeSpan.FromSeconds(53),
            timeline.Update("track", null, false, null, document, start.AddSeconds(8)));
        Assert.Equal(TimeSpan.FromSeconds(53),
            timeline.Update("track", null, false, null, document, start.AddSeconds(12)));
    }
    [Fact]
    public void LyricsMatcher_PrefersExactMetadata()
    {
        var exact = LyricsMatcher.Score("Song A", "Artist", TimeSpan.FromSeconds(180), "Song A", "Artist", 181);
        var partial = LyricsMatcher.Score("Song A", "Artist", TimeSpan.FromSeconds(180), "Song", "Someone", 210);

        Assert.True(exact > partial);
        Assert.True(exact >= 90);
    }

    [Theory]
    [InlineData("爱我还是他 - 陶喆", "爱我还是他", "陶喆")]
    [InlineData("Song - Live - Artist", "Song - Live", "Artist")]
    [InlineData("歌曲 — 歌手", "歌曲", "歌手")]
    public void DesktopMusicTitleParser_ExtractsTrackAndArtist(
        string windowTitle,
        string expectedTitle,
        string expectedArtist)
    {
        Assert.True(DesktopMusicTitleParser.TryParse(windowTitle, out var title, out var artist));
        Assert.Equal(expectedTitle, title);
        Assert.Equal(expectedArtist, artist);
    }

    [Theory]
    [InlineData("网易云音乐")]
    [InlineData("cloudmusic.exe")]
    [InlineData("QQMusic.exe")]
    [InlineData("Spotify.exe")]
    [InlineData("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic")]
    public void MediaSourceClassifier_RecognizesMusicPlayers(string sourceAppId)
    {
        Assert.True(MediaSourceClassifier.IsMusicPlayer(sourceAppId));
    }

    [Theory]
    [InlineData("douyin.exe")]
    [InlineData("抖音")]
    [InlineData("bilibili.exe")]
    [InlineData("Google Chrome")]
    [InlineData("Microsoft Edge")]
    public void MediaSourceClassifier_RejectsVideoSources(string sourceAppId)
    {
        Assert.False(MediaSourceClassifier.IsMusicPlayer(sourceAppId));
    }
    [Fact]
    public void DesktopMusicTitleParser_RejectsGenericPlayerTitle()
    {
        Assert.False(DesktopMusicTitleParser.TryParse("网易云音乐", out _, out _));
    }

    [Fact]
    public void DesktopMusicProgressDetector_FindsBarAcrossWindowFrameOffsets()
    {
        using var bitmap = new Bitmap(400, 200);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Black);
        for (var x = 8; x <= 108; x++)
            bitmap.SetPixel(x, 100, Color.FromArgb(255, 245, 70, 90));

        var ratio = DesktopMusicSessionDetector.DetectNetEaseProgress(bitmap);

        Assert.NotNull(ratio);
        Assert.InRange(ratio.Value, 0.25, 0.28);
    }

    [Fact]
    public void DesktopMusicProgressDetector_IgnoresRedControlsOutsideProgressBand()
    {
        using var bitmap = new Bitmap(400, 200);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Black);
        for (var x = 180; x <= 220; x++)
            bitmap.SetPixel(x, 160, Color.FromArgb(255, 245, 70, 90));

        Assert.Null(DesktopMusicSessionDetector.DetectNetEaseProgress(bitmap));
    }

    [Fact]
    public void DesktopMusicPlaybackDetector_DistinguishesPlayAndPauseIcons()
    {
        using var playBitmap = new Bitmap(200, 200);
        using var pauseBitmap = new Bitmap(200, 200);
        using (var graphics = Graphics.FromImage(playBitmap))
        {
            graphics.Clear(Color.Black);
            graphics.FillPolygon(Brushes.White,
            new Point[]
            {
                new Point(95, 150),
                new Point(95, 170),
                new Point(108, 160)
            });
        }
        using (var graphics = Graphics.FromImage(pauseBitmap))
        {
            graphics.Clear(Color.Black);
            graphics.FillRectangle(Brushes.White, 94, 150, 5, 21);
            graphics.FillRectangle(Brushes.White, 102, 150, 5, 21);
        }

        Assert.Equal(false, DesktopMusicSessionDetector.DetectNetEasePlayback(playBitmap));
        Assert.Equal(true, DesktopMusicSessionDetector.DetectNetEasePlayback(pauseBitmap));
    }

    [Fact]
    public void DesktopMediaTimeline_AdvancesFromLastProgressSampleOnlyWhilePlaying()
    {
        var now = new DateTimeOffset(2026, 7, 27, 10, 0, 0, TimeSpan.Zero);
        var sampledAt = now - TimeSpan.FromSeconds(10);

        Assert.Equal(
            TimeSpan.FromSeconds(60),
            DesktopMediaTimeline.EstimatePosition(0.25, 200, sampledAt, now, true));
        Assert.Equal(
            TimeSpan.FromSeconds(50),
            DesktopMediaTimeline.EstimatePosition(0.25, 200, sampledAt, now, false));
    }

    [Fact]
    public void MissingMediaTimelineClock_AdvancesPausesAndResetsOnTrackChange()
    {
        var clock = new MissingMediaTimelineClock();
        var start = new DateTimeOffset(2026, 7, 27, 10, 0, 0, TimeSpan.Zero);

        Assert.Equal(TimeSpan.Zero, clock.Update("track-a", TimeSpan.Zero, true, start).Position);
        Assert.Equal(
            TimeSpan.FromSeconds(2),
            clock.Update("track-a", TimeSpan.Zero, true, start.AddSeconds(2)).Position);
        Assert.Equal(
            TimeSpan.FromSeconds(4),
            clock.Update("track-a", TimeSpan.Zero, false, start.AddSeconds(4)).Position);
        Assert.Equal(
            TimeSpan.FromSeconds(4),
            clock.Update("track-a", TimeSpan.Zero, false, start.AddSeconds(7)).Position);
        Assert.Equal(
            TimeSpan.Zero,
            clock.Update("track-b", TimeSpan.Zero, true, start.AddSeconds(8)).Position);
    }

    [Fact]
    public void MissingMediaTimelineClock_RestoresRecentTrackAcrossRestart()
    {
        var statePath = Path.Combine(
            Path.GetTempPath(),
            $"chrono-isle-media-timeline-{Guid.NewGuid():N}.json");
        var start = new DateTimeOffset(2026, 7, 27, 10, 0, 0, TimeSpan.Zero);
        try
        {
            var first = new MissingMediaTimelineClock(statePath);
            first.Update("track-a", TimeSpan.Zero, true, start);
            first.Update("track-a", TimeSpan.Zero, true, start.AddSeconds(30));

            var restored = new MissingMediaTimelineClock(statePath);

            Assert.Equal(
                TimeSpan.FromSeconds(35),
                restored.Update("track-a", TimeSpan.Zero, true, start.AddSeconds(35)).Position);
        }
        finally
        {
            File.Delete(statePath);
        }
    }

    [Fact]
    public void MissingMediaTimelineClock_DiscardsExpiredPersistedTrack()
    {
        var statePath = Path.Combine(
            Path.GetTempPath(),
            $"chrono-isle-media-timeline-{Guid.NewGuid():N}.json");
        var start = new DateTimeOffset(2026, 7, 27, 10, 0, 0, TimeSpan.Zero);
        try
        {
            var first = new MissingMediaTimelineClock(statePath);
            first.Update("track-a", TimeSpan.Zero, true, start);
            first.Update("track-a", TimeSpan.Zero, true, start.AddSeconds(30));

            var restored = new MissingMediaTimelineClock(statePath);

            Assert.Equal(
                TimeSpan.Zero,
                restored.Update("track-a", TimeSpan.Zero, true, start.AddMinutes(3)).Position);
        }
        finally
        {
            File.Delete(statePath);
        }
    }
    [Fact]
    public void DesktopMediaTimeline_InterpolatesEstimatedPositionForKaraokeFrames()
    {
        var now = new DateTimeOffset(2026, 7, 27, 10, 0, 1, TimeSpan.Zero);

        Assert.Equal(
            TimeSpan.FromSeconds(12),
            DesktopMediaTimeline.EstimatePosition(
                TimeSpan.FromSeconds(11),
                200,
                now.AddSeconds(-1),
                now,
                true));
    }

    [Fact]
    public void DesktopMediaTimeline_ReanchorsToLatestSourcePositionAfterSeek()
    {
        var beforeSeek = new DateTimeOffset(2026, 7, 27, 10, 0, 0, TimeSpan.Zero);
        var afterSeek = beforeSeek.AddSeconds(1);

        Assert.Equal(
            TimeSpan.FromSeconds(27),
            DesktopMediaTimeline.EstimatePosition(
                TimeSpan.FromSeconds(26),
                263,
                beforeSeek,
                afterSeek,
                true));
        Assert.Equal(
            TimeSpan.FromSeconds(159.25),
            DesktopMediaTimeline.EstimatePosition(
                TimeSpan.FromSeconds(159),
                263,
                afterSeek,
                afterSeek.AddMilliseconds(250),
                true));
    }
    [Fact]
    public void DesktopMediaTimeline_WrapsStaleOverrunInsteadOfStickingAtEnd()
    {
        var now = new DateTimeOffset(2026, 7, 27, 10, 0, 0, TimeSpan.Zero);

        Assert.Equal(
            TimeSpan.FromSeconds(10),
            DesktopMediaTimeline.EstimatePosition(
                TimeSpan.FromSeconds(370),
                180,
                now,
                now,
                false));
    }

    [Fact]
    public void MediaSessionService_UsesMatchingDesktopProgressWhenSystemTimelineIsEmpty()
    {
        var sampledAt = new DateTimeOffset(2026, 7, 27, 10, 0, 0, TimeSpan.Zero);
        var fallback = new DesktopMusicSession(
            "网易云音乐",
            "奇妙能力歌",
            "陈粒",
            true,
            0.17,
            null,
            sampledAt);

        Assert.True(MediaSessionService.ShouldUseDesktopProgress(
            fallback,
            "奇妙能力歌",
            "陈粒",
            TimeSpan.Zero));
        Assert.False(MediaSessionService.ShouldUseDesktopProgress(
            fallback,
            "世间美好与你环环相扣",
            "柏松",
            TimeSpan.Zero));
        Assert.False(MediaSessionService.ShouldUseDesktopProgress(
            fallback,
            "奇妙能力歌",
            "陈粒",
            TimeSpan.FromMinutes(4)));
    }

    [Fact]
    public void MediaSessionService_KeepsPausedSystemSessionOverDesktopFallback()
    {
        var fallback = new DesktopMusicSession(
            "网易云音乐",
            "我记得",
            "赵雷",
            true,
            0.25,
            null,
            DateTimeOffset.UtcNow);

        Assert.False(MediaSessionService.ShouldPublishDesktopFallback(true, fallback));
        Assert.True(MediaSessionService.ShouldPublishDesktopFallback(false, fallback));
        Assert.False(MediaSessionService.ShouldPublishDesktopFallback(false, null));
    }

    [Fact]
    public void FullscreenDetector_AllowsSmallFrameTolerance()
    {
        var monitor = new Rectangle(0, 0, 1920, 1080);

        Assert.True(FullscreenAvoidanceService.CoversMonitor(new Rectangle(-1, 0, 1922, 1081), monitor));
        Assert.False(FullscreenAvoidanceService.CoversMonitor(new Rectangle(0, 0, 1600, 900), monitor));
    }

    [Fact]
    public async Task OpenAiStreaming_EmitsSseContentDeltas()
    {
        const string sse = """
            data: {"choices":[{"delta":{"role":"assistant"}}]}

            data: {"choices":[{"delta":{"content":"你"}}]}

            data: {"choices":[{"delta":{"content":"好"}}]}

            data: [DONE]

            """;
        var http = new HttpClient(new StaticResponseHandler(sse));
        var service = new OpenAiChatService(http);
        var chunks = new List<string>();

        await foreach (var chunk in service.StreamComplete(
                           new ProviderSettings("https://example.test/v1", "model", "key"),
                           [new ModelMessage("user", "test")]))
            chunks.Add(chunk);

        Assert.Equal(["你", "好"], chunks);
    }

    sealed class StaticResponseHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
            });
    }
}

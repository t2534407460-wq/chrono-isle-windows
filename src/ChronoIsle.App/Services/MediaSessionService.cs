using System.Net.Http;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace ChronoIsle.App.Services.Media;

public static class MediaSourceClassifier
{
    static readonly string[] MusicPlayerTokens =
    [
        "cloudmusic", "网易云", "qqmusic", "qq 音乐", "spotify",
        "itunes", "applemusic", "apple music", "zunemusic", "music.ui",
        "musicbee", "foobar2000", "aimp", "winamp", "kugou", "酷狗",
        "kuwo", "酷我", "mineradio", "com.mineradio.desktop"
    ];

    public static bool IsMusicPlayer(string? sourceAppId) =>
        !string.IsNullOrWhiteSpace(sourceAppId) &&
        MusicPlayerTokens.Any(token => sourceAppId.Contains(token, StringComparison.OrdinalIgnoreCase));
}

public sealed record MediaSessionSnapshot(
    string SourceAppId,
    string Title,
    string Artist,
    string? AlbumTitle,
    TimeSpan Position,
    TimeSpan Duration,
    bool IsPlaying,
    double? ProgressRatio = null,
    byte[]? Artwork = null,
    DateTimeOffset? ProgressSampledAtUtc = null,
    string? SourceLyric = null,
    DateTimeOffset? SourceLyricSampledAtUtc = null)
{
    public bool IsMusic => MediaSourceClassifier.IsMusicPlayer(SourceAppId);
    public string TrackKey => $"{SourceAppId}\n{Title}\n{Artist}\n{Math.Round(Duration.TotalSeconds)}";
}

/// <summary>读取并控制 Windows 系统媒体会话，不依赖具体播放器。</summary>
public sealed class MediaSessionService : IDisposable
{
    static readonly HttpClient ArtworkClient = new()
    {
        Timeout = TimeSpan.FromSeconds(4)
    };
    static readonly TimeSpan PlaybackConfirmationWindow = TimeSpan.FromSeconds(3);
    readonly SemaphoreSlim refreshGate = new(1, 1);
    readonly DesktopMusicSessionDetector desktop = new();
    readonly MissingMediaTimelineClock missingTimelineClock = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ChronoIsle",
        "media-timeline.json"));
    readonly System.Threading.Timer timer;
    GlobalSystemMediaTransportControlsSessionManager? manager;
    GlobalSystemMediaTransportControlsSession? activeSession;
    MediaSessionSnapshot? current;
    string? systemArtworkTrackKey;
    byte[]? systemArtwork;
    string? desktopArtworkUrl;
    byte[]? desktopArtwork;
    DateTimeOffset desktopArtworkAttemptedAtUtc;
    string? pendingPlaybackTrackKey;
    bool? pendingIsPlaying;
    DateTimeOffset pendingPlaybackUntilUtc;
    bool disposed;

    public MediaSessionService() =>
        timer = new System.Threading.Timer(_ => _ = RefreshAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    public event Action<MediaSessionSnapshot?>? SnapshotChanged;
    public MediaSessionSnapshot? Current => Volatile.Read(ref current);

    public async Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        try
        {
            manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Windows media session manager unavailable: {exception.Message}");
        }
        await RefreshAsync();
        timer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    public Task TogglePlayPauseAsync() => ControlAsync(
        session => session.TryTogglePlayPauseAsync().AsTask(),
        DesktopMediaCommand.TogglePlayPause);

    public Task PreviousAsync() => ControlAsync(
        session => session.TrySkipPreviousAsync().AsTask(),
        DesktopMediaCommand.Previous);

    public Task NextAsync() => ControlAsync(
        session => session.TrySkipNextAsync().AsTask(),
        DesktopMediaCommand.Next);

    async Task ControlAsync(
        Func<GlobalSystemMediaTransportControlsSession, Task<bool>> operation,
        DesktopMediaCommand desktopCommand)
    {
        if (ShouldPreferDesktopMediaKey(Current?.SourceAppId))
        {
            desktop.Control(desktopCommand);
            PublishOptimisticPlaybackToggle(desktopCommand);
            await RefreshAsync();
            return;
        }

        var session = activeSession;
        var commandDispatched = false;
        try
        {
            var handled = session is not null && await operation(session);
            if (!handled) desktop.Control(desktopCommand);
            commandDispatched = true;
        }
        catch (Exception exception)
        {
            activeSession = null;
            System.Diagnostics.Debug.WriteLine($"Media session control failed: {exception.Message}");
            try
            {
                desktop.Control(desktopCommand);
                commandDispatched = true;
            }
            catch (Exception fallbackException)
            {
                System.Diagnostics.Debug.WriteLine($"Desktop media control failed: {fallbackException.Message}");
            }
        }
        if (commandDispatched) PublishOptimisticPlaybackToggle(desktopCommand);
        await RefreshAsync();
    }

    void PublishOptimisticPlaybackToggle(DesktopMediaCommand command)
    {
        if (command != DesktopMediaCommand.TogglePlayPause || Current is not { } snapshot) return;
        pendingPlaybackTrackKey = snapshot.TrackKey;
        pendingIsPlaying = !snapshot.IsPlaying;
        pendingPlaybackUntilUtc = DateTimeOffset.UtcNow + PlaybackConfirmationWindow;
        Publish(snapshot with { IsPlaying = pendingIsPlaying.Value }, isOptimistic: true);
    }

    static bool ShouldPreferDesktopMediaKey(string? sourceAppId) =>
        !string.IsNullOrWhiteSpace(sourceAppId) &&
        (sourceAppId.Contains("cloudmusic", StringComparison.OrdinalIgnoreCase) ||
         sourceAppId.Contains("网易云音乐", StringComparison.OrdinalIgnoreCase) ||
         sourceAppId.Contains("qqmusic", StringComparison.OrdinalIgnoreCase));

    async Task RefreshAsync()
    {
        if (disposed || !await refreshGate.WaitAsync(0)) return;
        try
        {
            var session = manager is null ? null : SelectSession(manager);
            var desktopSession = desktop.TryGetCurrent();
            var systemSessionIsMusic = session is not null &&
                                       MediaSourceClassifier.IsMusicPlayer(session.SourceAppUserModelId);
            if (ShouldPreferActiveDesktop(
                    session is not null,
                    systemSessionIsMusic,
                    desktopSession,
                    session?.SourceAppUserModelId))
            {
                activeSession = systemSessionIsMusic ? session : null;
                await PublishDesktopAsync(desktopSession!);
                return;
            }
            if (ShouldPublishDesktopFallback(session is not null, desktopSession))
            {
                activeSession = null;
                await PublishDesktopAsync(desktopSession!);
                return;
            }

            activeSession = systemSessionIsMusic ? session : null;
            if (session is null)
            {
                Publish(null);
                return;
            }

            var media = await session.TryGetMediaPropertiesAsync();
            if (ShouldPreferActiveDesktop(
                    true,
                    systemSessionIsMusic,
                    desktopSession,
                    session.SourceAppUserModelId,
                    media.Title))
            {
                activeSession = systemSessionIsMusic ? session : null;
                await PublishDesktopAsync(desktopSession!);
                return;
            }
            if (string.IsNullOrWhiteSpace(media.Title))
            {
                var fallback = desktop.TryGetCurrent();
                if (fallback is null) Publish(null); else await PublishDesktopAsync(fallback);
                return;
            }

            var timeline = session.GetTimelineProperties();
            var playback = session.GetPlaybackInfo();
            var isPlaying = playback.PlaybackStatus ==
                            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var duration = timeline.EndTime > timeline.StartTime
                ? timeline.EndTime - timeline.StartTime
                : TimeSpan.Zero;
            var position = timeline.Position < TimeSpan.Zero ? TimeSpan.Zero : timeline.Position;
            var nowUtc = DateTimeOffset.UtcNow;
            var sinceUpdate = nowUtc - timeline.LastUpdatedTime;
            if (isPlaying && sinceUpdate >= TimeSpan.Zero && sinceUpdate < TimeSpan.FromMinutes(10))
                position += sinceUpdate;
            if (duration > TimeSpan.Zero && position > duration) position = duration;

            var sourceAppId = session.SourceAppUserModelId ?? string.Empty;
            var title = media.Title.Trim();
            var artist = media.Artist?.Trim() ?? string.Empty;
            var desktopFallback = duration <= TimeSpan.Zero
                ? desktop.TryGetCurrent(sourceAppId, title, artist)
                : null;
            var useDesktopProgress = ShouldUseDesktopProgress(
                desktopFallback,
                title,
                artist,
                duration);
            var artworkTrackKey = $"{sourceAppId}\n{title}\n{artist}";
            DateTimeOffset? timelineSampledAtUtc = duration > TimeSpan.Zero
                ? nowUtc
                : useDesktopProgress
                    ? desktopFallback!.ProgressSampledAtUtc
                    : null;
            if (duration <= TimeSpan.Zero && !useDesktopProgress)
            {
                var estimated = missingTimelineClock.Update(
                    $"{title}\n{artist}",
                    position,
                    isPlaying,
                    nowUtc);
                position = estimated.Position;
                timelineSampledAtUtc = estimated.SampledAtUtc;
            }
            if (!string.Equals(systemArtworkTrackKey, artworkTrackKey, StringComparison.Ordinal))
            {
                systemArtworkTrackKey = artworkTrackKey;
                systemArtwork = await ReadArtworkAsync(media.Thumbnail);
            }
            if (systemArtwork is null && desktopFallback is null)
                desktopFallback = desktop.TryGetCurrent(sourceAppId, title, artist);
            var fallbackArtwork = desktopFallback?.Artwork;
            if (systemArtwork is null && fallbackArtwork is null)
            {
                fallbackArtwork = await ReadDesktopArtworkAsync(desktopFallback?.ArtworkUrl);
            }

            Publish(new MediaSessionSnapshot(
                sourceAppId,
                title,
                artist,
                string.IsNullOrWhiteSpace(media.AlbumTitle) ? null : media.AlbumTitle.Trim(),
                position,
                duration,
                isPlaying,
                duration > TimeSpan.Zero
                    ? Math.Clamp(position.TotalSeconds / duration.TotalSeconds, 0, 1)
                    : useDesktopProgress
                        ? desktopFallback!.ProgressRatio
                        : null,
                SelectArtwork(systemArtwork, fallbackArtwork),
                timelineSampledAtUtc,
                desktopFallback?.CurrentLyric,
                desktopFallback?.CurrentLyricSampledAtUtc));
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Media session refresh failed: {exception.Message}");
        }
        finally
        {
            refreshGate.Release();
        }
    }

    async Task PublishDesktopAsync(DesktopMusicSession fallback)
    {
        var position = TimeSpan.Zero;
        var sampledAtUtc = fallback.ProgressSampledAtUtc;
        if (fallback.ProgressRatio is null)
        {
            var estimated = missingTimelineClock.Update(
                $"{fallback.Title}\n{fallback.Artist}",
                position,
                fallback.IsPlaying,
                DateTimeOffset.UtcNow);
            position = estimated.Position;
            sampledAtUtc = estimated.SampledAtUtc;
        }
        Publish(new MediaSessionSnapshot(
            fallback.SourceAppId,
            fallback.Title,
            fallback.Artist,
            null,
            position,
            TimeSpan.Zero,
            fallback.IsPlaying,
            fallback.ProgressRatio,
            fallback.Artwork ?? await ReadDesktopArtworkAsync(fallback.ArtworkUrl),
            sampledAtUtc,
            fallback.CurrentLyric,
            fallback.CurrentLyricSampledAtUtc));
    }

    internal static byte[]? SelectArtwork(
        byte[]? systemArtwork,
        byte[]? desktopArtwork) =>
        systemArtwork ?? desktopArtwork;

    internal static bool ShouldUseDesktopProgress(
        DesktopMusicSession? fallback,
        string title,
        string artist,
        TimeSpan systemDuration)
    {
        if (systemDuration > TimeSpan.Zero || fallback?.ProgressRatio is null) return false;
        if (!string.Equals(fallback.Title.Trim(), title.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;
        return string.IsNullOrWhiteSpace(artist) ||
               string.IsNullOrWhiteSpace(fallback.Artist) ||
               string.Equals(fallback.Artist.Trim(), artist.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ShouldPublishDesktopFallback(
        bool hasSystemSession,
        DesktopMusicSession? fallback) =>
        !hasSystemSession && fallback is not null;

    internal static bool ShouldPreferActiveDesktop(
        bool hasSystemSession,
        bool systemSessionIsMusic,
        DesktopMusicSession? desktopSession,
        string? systemSourceAppId = null,
        string? systemMediaTitle = null) =>
        desktopSession is not null &&
        (desktopSession.IsPlaying &&
         (!hasSystemSession || !systemSessionIsMusic) ||
         IsRicherMineradioSnapshot(systemSourceAppId, systemMediaTitle, desktopSession));

    static bool IsRicherMineradioSnapshot(
        string? systemSourceAppId,
        string? systemMediaTitle,
        DesktopMusicSession desktopSession) =>
        (systemSourceAppId?.Contains("mineradio", StringComparison.OrdinalIgnoreCase) == true ||
         string.Equals(systemMediaTitle?.Trim(), "Mineradio", StringComparison.OrdinalIgnoreCase)) &&
        desktopSession.SourceAppId.Contains("mineradio", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(
            desktopSession.Title.Trim(),
            desktopSession.SourceAppId.Trim(),
            StringComparison.OrdinalIgnoreCase);

    async Task<byte[]?> ReadDesktopArtworkAsync(string? artworkUrl)
    {
        if (string.IsNullOrWhiteSpace(artworkUrl))
        {
            desktopArtworkUrl = null;
            desktopArtwork = null;
            return null;
        }
        var nowUtc = DateTimeOffset.UtcNow;
        if (string.Equals(desktopArtworkUrl, artworkUrl, StringComparison.Ordinal) &&
            (desktopArtwork is not null || nowUtc - desktopArtworkAttemptedAtUtc < TimeSpan.FromSeconds(30)))
            return desktopArtwork;

        desktopArtworkUrl = artworkUrl;
        desktopArtwork = null;
        desktopArtworkAttemptedAtUtc = nowUtc;
        if (!Uri.TryCreate(artworkUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
            return null;
        try
        {
            using var response = await ArtworkClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > 4 * 1024 * 1024) return null;
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(chunk);
                if (read == 0) break;
                if (buffer.Length + read > 4 * 1024 * 1024) return null;
                buffer.Write(chunk, 0, read);
            }
            desktopArtwork = buffer.Length == 0 ? null : buffer.ToArray();
            return desktopArtwork;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Desktop media artwork download failed: {exception.Message}");
            return null;
        }
    }

    static async Task<byte[]?> ReadArtworkAsync(IRandomAccessStreamReference? reference)
    {
        if (reference is null) return null;
        try
        {
            using var stream = await reference.OpenReadAsync();
            if (stream.Size == 0 || stream.Size > 4 * 1024 * 1024) return null;
            var length = checked((uint)stream.Size);
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            var loaded = await reader.LoadAsync(length);
            if (loaded != length) return null;
            var bytes = new byte[length];
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Media artwork read failed: {exception.Message}");
            return null;
        }
    }

    static GlobalSystemMediaTransportControlsSession? SelectSession(
        GlobalSystemMediaTransportControlsSessionManager sessionManager)
    {
        var preferred = sessionManager.GetCurrentSession();
        if (preferred?.GetPlaybackInfo().PlaybackStatus ==
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            return preferred;

        return sessionManager.GetSessions().FirstOrDefault(session =>
                   session.GetPlaybackInfo().PlaybackStatus ==
                   GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
               ?? preferred
               ?? sessionManager.GetSessions().FirstOrDefault();
    }

    internal static bool ShouldHoldPendingPlaybackState(
        MediaSessionSnapshot snapshot,
        string? pendingTrackKey,
        bool? pendingIsPlaying,
        DateTimeOffset pendingUntilUtc,
        DateTimeOffset nowUtc) =>
        pendingIsPlaying is bool pending &&
        nowUtc <= pendingUntilUtc &&
        string.Equals(snapshot.TrackKey, pendingTrackKey, StringComparison.Ordinal) &&
        snapshot.IsPlaying != pending;

    void Publish(MediaSessionSnapshot? snapshot, bool isOptimistic = false)
    {
        if (snapshot is null)
        {
            pendingPlaybackTrackKey = null;
            pendingIsPlaying = null;
        }
        else if (!isOptimistic && pendingIsPlaying is not null)
        {
            if (ShouldHoldPendingPlaybackState(
                    snapshot,
                    pendingPlaybackTrackKey,
                    pendingIsPlaying,
                    pendingPlaybackUntilUtc,
                    DateTimeOffset.UtcNow))
                snapshot = snapshot with { IsPlaying = pendingIsPlaying.Value };
            else
            {
                pendingPlaybackTrackKey = null;
                pendingIsPlaying = null;
            }
        }
        var previous = Interlocked.Exchange(ref current, snapshot);
        if (previous == snapshot) return;
        SnapshotChanged?.Invoke(snapshot);
    }

    public void Dispose()
    {
        disposed = true;
        timer.Dispose();
        refreshGate.Dispose();
    }
}

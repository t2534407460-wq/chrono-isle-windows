using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using NAudio.CoreAudioApi;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace ChronoIsle.App.Services.Media;

public static class DesktopMusicTitleParser
{
    static readonly string[] Separators = [" - ", " — ", " – "];

    public static bool TryParse(string? windowTitle, out string title, out string artist)
    {
        title = string.Empty;
        artist = string.Empty;
        if (string.IsNullOrWhiteSpace(windowTitle)) return false;

        foreach (var separator in Separators)
        {
            var index = windowTitle.LastIndexOf(separator, StringComparison.Ordinal);
            if (index <= 0 || index + separator.Length >= windowTitle.Length) continue;
            title = windowTitle[..index].Trim();
            artist = windowTitle[(index + separator.Length)..].Trim();
            return title.Length > 0 && artist.Length > 0;
        }

        return false;
    }
}

internal enum DesktopMediaCommand
{
    Previous,
    TogglePlayPause,
    Next
}

internal sealed record DesktopMusicSession(
    string SourceAppId,
    string Title,
    string Artist,
    bool IsPlaying,
    double? ProgressRatio,
    byte[]? Artwork,
    DateTimeOffset? ProgressSampledAtUtc,
    string? CurrentLyric = null,
    DateTimeOffset? CurrentLyricSampledAtUtc = null,
    string? ArtworkUrl = null);

internal static class DesktopMediaTimeline
{
    public static TimeSpan EstimatePosition(
        double progressRatio,
        double durationSeconds,
        DateTimeOffset? sampledAtUtc,
        DateTimeOffset nowUtc,
        bool isPlaying)
    {
        if (durationSeconds <= 0) return TimeSpan.Zero;
        var seconds = durationSeconds * Math.Clamp(progressRatio, 0, 1);
        if (isPlaying && sampledAtUtc is { } sampledAt)
        {
            var elapsed = nowUtc - sampledAt;
            if (elapsed >= TimeSpan.Zero && elapsed < TimeSpan.FromMinutes(10))
                seconds += elapsed.TotalSeconds;
        }
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 0, durationSeconds));
    }

    public static TimeSpan EstimatePosition(
        TimeSpan sampledPosition,
        double durationSeconds,
        DateTimeOffset? sampledAtUtc,
        DateTimeOffset nowUtc,
        bool isPlaying)
    {
        var seconds = sampledPosition.TotalSeconds;
        if (isPlaying && sampledAtUtc is { } sampledAt)
        {
            var elapsed = nowUtc - sampledAt;
            if (elapsed >= TimeSpan.Zero && elapsed < TimeSpan.FromMinutes(10))
                seconds += elapsed.TotalSeconds;
        }
        if (durationSeconds > 0)
            seconds = seconds > durationSeconds + 2
                ? seconds % durationSeconds
                : Math.Min(seconds, durationSeconds);
        return TimeSpan.FromSeconds(Math.Max(0, seconds));
    }
}

internal sealed record PersistedMediaTimelineState(
    string TrackKey,
    double PositionSeconds,
    DateTimeOffset SampledAtUtc,
    bool WasPlaying);

internal sealed class MissingMediaTimelineClock
{
    static readonly TimeSpan RestoreWindow = TimeSpan.FromMinutes(2);
    readonly string? statePath;
    PersistedMediaTimelineState? restoredState;
    string? trackKey;
    TimeSpan position;
    DateTimeOffset sampledAtUtc;
    bool wasPlaying;

    public MissingMediaTimelineClock(string? statePath = null)
    {
        this.statePath = statePath;
        if (statePath is null || !File.Exists(statePath)) return;
        try
        {
            restoredState = JsonSerializer.Deserialize<PersistedMediaTimelineState>(
                File.ReadAllText(statePath));
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Unable to restore desktop media timeline: {exception.Message}");
        }
    }

    public (TimeSpan Position, DateTimeOffset SampledAtUtc) Update(
        string currentTrackKey,
        TimeSpan reportedPosition,
        bool isPlaying,
        DateTimeOffset nowUtc)
    {
        if (trackKey is null && restoredState is { } restored)
        {
            var age = nowUtc - restored.SampledAtUtc;
            if (string.Equals(restored.TrackKey, currentTrackKey, StringComparison.Ordinal) &&
                age >= TimeSpan.Zero &&
                age <= RestoreWindow)
            {
                trackKey = restored.TrackKey;
                position = TimeSpan.FromSeconds(Math.Max(0, restored.PositionSeconds));
                sampledAtUtc = restored.SampledAtUtc;
                wasPlaying = restored.WasPlaying;
            }
            restoredState = null;
        }

        if (!string.Equals(trackKey, currentTrackKey, StringComparison.Ordinal))
        {
            trackKey = currentTrackKey;
            position = reportedPosition > TimeSpan.Zero ? reportedPosition : TimeSpan.Zero;
        }
        else
        {
            var elapsed = nowUtc - sampledAtUtc;
            if (wasPlaying && elapsed >= TimeSpan.Zero && elapsed < TimeSpan.FromMinutes(10))
                position += elapsed;
            if (reportedPosition > TimeSpan.Zero &&
                Math.Abs((reportedPosition - position).TotalSeconds) > 2)
                position = reportedPosition;
        }
        sampledAtUtc = nowUtc;
        wasPlaying = isPlaying;
        Persist();
        return (position, sampledAtUtc);
    }

    void Persist()
    {
        if (statePath is null || trackKey is null) return;
        try
        {
            var directory = Path.GetDirectoryName(statePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(
                statePath,
                JsonSerializer.Serialize(new PersistedMediaTimelineState(
                    trackKey,
                    position.TotalSeconds,
                    sampledAtUtc,
                    wasPlaying)));
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Unable to persist desktop media timeline: {exception.Message}");
        }
    }
}
internal sealed record MineradioPlaybackSnapshot(
    string Title,
    string Artist,
    bool IsPlaying,
    TimeSpan Position,
    TimeSpan Duration,
    DateTimeOffset SampledAtUtc,
    string? ArtworkUrl);

internal static class MineradioPlaybackStore
{
    static readonly byte[] PlaybackKey = Encoding.UTF8.GetBytes("mineradio-last-playback-v1");

    public static MineradioPlaybackSnapshot? TryReadLatest()
    {
        var levelDbDirectory = GetLevelDbDirectory();
        if (!Directory.Exists(levelDbDirectory)) return null;
        MineradioPlaybackSnapshot? latest = null;
        foreach (var file in Directory.EnumerateFiles(levelDbDirectory)
                     .Where(IsLevelDbRecordFile)
                     .OrderByDescending(File.GetLastWriteTimeUtc)
                     .Take(8))
        {
            try
            {
                using var stream = new FileStream(
                    file,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length <= 0 || stream.Length > 8 * 1024 * 1024) continue;
                var bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
                if (!TryParseLatest(bytes, out var candidate)) continue;
                if (latest is null || candidate.SampledAtUtc > latest.SampledAtUtc) latest = candidate;
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Mineradio playback snapshot read failed: {exception.Message}");
            }
        }
        return latest;
    }

    internal static bool IsLevelDbRecordFile(string filePath) =>
        string.Equals(Path.GetExtension(filePath), ".log", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Path.GetExtension(filePath), ".ldb", StringComparison.OrdinalIgnoreCase);

    internal static bool TryParseLatest(ReadOnlySpan<byte> data, out MineradioPlaybackSnapshot snapshot)
    {
        snapshot = default!;
        var found = false;
        foreach (var record in ReadLevelDbRecords(data))
            if (TryParseRaw(record, out var candidate) &&
                (!found || candidate.SampledAtUtc > snapshot.SampledAtUtc))
            {
                snapshot = candidate;
                found = true;
            }
        return found || TryParseRaw(data, out snapshot);
    }

    static bool TryParseRaw(ReadOnlySpan<byte> data, out MineradioPlaybackSnapshot snapshot)
    {
        snapshot = default!;
        var offset = 0;
        var found = false;
        while (offset + PlaybackKey.Length < data.Length)
        {
            var relative = data[offset..].IndexOf(PlaybackKey);
            if (relative < 0) break;
            var keyEnd = offset + relative + PlaybackKey.Length;
            var jsonStart = FindUtf16JsonStart(data, keyEnd);
            if (jsonStart >= 0 && TryParseJson(data[jsonStart..], out var candidate) &&
                (!found || candidate.SampledAtUtc > snapshot.SampledAtUtc))
            {
                snapshot = candidate;
                found = true;
            }
            offset = keyEnd;
        }
        return found;
    }

    static IReadOnlyList<byte[]> ReadLevelDbRecords(ReadOnlySpan<byte> data)
    {
        const int blockSize = 32768;
        const int headerSize = 7;
        const byte full = 1;
        const byte first = 2;
        const byte middle = 3;
        const byte last = 4;
        var records = new List<byte[]>();
        MemoryStream? fragmented = null;
        var offset = 0;
        while (offset + headerSize <= data.Length)
        {
            var remainingInBlock = blockSize - offset % blockSize;
            if (remainingInBlock < headerSize)
            {
                offset += remainingInBlock;
                continue;
            }

            var length = data[offset + 4] | data[offset + 5] << 8;
            var type = data[offset + 6];
            if (length == 0 && type == 0)
            {
                offset += remainingInBlock;
                fragmented?.Dispose();
                fragmented = null;
                continue;
            }
            if (length > remainingInBlock - headerSize || offset + headerSize + length > data.Length)
                break;

            var payload = data.Slice(offset + headerSize, length);
            offset += headerSize + length;
            switch (type)
            {
                case full:
                    fragmented?.Dispose();
                    fragmented = null;
                    records.Add(payload.ToArray());
                    break;
                case first:
                    fragmented?.Dispose();
                    fragmented = new MemoryStream(length * 2);
                    fragmented.Write(payload);
                    break;
                case middle when fragmented is not null:
                    fragmented.Write(payload);
                    break;
                case last when fragmented is not null:
                    fragmented.Write(payload);
                    records.Add(fragmented.ToArray());
                    fragmented.Dispose();
                    fragmented = null;
                    break;
                default:
                    fragmented?.Dispose();
                    fragmented = null;
                    break;
            }
        }
        fragmented?.Dispose();
        return records;
    }

    static int FindUtf16JsonStart(ReadOnlySpan<byte> data, int start)
    {
        var end = Math.Min(data.Length - 1, start + 64);
        for (var index = start; index < end; index++)
            if (data[index] == (byte)'{' && data[index + 1] == 0) return index;
        return -1;
    }

    static bool TryParseJson(ReadOnlySpan<byte> data, out MineradioPlaybackSnapshot snapshot)
    {
        snapshot = default!;
        var depth = 0;
        var inString = false;
        var escaped = false;
        var end = -1;
        for (var index = 0; index + 1 < data.Length; index += 2)
        {
            var character = (char)(data[index] | data[index + 1] << 8);
            if (inString)
            {
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == '"') inString = false;
                continue;
            }
            if (character == '"') inString = true;
            else if (character == '{') depth++;
            else if (character == '}' && --depth == 0)
            {
                end = index + 2;
                break;
            }
        }
        if (end <= 0) return false;

        try
        {
            using var document = JsonDocument.Parse(Encoding.Unicode.GetString(data[..end]));
            var root = document.RootElement;
            if (!root.TryGetProperty("current", out var current)) return false;
            var title = ReadText(current, "name", "title");
            if (string.IsNullOrWhiteSpace(title)) return false;
            var artist = ReadText(current, "artist");
            var artworkUrl = ReadText(current, "cover");
            var savedAt = root.GetProperty("savedAt").GetInt64();
            snapshot = new MineradioPlaybackSnapshot(
                title.Trim(),
                artist.Trim(),
                root.TryGetProperty("playing", out var playing) && playing.GetBoolean(),
                TimeSpan.FromSeconds(ReadNumber(root, "currentTime")),
                TimeSpan.FromSeconds(ReadNumber(root, "duration")),
                DateTimeOffset.FromUnixTimeMilliseconds(savedAt),
                string.IsNullOrWhiteSpace(artworkUrl) ? null : artworkUrl.Trim());
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    static string ReadText(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? string.Empty;
        return string.Empty;
    }

    static double ReadNumber(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetDouble(out var number)
            ? Math.Max(0, number)
            : 0;

    static string GetLevelDbDirectory()
    {
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mineradio");
        var root = Directory.Exists(@"D:\") ? @"D:\MineradioCache" : Path.Combine(appData, "cache");
        try
        {
            var settingsPath = Path.Combine(appData, "cache-settings.json");
            if (File.Exists(settingsPath))
            {
                using var settings = JsonDocument.Parse(File.ReadAllText(settingsPath));
                if (settings.RootElement.TryGetProperty("rootPath", out var rootPath) && !string.IsNullOrWhiteSpace(rootPath.GetString()))
                    root = Path.GetFullPath(rootPath.GetString()!);
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Mineradio cache settings read failed: {exception.Message}");
        }
        return Path.Combine(root, "chromium", "Mineradio", "Local Storage", "leveldb");
    }
}

/// <summary>
/// 为不发布 Windows 系统媒体会话的桌面版网易云音乐、QQ 音乐提供本地兼容检测。
/// 只读取公开窗口信息、渲染画面和 Mineradio 自己的当前播放快照，不读取 Cookie、账号数据库或登录令牌。
/// </summary>
internal sealed class DesktopMusicSessionDetector
{
    const uint KeyUp = 0x0002;
    const ushort MediaNextTrack = 0xB0;
    const ushort MediaPreviousTrack = 0xB1;
    const ushort MediaPlayPause = 0xB3;
    const ushort ControlKey = 0x11;
    const ushort AltKey = 0x12;
    const ushort SpaceKey = 0x20;
    const ushort LeftKey = 0x25;
    const ushort RightKey = 0x27;

    static readonly Lazy<OcrEngine?> NetEaseOcrEngine = new(OcrEngine.TryCreateFromUserProfileLanguages);

    static readonly PlayerProfile[] Profiles =
    [
        new("cloudmusic", "网易云音乐", "OrpheusBrowserHost", true),
        new("QQMusic", "QQ 音乐", null, false),
        new("Mineradio", "Mineradio", null, false)
    ];

    string? cachedTrackKey;
    string? cachedPlayerProcessName;
    byte[]? cachedArtwork;
    string? cachedCurrentLyric;
    DateTimeOffset? cachedCurrentLyricSampledAtUtc;
    bool cachedIsPlaying = true;
    double? cachedProgressRatio;
    DateTimeOffset? cachedProgressSampledAtUtc;
    DateTimeOffset lastCaptureAt;

    string? cachedSourceAppId;
    public DesktopMusicSession? TryGetCurrent(
        string? sourceAppId = null,
        string? knownTitle = null,
        string? knownArtist = null)
    {
        var activeProfile = TryGetActiveAudioProfile(sourceAppId);
        if (activeProfile?.ProcessName.Equals("Mineradio", StringComparison.OrdinalIgnoreCase) == true)
        {
            var mineradio = TryGetMineradioCurrent();
            if (mineradio is not null)
            {
                cachedIsPlaying = true;
                return mineradio with { IsPlaying = true };
            }
        }
        var windows = FindPlayerWindows(sourceAppId, knownTitle, knownArtist, activeProfile);
        var candidate = windows
            .OrderByDescending(window => WindowScore(window, activeProfile))
            .FirstOrDefault();
        if (candidate is null)
        {
            cachedPlayerProcessName = null;
            if (activeProfile is not null) return CreateActiveProfileFallback(activeProfile);
            if (string.IsNullOrWhiteSpace(sourceAppId) ||
                sourceAppId.Contains("mineradio", StringComparison.OrdinalIgnoreCase))
                return TryGetMineradioCurrent();
            return null;
        }
        cachedPlayerProcessName = candidate.Profile.ProcessName;

        if (ShouldUseMineradioSnapshot(
                candidate.Profile.ProcessName,
                candidate.Title,
                candidate.Artist) &&
            TryGetMineradioCurrent() is { } snapshot)
        {
            if (!ReferenceEquals(candidate.Profile, activeProfile)) return snapshot;
            cachedIsPlaying = true;
            return snapshot with { IsPlaying = true };
        }

        var trackKey = $"{candidate.Profile.DisplayName}\n{candidate.Title}\n{candidate.Artist}";
        var trackChanged = !string.Equals(trackKey, cachedTrackKey, StringComparison.Ordinal);
        if (trackChanged)
        {
            cachedTrackKey = trackKey;
            cachedArtwork = null;
            cachedCurrentLyric = null;
            cachedCurrentLyricSampledAtUtc = null;
            cachedProgressRatio = null;
            cachedProgressSampledAtUtc = null;
            cachedIsPlaying = ReferenceEquals(candidate.Profile, activeProfile);
            lastCaptureAt = DateTimeOffset.MinValue;
        }

        bool? capturedPlaybackState = null;
        if (candidate.Profile.SupportsLocalCapture &&
            (trackChanged || DateTimeOffset.UtcNow - lastCaptureAt >= TimeSpan.FromMilliseconds(750)) &&
            TryCaptureNetEase(candidate.Window, out var capture))
        {
            var capturedAt = DateTimeOffset.UtcNow;
            lastCaptureAt = capturedAt;
            if (capture.IsPlaying is bool isPlaying)
            {
                capturedPlaybackState = isPlaying;
                cachedIsPlaying = isPlaying;
            }
            if (capture.ProgressRatio is double progressRatio)
                cachedProgressRatio = progressRatio;
            if (capture.ProgressRatio is not null)
                cachedProgressSampledAtUtc = capturedAt;
            if (!string.IsNullOrWhiteSpace(capture.CurrentLyric))
            {
                cachedCurrentLyric = capture.CurrentLyric;
                cachedCurrentLyricSampledAtUtc = capturedAt;
            }
            if (trackChanged || cachedArtwork is null) cachedArtwork = capture.Artwork;
        }
        cachedIsPlaying = ResolvePlaybackState(
            cachedIsPlaying,
            capturedPlaybackState,
            activeProfile is null ? null : ReferenceEquals(candidate.Profile, activeProfile));

        cachedSourceAppId = candidate.Profile.DisplayName;
        return new DesktopMusicSession(
            candidate.Profile.DisplayName,
            candidate.Title,
            candidate.Artist,
            cachedIsPlaying,
            cachedProgressRatio,
            cachedArtwork,
            cachedProgressSampledAtUtc,
            cachedCurrentLyric,
            cachedCurrentLyricSampledAtUtc);
    }

    public DesktopMusicSession? TryGetMineradioCurrent()
    {
        var processes = Process.GetProcessesByName("Mineradio");
        DateTimeOffset processStartedAtUtc;
        try
        {
            if (processes.Length == 0) return null;
            processStartedAtUtc = DateTimeOffset.MaxValue;
            foreach (var process in processes)
                try
                {
                    var startedAt = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
                    if (startedAt < processStartedAtUtc) processStartedAtUtc = startedAt;
                }
                catch { }
            if (processStartedAtUtc == DateTimeOffset.MaxValue) processStartedAtUtc = DateTimeOffset.UtcNow;
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
        var snapshot = MineradioPlaybackStore.TryReadLatest();
        if (snapshot is null) return null;
        if (!IsMineradioSnapshotCurrent(snapshot.SampledAtUtc, processStartedAtUtc, DateTimeOffset.UtcNow))
            return null;

        var trackKey = $"Mineradio\n{snapshot.Title}\n{snapshot.Artist}";
        if (!string.Equals(trackKey, cachedTrackKey, StringComparison.Ordinal))
        {
            cachedTrackKey = trackKey;
            cachedArtwork = null;
            cachedCurrentLyric = null;
        }
        cachedSourceAppId = "Mineradio";
        cachedIsPlaying = snapshot.IsPlaying;
        cachedProgressRatio = snapshot.Duration > TimeSpan.Zero
            ? Math.Clamp(snapshot.Position.TotalSeconds / snapshot.Duration.TotalSeconds, 0, 1)
            : null;
        cachedProgressSampledAtUtc = snapshot.SampledAtUtc;
        return new DesktopMusicSession(
            "Mineradio",
            snapshot.Title,
            snapshot.Artist,
            snapshot.IsPlaying,
            cachedProgressRatio,
            null,
            snapshot.SampledAtUtc,
            ArtworkUrl: snapshot.ArtworkUrl);
    }

    internal static bool IsMineradioSnapshotCurrent(
        DateTimeOffset sampledAtUtc,
        DateTimeOffset processStartedAtUtc,
        DateTimeOffset nowUtc) =>
        nowUtc - sampledAtUtc >= TimeSpan.FromMinutes(-1) &&
        sampledAtUtc >= processStartedAtUtc - TimeSpan.FromSeconds(5);

    public void Control(DesktopMediaCommand command)
    {
        if (string.Equals(cachedSourceAppId, "Mineradio", StringComparison.OrdinalIgnoreCase))
        {
            SendMineradioShortcut(command);
            if (command == DesktopMediaCommand.TogglePlayPause) cachedIsPlaying = !cachedIsPlaying;
            lastCaptureAt = DateTimeOffset.MinValue;
            return;
        }
        SendMediaKey(MediaKey(command));
        if (command == DesktopMediaCommand.TogglePlayPause) cachedIsPlaying = !cachedIsPlaying;
        lastCaptureAt = DateTimeOffset.MinValue;
    }

    static ushort MediaKey(DesktopMediaCommand command) => command switch
    {
        DesktopMediaCommand.Previous => MediaPreviousTrack,
        DesktopMediaCommand.Next => MediaNextTrack,
        _ => MediaPlayPause
    };

    static IReadOnlyList<PlayerWindow> FindPlayerWindows(
        string? sourceAppId = null,
        string? knownTitle = null,
        string? knownArtist = null,
        PlayerProfile? activeProfile = null)
    {
        var processProfiles = new Dictionary<uint, PlayerProfile>();
        foreach (var profile in Profiles)
        {
            if (!string.IsNullOrWhiteSpace(sourceAppId) &&
                !ProfileMatchesSource(profile, sourceAppId))
                continue;
            foreach (var process in Process.GetProcessesByName(profile.ProcessName))
            {
                try
                {
                    processProfiles[(uint)process.Id] = profile;
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        if (processProfiles.Count == 0) return [];

        var windows = new List<PlayerWindow>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var processId);
            if (!processProfiles.TryGetValue(processId, out var profile)) return true;

            var titleBuffer = new StringBuilder(512);
            GetWindowText(window, titleBuffer, titleBuffer.Capacity);
            if (!DesktopMusicTitleParser.TryParse(titleBuffer.ToString(), out var title, out var artist))
            {
                if (string.IsNullOrWhiteSpace(knownTitle) &&
                    !ReferenceEquals(profile, activeProfile))
                    return true;
                title = string.IsNullOrWhiteSpace(knownTitle)
                    ? profile.DisplayName
                    : knownTitle.Trim();
                artist = knownArtist?.Trim() ?? string.Empty;
            }

            var classBuffer = new StringBuilder(256);
            GetClassName(window, classBuffer, classBuffer.Capacity);
            windows.Add(new PlayerWindow(
                window,
                profile,
                title,
                artist,
                classBuffer.ToString(),
                IsWindowVisible(window)));
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    static bool ProfileMatchesSource(PlayerProfile profile, string sourceAppId) =>
        sourceAppId.Contains(profile.ProcessName, StringComparison.OrdinalIgnoreCase) ||
        sourceAppId.Contains(profile.DisplayName, StringComparison.OrdinalIgnoreCase);

    static int WindowScore(PlayerWindow window, PlayerProfile? activeProfile)
    {
        var score = ReferenceEquals(window.Profile, activeProfile) ? 1000 : 0;
        if (window.IsVisible) score += 100;
        if (!string.IsNullOrWhiteSpace(window.Profile.PreferredWindowClass) &&
            string.Equals(
                window.WindowClass,
                window.Profile.PreferredWindowClass,
                StringComparison.Ordinal))
            score += 50;
        return score;
    }

    DesktopMusicSession CreateActiveProfileFallback(PlayerProfile profile)
    {
        cachedTrackKey = $"{profile.DisplayName}\n{profile.DisplayName}\n";
        cachedSourceAppId = profile.DisplayName;
        cachedIsPlaying = true;
        cachedProgressRatio = null;
        cachedProgressSampledAtUtc = DateTimeOffset.UtcNow;
        cachedArtwork = null;
        return new DesktopMusicSession(
            profile.DisplayName,
            profile.DisplayName,
            string.Empty,
            true,
            null,
            null,
            cachedProgressSampledAtUtc);
    }

    static PlayerProfile? TryGetActiveAudioProfile(string? sourceAppId)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = device.AudioSessionManager.Sessions;
            for (var index = 0; index < sessions.Count; index++)
            {
                try
                {
                    using var session = sessions[index];
                    if (session.State != NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive)
                        continue;
                    var processId = session.GetProcessID;
                    if (processId == 0) continue;
                    using var process = Process.GetProcessById(checked((int)processId));
                    var profile = ProfileForProcessName(process.ProcessName);
                    if (profile is null ||
                        !string.IsNullOrWhiteSpace(sourceAppId) &&
                        !ProfileMatchesSource(profile, sourceAppId))
                        continue;
                    return profile;
                }
                catch (Exception exception)
                {
                    Debug.WriteLine($"Skipping inaccessible audio session: {exception.Message}");
                }
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Active music audio session detection failed: {exception.Message}");
        }
        return null;
    }

    static PlayerProfile? ProfileForProcessName(string? processName) =>
        Profiles.FirstOrDefault(profile =>
            string.Equals(profile.ProcessName, processName, StringComparison.OrdinalIgnoreCase));

    internal static bool ShouldUseMineradioSnapshot(
        string? processName,
        string? title,
        string? artist) =>
        string.Equals(processName, "Mineradio", StringComparison.OrdinalIgnoreCase) &&
        (string.IsNullOrWhiteSpace(title) ||
         string.Equals(title.Trim(), "Mineradio", StringComparison.OrdinalIgnoreCase) ||
         string.IsNullOrWhiteSpace(artist));

    internal static bool ResolvePlaybackState(
        bool currentState,
        bool? capturedState,
        bool? activeProfileMatches) =>
        capturedState ?? activeProfileMatches ?? currentState;

    internal static string? PlayerDisplayNameForProcess(string? processName) =>
        ProfileForProcessName(processName)?.DisplayName;

    static bool TryCaptureNetEase(IntPtr window, out PlayerCapture capture)
    {
        capture = default;
        if (!GetWindowRect(window, out var rectangle)) return false;
        var width = rectangle.Right - rectangle.Left;
        var height = rectangle.Bottom - rectangle.Top;
        if (width < 300 || height < 150 || width > 10000 || height > 10000) return false;

        try
        {
            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(bitmap);
            var deviceContext = graphics.GetHdc();
            bool rendered;
            try
            {
                rendered = PrintWindow(window, deviceContext, 2);
            }
            finally
            {
                graphics.ReleaseHdc(deviceContext);
            }
            if (!rendered) return false;

            var artwork = CropNetEaseArtwork(bitmap);
            capture = new PlayerCapture(
                DetectNetEasePlayback(bitmap),
                DetectNetEaseProgress(bitmap),
                artwork,
                TryReadNetEaseDesktopLyrics(window));
            return true;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Desktop music capture failed: {exception.Message}");
            return false;
        }
    }

    static string? TryReadNetEaseDesktopLyrics(IntPtr playerWindow)
    {
        var lyricsWindow = FindProcessWindow(playerWindow, "DesktopLyrics");
        if (lyricsWindow == IntPtr.Zero || !GetWindowRect(lyricsWindow, out var rectangle)) return null;
        var width = rectangle.Right - rectangle.Left;
        var height = rectangle.Bottom - rectangle.Top;
        if (width < 100 || height < 40 || width > 4000 || height > 1000) return null;

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        var deviceContext = graphics.GetHdc();
        bool rendered;
        try
        {
            rendered = PrintWindow(lyricsWindow, deviceContext, 2);
        }
        finally
        {
            graphics.ReleaseHdc(deviceContext);
        }
        return rendered ? RecognizeNetEaseLyrics(bitmap) : null;
    }

    static IntPtr FindProcessWindow(IntPtr sourceWindow, string windowClass)
    {
        GetWindowThreadProcessId(sourceWindow, out var sourceProcessId);
        var result = IntPtr.Zero;
        EnumWindows((candidate, _) =>
        {
            GetWindowThreadProcessId(candidate, out var candidateProcessId);
            if (candidateProcessId != sourceProcessId) return true;
            var className = new StringBuilder(256);
            GetClassName(candidate, className, className.Capacity);
            if (!string.Equals(className.ToString(), windowClass, StringComparison.Ordinal)) return true;
            result = candidate;
            return false;
        }, IntPtr.Zero);
        return result;
    }

    static string? RecognizeNetEaseLyrics(Bitmap bitmap)
    {
        var engine = NetEaseOcrEngine.Value;
        if (engine is null) return null;
        using var encoded = new MemoryStream();
        bitmap.Save(encoded, ImageFormat.Png);
        using var randomAccess = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(randomAccess))
        {
            writer.WriteBytes(encoded.ToArray());
            writer.StoreAsync().AsTask().GetAwaiter().GetResult();
            writer.DetachStream();
        }
        randomAccess.Seek(0);
        var decoder = BitmapDecoder.CreateAsync(randomAccess).AsTask().GetAwaiter().GetResult();
        using var softwareBitmap = decoder.GetSoftwareBitmapAsync().AsTask().GetAwaiter().GetResult();
        var result = engine.RecognizeAsync(softwareBitmap).AsTask().GetAwaiter().GetResult();
        var lines = result.Lines
            .Select(line => line.Text.Trim())
            .Where(line => line.Length > 0)
            .ToArray();
        return lines.Length == 0 ? null : string.Join('\n', lines);
    }
    static byte[]? CropNetEaseArtwork(Bitmap bitmap)
    {
        const int size = 64;
        const int left = 29;
        var top = bitmap.Height - 73;
        if (left < 0 || top < 0 || left + size > bitmap.Width || top + size > bitmap.Height)
            return null;

        using var artwork = bitmap.Clone(
            new Rectangle(left, top, size, size),
            PixelFormat.Format32bppArgb);
        using var stream = new MemoryStream();
        artwork.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    internal static bool? DetectNetEasePlayback(Bitmap bitmap)
    {
        var centerX = bitmap.Width / 2;
        var whitePixels = 0;
        var rowsWithTwoSegments = 0;
        var top = Math.Max(0, bitmap.Height - 51);
        var bottom = Math.Min(bitmap.Height - 1, bitmap.Height - 30);
        for (var y = top; y <= bottom; y++)
        {
            var segments = 0;
            var inSegment = false;
            for (var x = Math.Max(0, centerX - 12); x <= Math.Min(bitmap.Width - 1, centerX + 12); x++)
            {
                var color = bitmap.GetPixel(x, y);
                var isWhite = color.R > 225 && color.G > 225 && color.B > 225;
                if (isWhite)
                {
                    whitePixels++;
                    if (!inSegment) segments++;
                }
                inSegment = isWhite;
            }
            if (segments >= 2) rowsWithTwoSegments++;
        }
        if (whitePixels < 12) return null;
        return rowsWithTwoSegments >= 5;
    }

    internal static double? DetectNetEaseProgress(Bitmap bitmap)
    {
        var top = Math.Max(0, bitmap.Height - 115);
        var bottom = Math.Min(bitmap.Height - 1, bitmap.Height - 65);
        var bestStart = -1;
        var bestEnd = -1;
        var bestLength = 0;
        var maximumStartX = Math.Min(bitmap.Width - 1, Math.Max(32, bitmap.Width / 20));

        for (var y = top; y <= bottom; y++)
        {
            var start = -1;
            for (var x = 0; x <= maximumStartX; x++)
            {
                if (!IsNetEaseProgressColor(bitmap.GetPixel(x, y))) continue;
                start = x;
                break;
            }
            if (start < 0) continue;

            var end = start;
            var gaps = 0;
            for (var x = start + 1; x < bitmap.Width; x++)
            {
                if (IsNetEaseProgressColor(bitmap.GetPixel(x, y)))
                {
                    end = x;
                    gaps = 0;
                }
                else if (++gaps > 3)
                {
                    break;
                }
            }

            var length = end - start + 1;
            if (length < 3 || length <= bestLength) continue;
            bestStart = start;
            bestEnd = end;
            bestLength = length;
        }

        if (bestStart >= 0)
        {
            var trackWidth = Math.Max(1, bitmap.Width - bestStart * 2);
            return Math.Clamp((bestEnd - bestStart + 1d) / trackWidth, 0, 1);
        }
        return null;
    }

    static bool IsNetEaseProgressColor(System.Drawing.Color color) =>
        color.R > 190 && color.R - color.G > 45 && color.R - color.B > 25;

    static void SendMediaKey(ushort virtualKey)
    {
        keybd_event((byte)virtualKey, 0, 0, UIntPtr.Zero);
        keybd_event((byte)virtualKey, 0, KeyUp, UIntPtr.Zero);
    }

    static void SendMineradioShortcut(DesktopMediaCommand command)
    {
        var key = command switch
        {
            DesktopMediaCommand.Previous => LeftKey,
            DesktopMediaCommand.Next => RightKey,
            _ => SpaceKey
        };
        SendShortcut(key);
    }

    static void SendShortcut(ushort virtualKey)
    {
        keybd_event((byte)ControlKey, 0, 0, UIntPtr.Zero);
        keybd_event((byte)AltKey, 0, 0, UIntPtr.Zero);
        keybd_event((byte)virtualKey, 0, 0, UIntPtr.Zero);
        keybd_event((byte)virtualKey, 0, KeyUp, UIntPtr.Zero);
        keybd_event((byte)AltKey, 0, KeyUp, UIntPtr.Zero);
        keybd_event((byte)ControlKey, 0, KeyUp, UIntPtr.Zero);
    }

    delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(IntPtr window, StringBuilder text, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr window, StringBuilder className, int maximumCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetWindowRect(IntPtr window, out NativeRectangle rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool PrintWindow(IntPtr window, IntPtr deviceContext, uint flags);

    [DllImport("user32.dll")]
    static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    [StructLayout(LayoutKind.Sequential)]
    struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    sealed record PlayerProfile(
        string ProcessName,
        string DisplayName,
        string? PreferredWindowClass,
        bool SupportsLocalCapture);

    sealed record PlayerWindow(
        IntPtr Window,
        PlayerProfile Profile,
        string Title,
        string Artist,
        string WindowClass,
        bool IsVisible);

    readonly record struct PlayerCapture(bool? IsPlaying, double? ProgressRatio, byte[]? Artwork, string? CurrentLyric);
}

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
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
    DateTimeOffset? CurrentLyricSampledAtUtc = null);

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
/// <summary>
/// 为不发布 Windows 系统媒体会话的桌面版网易云音乐、QQ 音乐提供本地兼容检测。
/// 只读取播放器进程的公开窗口标题和渲染画面，不读取 Cookie、账号数据库或登录令牌。
/// </summary>
internal sealed class DesktopMusicSessionDetector
{
    const uint KeyUp = 0x0002;
    const ushort MediaNextTrack = 0xB0;
    const ushort MediaPreviousTrack = 0xB1;
    const ushort MediaPlayPause = 0xB3;
    const ushort ControlKey = 0x11;
    const ushort AltKey = 0x12;
    const ushort LeftKey = 0x25;
    const ushort RightKey = 0x27;

    static readonly Lazy<OcrEngine?> NetEaseOcrEngine = new(OcrEngine.TryCreateFromUserProfileLanguages);

    static readonly PlayerProfile[] Profiles =
    [
        new("cloudmusic", "网易云音乐", "OrpheusBrowserHost", true),
        new("QQMusic", "QQ 音乐", null, false)
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

    public DesktopMusicSession? TryGetCurrent(
        string? sourceAppId = null,
        string? knownTitle = null,
        string? knownArtist = null)
    {
        var windows = FindPlayerWindows(sourceAppId, knownTitle, knownArtist);
        var candidate = windows
            .OrderByDescending(window => WindowScore(window))
            .FirstOrDefault();
        if (candidate is null)
        {
            cachedPlayerProcessName = null;
            return null;
        }
        cachedPlayerProcessName = candidate.Profile.ProcessName;

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
            cachedIsPlaying = true;
            lastCaptureAt = DateTimeOffset.MinValue;
        }

        if (candidate.Profile.SupportsLocalCapture &&
            candidate.IsVisible &&
            (trackChanged || DateTimeOffset.UtcNow - lastCaptureAt >= TimeSpan.FromMilliseconds(750)) &&
            TryCaptureNetEase(candidate.Window, out var capture))
        {
            var capturedAt = DateTimeOffset.UtcNow;
            lastCaptureAt = capturedAt;
            if (capture.IsPlaying is bool isPlaying) cachedIsPlaying = isPlaying;
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

    public void Control(DesktopMediaCommand command, string? sourceAppId = null)
    {
        if (ShouldUseNetEaseShortcut(sourceAppId, command) ||
            ShouldUseNetEaseShortcut(cachedPlayerProcessName, command))
            SendNetEaseShortcut(command);
        else
            SendMediaKey(command switch
            {
                DesktopMediaCommand.Previous => MediaPreviousTrack,
                DesktopMediaCommand.Next => MediaNextTrack,
                _ => MediaPlayPause
            });
        if (command == DesktopMediaCommand.TogglePlayPause) cachedIsPlaying = !cachedIsPlaying;
        lastCaptureAt = DateTimeOffset.MinValue;
    }

    static bool ShouldUseNetEaseShortcut(string? sourceAppId, DesktopMediaCommand command) =>
        command != DesktopMediaCommand.TogglePlayPause &&
        !string.IsNullOrWhiteSpace(sourceAppId) &&
        (sourceAppId.Contains("cloudmusic", StringComparison.OrdinalIgnoreCase) ||
         sourceAppId.Contains(Profiles[0].DisplayName, StringComparison.OrdinalIgnoreCase));

    static ushort NetEaseShortcutKey(DesktopMediaCommand command) => command switch
    {
        DesktopMediaCommand.Previous => LeftKey,
        DesktopMediaCommand.Next => RightKey,
        _ => throw new ArgumentOutOfRangeException(nameof(command))
    };

    static IReadOnlyList<PlayerWindow> FindPlayerWindows(
        string? sourceAppId = null,
        string? knownTitle = null,
        string? knownArtist = null)
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
                if (string.IsNullOrWhiteSpace(knownTitle)) return true;
                title = knownTitle.Trim();
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

    static int WindowScore(PlayerWindow window)
    {
        var score = window.IsVisible ? 100 : 0;
        if (!string.IsNullOrWhiteSpace(window.Profile.PreferredWindowClass) &&
            string.Equals(
                window.WindowClass,
                window.Profile.PreferredWindowClass,
                StringComparison.Ordinal))
            score += 50;
        return score;
    }

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

    static void SendNetEaseShortcut(DesktopMediaCommand command)
    {
        var key = (byte)NetEaseShortcutKey(command);
        keybd_event((byte)ControlKey, 0, 0, UIntPtr.Zero);
        keybd_event((byte)AltKey, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, KeyUp, UIntPtr.Zero);
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

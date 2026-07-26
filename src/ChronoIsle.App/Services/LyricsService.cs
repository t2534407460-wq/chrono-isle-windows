using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ChronoIsle.App.Services.Media;

public sealed record LyricLine(TimeSpan Timestamp, string Text);

internal static class LyricsTextNormalizer
{
    const uint SimplifiedChinese = 0x02000000;

    public static string ToSimplified(string text)
    {
        if (text.Length == 0) return text;
        var length = LCMapStringEx(
            "zh-CN", SimplifiedChinese, text, text.Length, null, 0,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (length <= 0) return text;
        var result = new StringBuilder(length);
        var written = LCMapStringEx(
            "zh-CN", SimplifiedChinese, text, text.Length, result, result.Capacity,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return written > 0 ? result.ToString(0, written) : text;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int LCMapStringEx(
        string localeName,
        uint mapFlags,
        string source,
        int sourceLength,
        StringBuilder? destination,
        int destinationLength,
        IntPtr versionInformation,
        IntPtr reserved,
        IntPtr sortHandle);
}

internal static class LyricsSourceMatcher
{
    public static int FindCurrentLine(IReadOnlyList<LyricLine> lines, string? sourceLyrics)
    {
        if (lines.Count == 0 || string.IsNullOrWhiteSpace(sourceLyrics)) return -1;
        var samples = sourceLyrics
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize)
            .Where(sample => sample.Length > 0)
            .ToArray();
        if (samples.Length == 0) return -1;

        var currentSource = samples[0];
        var nextSource = samples.Length > 1 ? samples[1] : string.Empty;
        var sourceContext = currentSource + nextSource;
        var bestIndex = -1;
        var bestScore = 0d;
        var bestCurrentScore = 0d;
        for (var index = 0; index < lines.Count; index++)
        {
            var currentCandidate = Normalize(lines[index].Text);
            var currentScore = Similarity(currentSource, currentCandidate);
            var nextCandidate = index + 1 < lines.Count ? Normalize(lines[index + 1].Text) : string.Empty;
            var contextScore = nextSource.Length > 0
                ? Similarity(sourceContext, currentCandidate + nextCandidate)
                : 0;
            var score = currentScore * 2 + contextScore;
            if (score <= bestScore) continue;
            bestIndex = index;
            bestScore = score;
            bestCurrentScore = currentScore;
        }
        return bestCurrentScore >= 0.55 ? bestIndex : -1;
    }

    static string Normalize(string text) =>
        new(text.Where(char.IsLetterOrDigit).ToArray());

    static double Similarity(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0) return 0;
        if (left.Contains(right, StringComparison.Ordinal) ||
            right.Contains(left, StringComparison.Ordinal))
            return 1;
        return LongestCommonSubstring(left, right) / (double)Math.Min(left.Length, right.Length);
    }

    static int LongestCommonSubstring(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var best = 0;
        for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            var current = new int[right.Length + 1];
            for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                if (left[leftIndex - 1] != right[rightIndex - 1]) continue;
                current[rightIndex] = previous[rightIndex - 1] + 1;
                best = Math.Max(best, current[rightIndex]);
            }
            previous = current;
        }
        return best;
    }
}

internal sealed class SourceLyricTimeline
{
    string? trackKey;
    int acceptedLineIndex = -1;
    int pendingLineIndex = -1;
    int pendingSamples;
    DateTimeOffset pendingFirstObservedAtUtc;
    DateTimeOffset? lastObservedAtUtc;
    TimeSpan anchorPosition;
    DateTimeOffset anchorSampledAtUtc;
    bool hasAnchor;
    bool wasPlaying;

    public TimeSpan? Update(
        string currentTrackKey,
        string? sourceLyrics,
        bool isPlaying,
        DateTimeOffset? sourceSampledAtUtc,
        LyricsDocument? document,
        DateTimeOffset nowUtc)
    {
        if (!string.Equals(trackKey, currentTrackKey, StringComparison.Ordinal))
            Reset(currentTrackKey, isPlaying, nowUtc);
        if (document is null || document.Lines.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(sourceLyrics) &&
            sourceSampledAtUtc is { } sampledAtUtc &&
            lastObservedAtUtc != sampledAtUtc)
        {
            lastObservedAtUtc = sampledAtUtc;
            Observe(
                LyricsSourceMatcher.FindCurrentLine(document.Lines, sourceLyrics),
                sampledAtUtc,
                isPlaying,
                document);
        }
        if (!hasAnchor) return null;

        var durationSeconds = document.DurationSeconds > 0
            ? document.DurationSeconds
            : (document.Lines[^1].Timestamp + TimeSpan.FromSeconds(12)).TotalSeconds;
        if (wasPlaying != isPlaying)
        {
            anchorPosition = DesktopMediaTimeline.EstimatePosition(
                anchorPosition, durationSeconds, anchorSampledAtUtc, nowUtc, wasPlaying);
            anchorSampledAtUtc = nowUtc;
            wasPlaying = isPlaying;
        }
        return DesktopMediaTimeline.EstimatePosition(
            anchorPosition, durationSeconds, anchorSampledAtUtc, nowUtc, isPlaying);
    }

    void Observe(
        int lineIndex,
        DateTimeOffset sampledAtUtc,
        bool isPlaying,
        LyricsDocument document)
    {
        if (lineIndex < 0) return;
        if (lineIndex == acceptedLineIndex)
        {
            pendingLineIndex = -1;
            pendingSamples = 0;
            return;
        }
        if (lineIndex != pendingLineIndex)
        {
            pendingLineIndex = lineIndex;
            pendingSamples = 1;
            pendingFirstObservedAtUtc = sampledAtUtc;
            return;
        }
        if (++pendingSamples < 2) return;

        acceptedLineIndex = lineIndex;
        anchorPosition = document.Lines[lineIndex].Timestamp;
        anchorSampledAtUtc = pendingFirstObservedAtUtc;
        hasAnchor = true;
        wasPlaying = isPlaying;
        pendingLineIndex = -1;
        pendingSamples = 0;
    }

    void Reset(string currentTrackKey, bool isPlaying, DateTimeOffset nowUtc)
    {
        trackKey = currentTrackKey;
        acceptedLineIndex = -1;
        pendingLineIndex = -1;
        pendingSamples = 0;
        pendingFirstObservedAtUtc = nowUtc;
        lastObservedAtUtc = null;
        anchorPosition = TimeSpan.Zero;
        anchorSampledAtUtc = nowUtc;
        hasAnchor = false;
        wasPlaying = isPlaying;
    }
}
public sealed record LyricsDocument(
    string TrackKey,
    IReadOnlyList<LyricLine> Lines,
    string Source,
    DateTimeOffset CachedAtUtc,
    double DurationSeconds = 0)
{
    public int CurrentLineIndex(TimeSpan position, int offsetMs = 0)
    {
        var adjusted = position + TimeSpan.FromMilliseconds(offsetMs);
        var low = 0;
        var high = Lines.Count - 1;
        var answer = -1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (Lines[middle].Timestamp <= adjusted)
            {
                answer = middle;
                low = middle + 1;
            }
            else high = middle - 1;
        }
        return answer;
    }

}

public static class LrcParser
{
    static readonly Regex TimestampPattern = new(
        @"\[(?<minute>\d{1,3}):(?<second>\d{1,2})(?:[.:](?<fraction>\d{1,3}))?\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<LyricLine> Parse(string? lrc)
    {
        if (string.IsNullOrWhiteSpace(lrc)) return [];
        var result = new List<LyricLine>();
        foreach (var rawLine in lrc.Replace("\r\n", "\n").Split('\n'))
        {
            var matches = TimestampPattern.Matches(rawLine);
            if (matches.Count == 0) continue;
            var text = LyricsTextNormalizer.ToSimplified(
                TimestampPattern.Replace(rawLine, string.Empty).Trim());
            if (text.Length == 0) continue;

            foreach (Match match in matches)
            {
                var minutes = int.Parse(match.Groups["minute"].Value, CultureInfo.InvariantCulture);
                var seconds = int.Parse(match.Groups["second"].Value, CultureInfo.InvariantCulture);
                if (seconds > 59) continue;
                var fraction = match.Groups["fraction"].Value;
                var milliseconds = fraction.Length switch
                {
                    1 => int.Parse(fraction, CultureInfo.InvariantCulture) * 100,
                    2 => int.Parse(fraction, CultureInfo.InvariantCulture) * 10,
                    3 => int.Parse(fraction, CultureInfo.InvariantCulture),
                    _ => 0
                };
                result.Add(new(
                    TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds) +
                    TimeSpan.FromMilliseconds(milliseconds),
                    text));
            }
        }

        return result
            .OrderBy(line => line.Timestamp)
            .GroupBy(line => (line.Timestamp, line.Text))
            .Select(group => group.First())
            .ToArray();
    }

    public static IReadOnlyList<LyricLine> ParsePlain(string? lyrics, double durationSeconds)
    {
        if (string.IsNullOrWhiteSpace(lyrics)) return [];
        var texts = lyrics.Replace("\r\n", "\n")
            .Split('\n')
            .Select(line => LyricsTextNormalizer.ToSimplified(line.Trim()))
            .Where(line => line.Length > 0)
            .ToArray();
        if (texts.Length == 0) return [];

        var duration = durationSeconds > 0
            ? durationSeconds
            : Math.Max(120, texts.Length * 6);
        var leadIn = Math.Min(8, duration * .03);
        var usableDuration = Math.Max(1, duration - leadIn);
        return texts.Select((text, index) => new LyricLine(
                TimeSpan.FromSeconds(leadIn + usableDuration * index / texts.Length),
                text))
            .ToArray();
    }
}

public static class LyricsMatcher
{
    public static int Score(
        string expectedTitle,
        string expectedArtist,
        TimeSpan expectedDuration,
        string candidateTitle,
        string candidateArtist,
        double candidateDurationSeconds)
    {
        var expectedTitleKey = Normalize(expectedTitle);
        var candidateTitleKey = Normalize(candidateTitle);
        if (expectedTitleKey.Length == 0 || candidateTitleKey.Length == 0) return 0;

        var score = expectedTitleKey == candidateTitleKey
            ? 70
            : expectedTitleKey.Contains(candidateTitleKey, StringComparison.Ordinal) ||
              candidateTitleKey.Contains(expectedTitleKey, StringComparison.Ordinal)
                ? 45
                : 0;

        var expectedArtistKey = Normalize(expectedArtist);
        var candidateArtistKey = Normalize(candidateArtist);
        if (expectedArtistKey.Length > 0 && candidateArtistKey.Length > 0)
        {
            if (expectedArtistKey == candidateArtistKey) score += 20;
            else if (expectedArtistKey.Contains(candidateArtistKey, StringComparison.Ordinal) ||
                     candidateArtistKey.Contains(expectedArtistKey, StringComparison.Ordinal)) score += 10;
        }

        if (expectedDuration > TimeSpan.Zero && candidateDurationSeconds > 0)
        {
            var difference = Math.Abs(expectedDuration.TotalSeconds - candidateDurationSeconds);
            score += difference <= 2 ? 10 : difference <= 5 ? 5 : 0;
        }
        return score;
    }

    static string Normalize(string value) =>
        string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();
}

/// <summary>从 LRCLIB 获取同步歌词，并将成功匹配结果缓存在本地。</summary>
public sealed class LyricsService : IDisposable
{
    readonly MediaSessionService media;
    readonly LifePreferencesService preferences;
    readonly HttpClient http;
    readonly string cacheDirectory;
    CancellationTokenSource? requestCancellation;
    LyricsDocument? current;
    string? requestedTrackKey;
    DateTimeOffset retryAfterUtc = DateTimeOffset.MinValue;
    bool requestInFlight;
    bool disposed;

    public LyricsService(MediaSessionService media, LifePreferencesService preferences)
    {
        this.media = media;
        this.preferences = preferences;
        http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ChronoIsle/1.0 (desktop-client)");
        cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChronoIsle", "lyrics-cache");
        media.SnapshotChanged += MediaSnapshotChanged;
    }

    public event Action? Changed;
    public LyricsDocument? Current => Volatile.Read(ref current);
    public string Status { get; private set; } = "等待播放音乐";

    public void Refresh()
    {
        retryAfterUtc = DateTimeOffset.MinValue;
        MediaSnapshotChanged(media.Current);
    }

    void MediaSnapshotChanged(MediaSessionSnapshot? snapshot)
    {
        if (disposed) return;
        if (!preferences.Load().LyricsEnabled || snapshot is null)
        {
            requestedTrackKey = null;
            retryAfterUtc = DateTimeOffset.MinValue;
            requestInFlight = false;
            requestCancellation?.Cancel();
            SetCurrent(null, snapshot is null ? "等待播放音乐" : "歌词显示已关闭");
            return;
        }
        var sameTrack = requestedTrackKey == snapshot.TrackKey;
        if (sameTrack &&
            (Current is not null || requestInFlight || DateTimeOffset.UtcNow < retryAfterUtc))
            return;

        requestedTrackKey = snapshot.TrackKey;
        if (!sameTrack) retryAfterUtc = DateTimeOffset.MinValue;
        requestCancellation?.Cancel();
        requestCancellation?.Dispose();
        requestCancellation = new CancellationTokenSource();
        requestInFlight = true;
        SetCurrent(null, sameTrack ? "正在重试歌词…" : "正在匹配歌词…");
        _ = LoadAsync(snapshot, requestCancellation.Token);
    }

    async Task LoadAsync(MediaSessionSnapshot snapshot, CancellationToken cancellationToken)
    {
        try
        {
            var cached = await ReadCacheAsync(snapshot.TrackKey, cancellationToken);
            if (cached is not null)
            {
                if (requestedTrackKey == snapshot.TrackKey) SetCurrent(cached, "同步歌词 · 本地缓存");
                return;
            }

            LyricsLoadResult? result = null;
            if (snapshot.SourceAppId.Contains("网易云", StringComparison.Ordinal))
                result = await LoadNeteaseLyricsAsync(snapshot, cancellationToken);
            result ??= await LoadLrcLibLyricsAsync(snapshot, cancellationToken);
            if (result is null)
            {
                if (requestedTrackKey == snapshot.TrackKey)
                {
                    retryAfterUtc = DateTimeOffset.UtcNow.AddMinutes(5);
                    SetCurrent(null, "未找到歌词，稍后自动重试");
                }
                return;
            }

            await WriteCacheAsync(result.Document, cancellationToken);
            if (requestedTrackKey == snapshot.TrackKey)
                SetCurrent(result.Document, result.Status);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Lyrics lookup failed: {exception.Message}");
            if (requestedTrackKey == snapshot.TrackKey)
            {
                retryAfterUtc = DateTimeOffset.UtcNow.AddSeconds(30);
                SetCurrent(null, "歌词服务暂时不可用，30 秒后重试");
            }
        }
        finally
        {
            if (requestedTrackKey == snapshot.TrackKey) requestInFlight = false;
        }
    }

    async Task<LyricsLoadResult?> LoadLrcLibLyricsAsync(
        MediaSessionSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var query = $"track_name={Uri.EscapeDataString(snapshot.Title)}";
        if (!string.IsNullOrWhiteSpace(snapshot.Artist))
            query += $"&artist_name={Uri.EscapeDataString(snapshot.Artist)}";
        var candidates = await SearchLrcLibAsync(query, cancellationToken);
        var best = BestLrcLibMatch(snapshot, candidates);
        if (best is null || best.Score < 45)
        {
            await Task.Delay(250, cancellationToken);
            var broadQuery = Uri.EscapeDataString(
                $"{snapshot.Title} {snapshot.Artist}".Trim());
            candidates = await SearchLrcLibAsync($"q={broadQuery}", cancellationToken);
            best = BestLrcLibMatch(snapshot, candidates);
        }
        if (best is null || best.Score < 45) return null;

        var lines = LrcParser.Parse(best.Candidate.SyncedLyrics);
        var status = "同步歌词 · LRCLIB";
        if (lines.Count == 0)
        {
            lines = LrcParser.ParsePlain(best.Candidate.PlainLyrics, best.Candidate.Duration);
            status = "普通歌词 · LRCLIB";
        }
        if (lines.Count == 0) return null;

        return new LyricsLoadResult(
            new LyricsDocument(
                snapshot.TrackKey,
                lines,
                "LRCLIB",
                DateTimeOffset.UtcNow,
                best.Candidate.Duration),
            status);
    }

    async Task<IReadOnlyList<LrcLibCandidate>> SearchLrcLibAsync(
        string query,
        CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(
            $"https://lrclib.net/api/search?{query}",
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<List<LrcLibCandidate>>(json) ?? [];
    }

    static LrcLibMatch? BestLrcLibMatch(
        MediaSessionSnapshot snapshot,
        IEnumerable<LrcLibCandidate> candidates) =>
        candidates
            .Where(candidate =>
                !string.IsNullOrWhiteSpace(candidate.SyncedLyrics) ||
                !string.IsNullOrWhiteSpace(candidate.PlainLyrics))
            .Select(candidate => new LrcLibMatch(
                candidate,
                LyricsMatcher.Score(
                    snapshot.Title,
                    snapshot.Artist,
                    snapshot.Duration,
                    candidate.TrackName ?? string.Empty,
                    candidate.ArtistName ?? string.Empty,
                    candidate.Duration)))
            .OrderByDescending(match => match.Score)
            .FirstOrDefault();

    async Task<LyricsLoadResult?> LoadNeteaseLyricsAsync(
        MediaSessionSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        try
        {
            var searchText = Uri.EscapeDataString(
                $"{snapshot.Title} {snapshot.Artist}".Trim());
            using var searchRequest = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://music.163.com/api/search/get/web?s={searchText}&type=1&offset=0&total=true&limit=10");
            searchRequest.Headers.Referrer = new Uri("https://music.163.com/");
            using var searchResponse = await http.SendAsync(searchRequest, cancellationToken);
            if (!searchResponse.IsSuccessStatusCode) return null;
            var searchJson = await searchResponse.Content.ReadAsStringAsync(cancellationToken);
            var search = JsonSerializer.Deserialize<NeteaseSearchResponse>(searchJson);
            var best = search?.Result?.Songs?
                .Select(song => new NeteaseSongMatch(
                    song,
                    LyricsMatcher.Score(
                        snapshot.Title,
                        snapshot.Artist,
                        snapshot.Duration,
                        song.Name ?? string.Empty,
                        string.Join("/", song.Artists?.Select(artist => artist.Name) ?? []),
                        song.Duration / 1000d)))
                .OrderByDescending(match => match.Score)
                .FirstOrDefault();
            if (best is null || best.Score < 45) return null;

            using var lyricRequest = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://music.163.com/api/song/lyric?os=pc&id={best.Song.Id}&lv=-1&kv=-1&tv=-1");
            lyricRequest.Headers.Referrer = new Uri("https://music.163.com/");
            using var lyricResponse = await http.SendAsync(lyricRequest, cancellationToken);
            if (!lyricResponse.IsSuccessStatusCode) return null;
            var lyricJson = await lyricResponse.Content.ReadAsStringAsync(cancellationToken);
            var lyric = JsonSerializer.Deserialize<NeteaseLyricsResponse>(lyricJson);
            var lines = LrcParser.Parse(lyric?.Lrc?.Lyric);
            if (lines.Count == 0) return null;

            var durationSeconds = best.Song.Duration / 1000d;
            return new LyricsLoadResult(
                new LyricsDocument(
                    snapshot.TrackKey,
                    lines,
                    "网易云音乐",
                    DateTimeOffset.UtcNow,
                    durationSeconds),
                "同步歌词 · 网易云音乐");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine($"NetEase lyrics lookup failed: {exception.Message}");
            return null;
        }
    }

    async Task<LyricsDocument?> ReadCacheAsync(string trackKey, CancellationToken cancellationToken)
    {
        var path = CachePath(trackKey);
        if (!File.Exists(path)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            var document = JsonSerializer.Deserialize<LyricsDocument>(json);
            if (document is null ||
                document.CachedAtUtc < DateTimeOffset.UtcNow.AddDays(-30) ||
                document.Lines.Count == 0)
                return null;
            return document with
            {
                Lines = document.Lines
                    .Select(line => line with
                    {
                        Text = LyricsTextNormalizer.ToSimplified(line.Text)
                    })
                    .ToArray()
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    async Task WriteCacheAsync(LyricsDocument document, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(cacheDirectory);
        await File.WriteAllTextAsync(
            CachePath(document.TrackKey),
            JsonSerializer.Serialize(document),
            cancellationToken);
    }

    string CachePath(string trackKey)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(trackKey)));
        return Path.Combine(cacheDirectory, $"{hash}.json");
    }

    void SetCurrent(LyricsDocument? document, string status)
    {
        Interlocked.Exchange(ref current, document);
        Status = status;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        disposed = true;
        media.SnapshotChanged -= MediaSnapshotChanged;
        requestCancellation?.Cancel();
        requestCancellation?.Dispose();
        http.Dispose();
    }

    sealed record LrcLibCandidate(
        [property: JsonPropertyName("trackName")] string? TrackName,
        [property: JsonPropertyName("artistName")] string? ArtistName,
        [property: JsonPropertyName("duration")] double Duration,
        [property: JsonPropertyName("syncedLyrics")] string? SyncedLyrics,
        [property: JsonPropertyName("plainLyrics")] string? PlainLyrics);

    sealed record LrcLibMatch(LrcLibCandidate Candidate, int Score);
    sealed record LyricsLoadResult(LyricsDocument Document, string Status);

    sealed record NeteaseSearchResponse(
        [property: JsonPropertyName("result")] NeteaseSearchResult? Result);

    sealed record NeteaseSearchResult(
        [property: JsonPropertyName("songs")] IReadOnlyList<NeteaseSong>? Songs);

    sealed record NeteaseSong(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("artists")] IReadOnlyList<NeteaseArtist>? Artists,
        [property: JsonPropertyName("album")] NeteaseAlbum? Album,
        [property: JsonPropertyName("duration")] long Duration);

    sealed record NeteaseArtist(
        [property: JsonPropertyName("name")] string? Name);

    sealed record NeteaseAlbum(
        [property: JsonPropertyName("name")] string? Name);

    sealed record NeteaseSongMatch(NeteaseSong Song, int Score);

    sealed record NeteaseLyricsResponse(
        [property: JsonPropertyName("lrc")] NeteaseLyricPart? Lrc);

    sealed record NeteaseLyricPart(
        [property: JsonPropertyName("lyric")] string? Lyric);
}

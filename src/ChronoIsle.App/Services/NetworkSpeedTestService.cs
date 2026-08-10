namespace ChronoIsle.App.Services;

public enum NetworkSpeedTestPhase
{
    Idle,
    SelectingNode,
    MeasuringPlatforms,
    MeasuringDownload,
    MeasuringUpload,
    Completed,
    Unavailable,
    Cancelled
}

public sealed record NetworkSpeedTestNode(string Name, Uri BaseUri);

internal sealed record NetworkSpeedTestNodeProbe(NetworkSpeedTestNode Node, IReadOnlyList<long> Latencies);

public sealed record PlatformConnectionLatency(string Key, string Name, long? Milliseconds);

public sealed record NetworkSpeedTestSnapshot(
    NetworkSpeedTestPhase Phase,
    string Status,
    double? DownloadMegabitsPerSecond,
    double? UploadMegabitsPerSecond,
    long? NodeLatencyMilliseconds,
    IReadOnlyList<PlatformConnectionLatency> Platforms)
{
    public bool IsRunning => Phase is NetworkSpeedTestPhase.SelectingNode
        or NetworkSpeedTestPhase.MeasuringPlatforms
        or NetworkSpeedTestPhase.MeasuringDownload
        or NetworkSpeedTestPhase.MeasuringUpload;

    public static NetworkSpeedTestSnapshot Idle { get; } = new(
        NetworkSpeedTestPhase.Idle,
        "准备测速",
        null,
        null,
        null,
        []);
}

public sealed class NetworkSpeedTestService
{
    internal static NetworkSpeedTestNode? SelectNode(IEnumerable<NetworkSpeedTestNodeProbe> probes)
    {
        return probes
            .Where(probe => probe.Latencies.Count > 0)
            .OrderBy(probe =>
            {
                var latencies = probe.Latencies.Order().ToArray();
                var middle = latencies.Length / 2;
                return latencies.Length % 2 == 0
                    ? latencies[middle - 1] / 2d + latencies[middle] / 2d
                    : latencies[middle];
            })
            .Select(probe => probe.Node)
            .FirstOrDefault();
    }

    internal static double ToMegabitsPerSecond(long bytes, TimeSpan elapsed)
    {
        return bytes <= 0 || elapsed <= TimeSpan.Zero
            ? 0
            : bytes * 8d / elapsed.TotalSeconds / 1_000_000d;
    }
}

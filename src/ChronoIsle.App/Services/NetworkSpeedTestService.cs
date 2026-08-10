using System.Buffers;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;

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

public sealed class NetworkSpeedTestService : IDisposable
{
    const int TransferWorkerCount = 4;
    static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(1.5);
    static readonly TimeSpan DefaultTransferDuration = TimeSpan.FromSeconds(7);
    static readonly TimeSpan DefaultSnapshotInterval = TimeSpan.FromMilliseconds(250);

    static readonly NetworkSpeedTestNode[] DomesticNodes =
    [
        new("清华大学", new Uri("https://iptv.tsinghua.edu.cn/st/")),
        new("武汉大学图书馆", new Uri("https://www.lib.whu.edu.cn/speedtest/backend/"))
    ];

    static readonly PlatformTarget[] Platforms =
    [
        new("league", "英雄联盟", new Uri("https://lol.qq.com/favicon.ico")),
        new("douyin", "抖音", new Uri("https://douyin.com/favicon.ico")),
        new("jd", "京东", new Uri("https://jd.com/favicon.ico")),
        new("ctrip", "携程", new Uri("https://ctrip.com/favicon.ico")),
        new("toutiao", "今日头条", new Uri("https://toutiao.com/favicon.ico"))
    ];

    readonly object stateGate = new();
    readonly HttpClient httpClient;
    readonly TimeSpan probeTimeout;
    readonly TimeSpan transferDuration;
    readonly TimeSpan snapshotInterval;
    CancellationTokenSource? activeSession;
    NetworkSpeedTestSnapshot current = NetworkSpeedTestSnapshot.Idle;
    bool disposed;

    public NetworkSpeedTestService()
        : this(new HttpClient(), DefaultProbeTimeout, DefaultTransferDuration, DefaultSnapshotInterval)
    {
    }

    internal NetworkSpeedTestService(
        HttpMessageHandler handler,
        TimeSpan probeTimeout,
        TimeSpan transferDuration,
        TimeSpan snapshotInterval)
        : this(new HttpClient(handler, disposeHandler: true), probeTimeout, transferDuration, snapshotInterval)
    {
    }

    NetworkSpeedTestService(
        HttpClient httpClient,
        TimeSpan probeTimeout,
        TimeSpan transferDuration,
        TimeSpan snapshotInterval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(probeTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(transferDuration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(snapshotInterval, TimeSpan.Zero);

        this.httpClient = httpClient;
        this.httpClient.Timeout = Timeout.InfiniteTimeSpan;
        this.probeTimeout = probeTimeout;
        this.transferDuration = transferDuration;
        this.snapshotInterval = snapshotInterval;
    }

    public event Action<NetworkSpeedTestSnapshot>? SnapshotChanged;

    public NetworkSpeedTestSnapshot Current
    {
        get
        {
            lock (stateGate)
            {
                return current;
            }
        }
    }

    public Task StartAsync()
    {
        CancellationTokenSource cancellation;
        NetworkSpeedTestSnapshot snapshot;

        lock (stateGate)
        {
            ThrowIfDisposed();
            if (current.IsRunning)
            {
                return Task.CompletedTask;
            }

            cancellation = new CancellationTokenSource();
            activeSession = cancellation;
            snapshot = NetworkSpeedTestSnapshot.Idle with
            {
                Phase = NetworkSpeedTestPhase.SelectingNode,
                Status = "正在选择国内节点"
            };
            current = snapshot;
        }

        return RunSessionAsync(cancellation, snapshot);
    }

    public void Cancel()
    {
        CancellationTokenSource? cancellation;
        lock (stateGate)
        {
            cancellation = activeSession;
        }

        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        lock (stateGate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            cancellation = activeSession;
            activeSession = null;
        }

        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        httpClient.Dispose();
    }

    async Task RunSessionAsync(CancellationTokenSource cancellation, NetworkSpeedTestSnapshot initialSnapshot)
    {
        var cancellationToken = cancellation.Token;
        try
        {
            RaiseSnapshotChanged(initialSnapshot);
            var probes = await Task.WhenAll(DomesticNodes.Select(node => ProbeNodeAsync(node, cancellationToken))).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var selectedNode = SelectNode(probes);
            if (selectedNode is null)
            {
                Publish(new NetworkSpeedTestSnapshot(
                    NetworkSpeedTestPhase.Unavailable,
                    "暂时没有可用的国内测速节点，请稍后重试",
                    null,
                    null,
                    null,
                    []));
                return;
            }

            var selectedProbe = probes.First(probe => probe.Node == selectedNode);
            var nodeLatency = GetRoundedMedian(selectedProbe.Latencies);
            var pendingPlatforms = Platforms
                .Select(platform => new PlatformConnectionLatency(platform.Key, platform.Name, null))
                .ToArray();
            Publish(new NetworkSpeedTestSnapshot(
                NetworkSpeedTestPhase.MeasuringPlatforms,
                "正在检测平台连接延迟",
                null,
                null,
                nodeLatency,
                pendingPlatforms));

            var platformLatencies = await Task.WhenAll(Platforms.Select(platform => ProbePlatformAsync(platform, cancellationToken))).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            Publish(new NetworkSpeedTestSnapshot(
                NetworkSpeedTestPhase.MeasuringDownload,
                "正在测速下载",
                null,
                null,
                nodeLatency,
                platformLatencies));
            var download = await MeasureTransferAsync(selectedNode, isDownload: true, nodeLatency, platformLatencies, null, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            Publish(new NetworkSpeedTestSnapshot(
                NetworkSpeedTestPhase.MeasuringUpload,
                "正在测速上传",
                download,
                null,
                nodeLatency,
                platformLatencies));
            var upload = await MeasureTransferAsync(selectedNode, isDownload: false, nodeLatency, platformLatencies, download, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            Publish(new NetworkSpeedTestSnapshot(
                NetworkSpeedTestPhase.Completed,
                "测速完成",
                download,
                upload,
                nodeLatency,
                platformLatencies));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var snapshot = Current;
            Publish(snapshot with
            {
                Phase = NetworkSpeedTestPhase.Cancelled,
                Status = "测速已取消"
            });
        }
        catch
        {
            var snapshot = Current;
            Publish(snapshot with
            {
                Phase = NetworkSpeedTestPhase.Unavailable,
                Status = "测速暂时不可用，请稍后重试"
            });
        }
        finally
        {
            lock (stateGate)
            {
                if (ReferenceEquals(activeSession, cancellation))
                {
                    activeSession = null;
                }
            }

            cancellation.Dispose();
        }
    }

    async Task<NetworkSpeedTestNodeProbe> ProbeNodeAsync(NetworkSpeedTestNode node, CancellationToken cancellationToken)
    {
        var samples = await Task.WhenAll(
            ProbeMillisecondsAsync(CreateNodeUri(node, "empty.php?x="), cancellationToken),
            ProbeMillisecondsAsync(CreateNodeUri(node, "empty.php?x="), cancellationToken)).ConfigureAwait(false);

        return new NetworkSpeedTestNodeProbe(node, samples.Where(sample => sample.HasValue).Select(sample => sample!.Value).ToArray());
    }

    async Task<PlatformConnectionLatency> ProbePlatformAsync(PlatformTarget platform, CancellationToken cancellationToken)
    {
        var milliseconds = await ProbeMillisecondsAsync(platform.Uri, cancellationToken).ConfigureAwait(false);
        return new PlatformConnectionLatency(platform.Key, platform.Name, milliseconds);
    }

    async Task<long?> ProbeMillisecondsAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(probeTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            var stopwatch = Stopwatch.StartNew();
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            stopwatch.Stop();
            return response.IsSuccessStatusCode
                ? Math.Max(0, (long)Math.Round(stopwatch.Elapsed.TotalMilliseconds, MidpointRounding.AwayFromZero))
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    async Task<double> MeasureTransferAsync(
        NetworkSpeedTestNode node,
        bool isDownload,
        long nodeLatency,
        IReadOnlyList<PlatformConnectionLatency> platformLatencies,
        double? completedDownload,
        CancellationToken cancellationToken)
    {
        long totalBytes = 0;
        var stopwatch = Stopwatch.StartNew();
        using var phaseCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        phaseCancellation.CancelAfter(transferDuration);

        var workers = Enumerable.Range(0, TransferWorkerCount).Select(_ => TransferWorkerAsync()).ToArray();
        var workersTask = Task.WhenAll(workers);
        try
        {
            while (!workersTask.IsCompleted)
            {
                var remaining = transferDuration - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                await Task.Delay(remaining < snapshotInterval ? remaining : snapshotInterval, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                PublishTransferProgress(ToMegabitsPerSecond(Interlocked.Read(ref totalBytes), stopwatch.Elapsed));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                await workersTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }

            throw;
        }
        catch
        {
            phaseCancellation.Cancel();
            try
            {
                await workersTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (phaseCancellation.IsCancellationRequested)
            {
            }

            throw;
        }

        await workersTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        stopwatch.Stop();
        return ToMegabitsPerSecond(Interlocked.Read(ref totalBytes), stopwatch.Elapsed);

        async Task TransferWorkerAsync()
        {
            while (!phaseCancellation.IsCancellationRequested)
            {
                var shouldPauseBeforeRetry = false;
                try
                {
                    shouldPauseBeforeRetry = isDownload
                        ? !(await DownloadAsync().ConfigureAwait(false))
                        : !(await UploadAsync().ConfigureAwait(false));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException) when (phaseCancellation.IsCancellationRequested)
                {
                    return;
                }
                catch
                {
                    shouldPauseBeforeRetry = true;
                }

                if (!shouldPauseBeforeRetry)
                {
                    continue;
                }

                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(50), phaseCancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException) when (phaseCancellation.IsCancellationRequested)
                {
                    return;
                }
            }
        }

        async Task<bool> DownloadAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, CreateNodeUri(node, "garbage.php?ckSize=10485760&x="));
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, phaseCancellation.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(phaseCancellation.Token).ConfigureAwait(false);
            var buffer = ArrayPool<byte>.Shared.Rent(81_920);
            try
            {
                int bytesRead;
                while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(), phaseCancellation.Token).ConfigureAwait(false)) > 0)
                {
                    Interlocked.Add(ref totalBytes, bytesRead);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return true;
        }

        async Task<bool> UploadAsync()
        {
            var payload = new byte[1_048_576];
            RandomNumberGenerator.Fill(payload);
            using var request = new HttpRequestMessage(HttpMethod.Post, CreateNodeUri(node, "empty.php?x="))
            {
                Content = new ByteArrayContent(payload)
            };
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, phaseCancellation.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                Interlocked.Add(ref totalBytes, payload.Length);
                return true;
            }

            return false;
        }

        void PublishTransferProgress(double rate)
        {
            var snapshot = new NetworkSpeedTestSnapshot(
                isDownload ? NetworkSpeedTestPhase.MeasuringDownload : NetworkSpeedTestPhase.MeasuringUpload,
                isDownload ? "正在测速下载" : "正在测速上传",
                isDownload ? rate : completedDownload,
                isDownload ? null : rate,
                nodeLatency,
                platformLatencies);
            Publish(snapshot);
        }
    }

    static Uri CreateNodeUri(NetworkSpeedTestNode node, string relativePathAndQueryPrefix) =>
        new(node.BaseUri, relativePathAndQueryPrefix + Guid.NewGuid().ToString("N"));

    static long GetRoundedMedian(IReadOnlyList<long> latencies)
    {
        var ordered = latencies.Order().ToArray();
        var middle = ordered.Length / 2;
        var median = ordered.Length % 2 == 0
            ? ordered[middle - 1] / 2d + ordered[middle] / 2d
            : ordered[middle];
        return (long)Math.Round(median, MidpointRounding.AwayFromZero);
    }

    void Publish(NetworkSpeedTestSnapshot snapshot)
    {
        lock (stateGate)
        {
            current = snapshot;
        }

        RaiseSnapshotChanged(snapshot);
    }

    void RaiseSnapshotChanged(NetworkSpeedTestSnapshot snapshot) => SnapshotChanged?.Invoke(snapshot);

    void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(NetworkSpeedTestService));
        }
    }

    sealed record PlatformTarget(string Key, string Name, Uri Uri);

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

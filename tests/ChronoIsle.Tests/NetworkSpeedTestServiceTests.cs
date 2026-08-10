using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Reflection;
using ChronoIsle.App.Services;

namespace ChronoIsle.Tests;

public sealed class NetworkSpeedTestServiceTests
{
    static readonly TimeSpan FastProbeTimeout = TimeSpan.FromMilliseconds(20);
    static readonly TimeSpan FastTransferDuration = TimeSpan.FromMilliseconds(20);
    static readonly TimeSpan FastSnapshotInterval = TimeSpan.FromMilliseconds(20);

    [Fact]
    public void SelectNode_UsesLowestMedianAcrossSuccessfulDomesticCandidates()
    {
        var tsinghua = new NetworkSpeedTestNode("清华大学", new Uri("https://iptv.tsinghua.edu.cn/st/"));
        var wuhan = new NetworkSpeedTestNode("武汉大学图书馆", new Uri("https://www.lib.whu.edu.cn/speedtest/backend/"));
        var unavailable = new NetworkSpeedTestNode("空样本节点", new Uri("https://empty.example/"));
        var selected = NetworkSpeedTestService.SelectNode(
        [
            new NetworkSpeedTestNodeProbe(unavailable, []),
            new NetworkSpeedTestNodeProbe(tsinghua, [10, 100]),
            new NetworkSpeedTestNodeProbe(wuhan, [60, 60])
        ]);

        Assert.Equal(tsinghua, selected);
    }

    [Fact]
    public void DomesticNodes_UsesPublishedChineseEducationBackends()
    {
        var field = typeof(NetworkSpeedTestService).GetField("DomesticNodes", BindingFlags.Static | BindingFlags.NonPublic);
        var nodes = Assert.IsType<NetworkSpeedTestNode[]>(field?.GetValue(null));

        Assert.True(nodes.Select(node => node.BaseUri.Host).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 5);
        Assert.Contains(nodes, node => node.BaseUri.Host == "speed.nuaa.edu.cn" && node.BaseUri.AbsolutePath == "/backend/");
        Assert.Contains(nodes, node => node.BaseUri.Host == "wsus.sjtu.edu.cn" && node.BaseUri.AbsolutePath == "/speedtest/backend/");
        Assert.Contains(nodes, node => node.BaseUri.Host == "test.ustc.edu.cn" && node.BaseUri.AbsolutePath == "/backend/");
    }

    [Theory]
    [InlineData(12_500_000L, 2.0, 50.0)]
    [InlineData(0L, 5.0, 0.0)]
    [InlineData(1L, 0.0, 0.0)]
    public void ToMegabitsPerSecond_UsesActualBytesAndElapsedTime(long bytes, double seconds, double expected)
    {
        Assert.Equal(expected, NetworkSpeedTestService.ToMegabitsPerSecond(bytes, TimeSpan.FromSeconds(seconds)), 3);
    }

    [Fact]
    public async Task StartAsync_WhenAllNodeProbesFail_ReportsFriendlyUnavailableWithoutTransferRates()
    {
        using var service = CreateService(new AllNodeProbesFailHandler());

        await service.StartAsync();

        var snapshot = service.Current;
        Assert.Equal(NetworkSpeedTestPhase.Unavailable, snapshot.Phase);
        Assert.Equal("暂时没有可用的国内测速节点，请稍后重试", snapshot.Status);
        Assert.Null(snapshot.DownloadMegabitsPerSecond);
        Assert.Null(snapshot.UploadMegabitsPerSecond);
        Assert.Null(snapshot.NodeLatencyMilliseconds);
    }

    [Fact]
    public async Task StartAsync_WhenPlatformProbesTimeout_KeepsEveryPlatformCardWithoutExceptionText()
    {
        var handler = new PlatformTimeoutHandler();
        using var service = CreateService(handler);

        await service.StartAsync();

        var snapshot = service.Current;
        Assert.Equal(NetworkSpeedTestPhase.Completed, snapshot.Phase);
        Assert.Equal(5, snapshot.Platforms.Count);
        Assert.All(snapshot.Platforms, platform => Assert.Null(platform.Milliseconds));
        Assert.DoesNotContain("Exception", snapshot.Status);
        Assert.Equal(
            new[]
            {
                "https://lol.qq.com/favicon.ico",
                "https://douyin.com/favicon.ico",
                "https://jd.com/favicon.ico",
                "https://ctrip.com/favicon.ico",
                "https://toutiao.com/favicon.ico"
            }.Order(),
            handler.PlatformRequests.Select(uri => uri.AbsoluteUri).Order());
    }

    [Fact]
    public async Task Cancel_WhenRequestWaitsForCancellation_PublishesCancelledSnapshot()
    {
        var handler = new WaitingForCancellationHandler();
        using var service = CreateService(handler);

        var session = service.StartAsync();
        await handler.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(1));
        service.Cancel();
        await session.WaitAsync(TimeSpan.FromSeconds(1));

        var snapshot = service.Current;
        Assert.Equal(NetworkSpeedTestPhase.Cancelled, snapshot.Phase);
        Assert.Equal("测速已取消", snapshot.Status);
    }

    [Fact]
    public async Task StartAsync_WhenSessionIsRunning_DoesNotStartAnotherSelection()
    {
        var handler = new BlockingNodeProbeHandler();
        using var service = CreateService(handler, probeTimeout: TimeSpan.FromSeconds(1));
        var phases = new ConcurrentQueue<NetworkSpeedTestPhase>();
        service.SnapshotChanged += snapshot => phases.Enqueue(snapshot.Phase);

        var firstSession = service.StartAsync();
        await handler.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var requestCount = handler.RequestCount;
        var selectingSnapshots = phases.Count(phase => phase == NetworkSpeedTestPhase.SelectingNode);

        await service.StartAsync();

        Assert.Equal(requestCount, handler.RequestCount);
        Assert.Equal(selectingSnapshots, phases.Count(phase => phase == NetworkSpeedTestPhase.SelectingNode));

        service.Cancel();
        await firstSession.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(NetworkSpeedTestPhase.Cancelled, service.Current.Phase);
    }

    [Fact]
    public async Task Cancel_DuringDownload_WaitsForWorkersToExitBeforeCompletingSession()
    {
        var handler = new BlockingDownloadHandler();
        using var service = CreateService(handler, transferDuration: TimeSpan.FromSeconds(1));

        var session = service.StartAsync();
        await handler.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        service.Cancel();
        await session.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(0, handler.ActiveDownloadWorkers);
        Assert.Equal(NetworkSpeedTestPhase.Cancelled, service.Current.Phase);
    }

    [Fact]
    public async Task StartAsync_WhenDownloadProgressSubscriberThrows_WaitsForWorkersBeforeReportingUnavailable()
    {
        var handler = new BlockingDownloadHandler();
        using var service = CreateService(handler, transferDuration: TimeSpan.FromSeconds(1));
        var downloadSnapshots = 0;
        service.SnapshotChanged += snapshot =>
        {
            if (snapshot.Phase == NetworkSpeedTestPhase.MeasuringDownload
                && Interlocked.Increment(ref downloadSnapshots) == 2)
            {
                throw new InvalidOperationException("test subscriber failure");
            }
        };

        var session = service.StartAsync();
        await handler.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await session.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(0, handler.ActiveDownloadWorkers);
        Assert.Equal(NetworkSpeedTestPhase.Unavailable, service.Current.Phase);
    }

    [Fact]
    public async Task StartAsync_WhenSelectingNodeSubscriberThrows_ClearsSessionAndAllowsRestart()
    {
        var handler = new BlockingNodeProbeHandler();
        using var service = CreateService(handler, probeTimeout: TimeSpan.FromSeconds(1));
        var selectingSnapshots = 0;
        var throwOnFirstSelectingSnapshot = 1;
        service.SnapshotChanged += snapshot =>
        {
            if (snapshot.Phase == NetworkSpeedTestPhase.SelectingNode)
            {
                Interlocked.Increment(ref selectingSnapshots);
                if (Interlocked.Exchange(ref throwOnFirstSelectingSnapshot, 0) == 1)
                {
                    throw new InvalidOperationException("test selecting snapshot failure");
                }
            }
        };

        await service.StartAsync();

        Assert.Equal(NetworkSpeedTestPhase.Unavailable, service.Current.Phase);
        Assert.False(service.Current.IsRunning);

        var restartedSession = service.StartAsync();
        await handler.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(2, Volatile.Read(ref selectingSnapshots));
        Assert.True(handler.RequestCount > 0);

        service.Cancel();
        await restartedSession.WaitAsync(TimeSpan.FromSeconds(1));
    }

    static NetworkSpeedTestService CreateService(
        HttpMessageHandler handler,
        TimeSpan? probeTimeout = null,
        TimeSpan? transferDuration = null) => new(
        handler,
        probeTimeout ?? FastProbeTimeout,
        transferDuration ?? FastTransferDuration,
        FastSnapshotInterval);

    sealed class AllNodeProbesFailHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
    }

    sealed class PlatformTimeoutHandler : HttpMessageHandler
    {
        public ConcurrentBag<Uri> PlatformRequests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (request.Method == HttpMethod.Get && uri.AbsolutePath.EndsWith("empty.php", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (uri.AbsolutePath.EndsWith("favicon.ico", StringComparison.Ordinal))
            {
                PlatformRequests.Add(uri);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (uri.AbsolutePath.EndsWith("garbage.php", StringComparison.Ordinal))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(2), cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(new byte[1024])
                };
            }

            if (request.Method == HttpMethod.Post)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(2), cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            return new HttpResponseMessage(HttpStatusCode.BadGateway);
        }
    }

    sealed class WaitingForCancellationHandler : HttpMessageHandler
    {
        public TaskCompletionSource FirstRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            FirstRequest.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    sealed class BlockingNodeProbeHandler : HttpMessageHandler
    {
        int requestCount;

        public TaskCompletionSource FirstRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RequestCount => Volatile.Read(ref requestCount);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requestCount);
            FirstRequest.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    sealed class BlockingDownloadHandler : HttpMessageHandler
    {
        int activeDownloadWorkers;

        public TaskCompletionSource DownloadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ActiveDownloadWorkers => Volatile.Read(ref activeDownloadWorkers);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("garbage.php", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref activeDownloadWorkers);
                DownloadStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                finally
                {
                    Interlocked.Decrement(ref activeDownloadWorkers);
                }
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}

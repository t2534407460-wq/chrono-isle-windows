using ChronoIsle.App.Services;
using System.Reflection;

namespace ChronoIsle.Tests;

public sealed class ForegroundFpsServiceTests
{
    [Fact]
    public async Task CaptureFailure_RetriesWithoutRestartingTheService()
    {
        var attempts = 0;
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new ForegroundFpsService(() =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
                throw new InvalidOperationException("capture interrupted");
            recovered.TrySetResult();
        });

        service.Start();
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        service.Stop();
        Assert.Equal(2, Volatile.Read(ref attempts));
    }

    [Fact]
    public async Task RepeatedStart_AndQuickStopStart_KeepOneCaptureUntilItExits()
    {
        var attempts = 0;
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new ForegroundFpsService(() =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                entered.TrySetResult();
                release.Wait(TimeSpan.FromSeconds(10));
            }
            else resumed.TrySetResult();
        });

        try
        {
            service.Start();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            service.Start();
            service.Stop();
            service.Start();
            Assert.Equal(1, Volatile.Read(ref attempts));
            release.Set();
            await resumed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            service.Stop();
            Assert.Equal(2, Volatile.Read(ref attempts));
        }
        finally { release.Set(); }
    }

    [Fact]
    public void LegacySessionCleanup_SelectsOnlyPresentMonSessionNames()
    {
        var method = typeof(ForegroundFpsService).GetMethod(
            "GetLegacyPresentMonSessionNames",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var names = Assert.IsAssignableFrom<IEnumerable<string>>(method!.Invoke(null,
            new object?[] { new[] { "ChronoIsleFps-25036", "ChronoIsleFps90160", "Eventlog-Security" } })!);

        Assert.Equal(["ChronoIsleFps-25036", "ChronoIsleFps90160"], names.OrderBy(n => n));
    }

    [Fact]
    public void ForegroundSwitch_IgnoresSharedDwmProcess()
    {
        var method = typeof(ForegroundFpsService).GetMethod(
            "IsForegroundApplicationChanged",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var changed = Assert.IsType<bool>(method!.Invoke(null,
            new object?[]
            {
                10,
                20,
                new HashSet<int> { 10, 100 },
                new HashSet<int> { 20, 100 },
                100
            }));

        Assert.True(changed);
    }

    [Fact]
    public void FrameRate_UsesTheLatestEventSecondInsteadOfWallClockTime()
    {
        var now = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new ForegroundFrameSample(now.AddMilliseconds(-1800)),
            new ForegroundFrameSample(now.AddMilliseconds(-1300)),
            new ForegroundFrameSample(now.AddMilliseconds(-800))
        };

        Assert.Equal(2d, ForegroundFrameRate.Calculate(samples, now));
    }

    [Fact]
    public void FrameRate_ReturnsNullWhenTheNewestEventIsStale()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.Null(ForegroundFrameRate.Calculate(
            [
                new ForegroundFrameSample(now.AddSeconds(-4)),
                new ForegroundFrameSample(now.AddSeconds(-3))
            ], now));
    }
}

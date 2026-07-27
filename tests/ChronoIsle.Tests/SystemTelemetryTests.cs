using ChronoIsle.App.Services;

namespace ChronoIsle.Tests;

public sealed class SystemTelemetryTests
{
    [Theory]
    [InlineData(0u, 19.4, 19.4)]
    [InlineData(1u, 120.0, 100.0)]
    [InlineData(0u, -5.0, 0.0)]
    public void ProcessorUtilitySampler_NormalizesValidWindowsValues(
        uint status,
        double value,
        double expected)
    {
        var normalized = ProcessorUtilitySampler.Normalize(status, value);

        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(2u, 20.0)]
    [InlineData(0u, double.NaN)]
    [InlineData(0u, double.PositiveInfinity)]
    public void ProcessorUtilitySampler_RejectsInvalidWindowsValues(uint status, double value)
    {
        Assert.Null(ProcessorUtilitySampler.Normalize(status, value));
    }

    [Theory]
    [InlineData(false, 40, 5, NetworkHealth.Offline)]
    [InlineData(true, null, null, NetworkHealth.Unstable)]
    [InlineData(true, 45, null, NetworkHealth.Connected)]
    [InlineData(true, 300, 5, NetworkHealth.Unstable)]
    [InlineData(true, 45, 80, NetworkHealth.Unstable)]
    public void NetworkHealth_UsesReachabilityLatencyAndJitter(
        bool hasNetwork,
        int? latencyMilliseconds,
        int? latencyDeltaMilliseconds,
        NetworkHealth expected)
    {
        long? latency = latencyMilliseconds;
        long? latencyDelta = latencyDeltaMilliseconds;
        Assert.Equal(expected, SystemTelemetryService.ClassifyNetwork(
            hasNetwork, latency, latencyDelta));
    }
    [Fact]
    public void TrafficAccumulator_ComputesSpeedAndDailyTotals()
    {
        var start = new DateTimeOffset(2026, 7, 27, 10, 0, 0, TimeSpan.Zero);
        var accumulator = new TrafficAccumulator();

        accumulator.Update(start, 1_000, 2_000);
        var sample = accumulator.Update(start.AddSeconds(2), 3_000, 8_000);
        var today = accumulator.ForDay(DateOnly.FromDateTime(start.LocalDateTime));

        Assert.Equal(1_000, sample.UploadSpeed);
        Assert.Equal(3_000, sample.DownloadSpeed);
        Assert.Equal(2_000UL, today.UploadedBytes);
        Assert.Equal(6_000UL, today.DownloadedBytes);
    }

    [Fact]
    public void TrafficAccumulator_HandlesCounterResetWithoutInventingTraffic()
    {
        var start = new DateTimeOffset(2026, 7, 27, 10, 0, 0, TimeSpan.Zero);
        var accumulator = new TrafficAccumulator();
        accumulator.Update(start, 10_000, 20_000);

        var sample = accumulator.Update(start.AddSeconds(1), 100, 200);

        Assert.Equal(0, sample.UploadSpeed);
        Assert.Equal(0, sample.DownloadSpeed);
        Assert.Equal(0UL, sample.UploadedDelta);
        Assert.Equal(0UL, sample.DownloadedDelta);
    }

    [Fact]
    public void TrafficAccumulator_ReturnsSevenContinuousDays()
    {
        var today = new DateOnly(2026, 7, 27);
        var accumulator = new TrafficAccumulator(
        [
            new DailyTraffic(today.AddDays(-1), 12, 34)
        ]);

        var recent = accumulator.Recent(today, 7);

        Assert.Equal(7, recent.Count);
        Assert.Equal(today.AddDays(-6), recent[0].Day);
        Assert.Equal(today, recent[^1].Day);
        Assert.Equal(12UL, recent[^2].UploadedBytes);
    }
}

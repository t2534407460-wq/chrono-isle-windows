using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ChronoIsle.App.Services;

public enum NetworkHealth
{
    Offline,
    Poor,
    Connected,
    Busy
}

public sealed record DailyTraffic(DateOnly Day, ulong UploadedBytes, ulong DownloadedBytes);

public sealed record SystemTelemetrySnapshot(
    double UploadBytesPerSecond,
    double DownloadBytesPerSecond,
    ulong TodayUploadedBytes,
    ulong TodayDownloadedBytes,
    ulong MonthUploadedBytes,
    ulong MonthDownloadedBytes,
    double CpuPercent,
    double MemoryPercent,
    long? LatencyMilliseconds,
    NetworkHealth NetworkHealth,
    IReadOnlyList<DailyTraffic> RecentDays,
    DateTimeOffset SampledAtUtc)
{
    public static SystemTelemetrySnapshot Empty { get; } = new(
        0, 0, 0, 0, 0, 0, 0, 0, null, NetworkHealth.Offline,
        Array.Empty<DailyTraffic>(), DateTimeOffset.UtcNow);
}

internal sealed class TrafficAccumulator
{
    readonly Dictionary<DateOnly, DailyTraffic> days;
    ulong previousUploaded;
    ulong previousDownloaded;
    DateTimeOffset? previousSample;

    public TrafficAccumulator(IEnumerable<DailyTraffic>? existing = null)
    {
        days = (existing ?? Array.Empty<DailyTraffic>())
            .GroupBy(item => item.Day)
            .ToDictionary(
                group => group.Key,
                group => new DailyTraffic(
                    group.Key,
                    SumSaturating(group.Select(item => item.UploadedBytes)),
                    SumSaturating(group.Select(item => item.DownloadedBytes))));
    }

    public (double UploadSpeed, double DownloadSpeed, ulong UploadedDelta, ulong DownloadedDelta) Update(
        DateTimeOffset sampledAtUtc,
        ulong totalUploaded,
        ulong totalDownloaded)
    {
        if (previousSample is null)
        {
            previousSample = sampledAtUtc;
            previousUploaded = totalUploaded;
            previousDownloaded = totalDownloaded;
            return (0, 0, 0, 0);
        }

        var elapsed = (sampledAtUtc - previousSample.Value).TotalSeconds;
        var uploadedDelta = totalUploaded >= previousUploaded ? totalUploaded - previousUploaded : 0;
        var downloadedDelta = totalDownloaded >= previousDownloaded ? totalDownloaded - previousDownloaded : 0;
        previousSample = sampledAtUtc;
        previousUploaded = totalUploaded;
        previousDownloaded = totalDownloaded;

        if (elapsed <= 0 || elapsed > 30)
            return (0, 0, 0, 0);

        var day = DateOnly.FromDateTime(sampledAtUtc.ToLocalTime().Date);
        var current = days.GetValueOrDefault(day, new DailyTraffic(day, 0, 0));
        days[day] = current with
        {
            UploadedBytes = AddSaturating(current.UploadedBytes, uploadedDelta),
            DownloadedBytes = AddSaturating(current.DownloadedBytes, downloadedDelta)
        };
        Trim(day.AddDays(-45));
        return (uploadedDelta / elapsed, downloadedDelta / elapsed, uploadedDelta, downloadedDelta);
    }

    public IReadOnlyList<DailyTraffic> Recent(DateOnly today, int count) =>
        Enumerable.Range(0, Math.Max(0, count))
            .Select(offset => today.AddDays(-offset))
            .Select(day => days.GetValueOrDefault(day, new DailyTraffic(day, 0, 0)))
            .OrderBy(item => item.Day)
            .ToArray();

    public DailyTraffic ForDay(DateOnly day) =>
        days.GetValueOrDefault(day, new DailyTraffic(day, 0, 0));

    public (ulong Uploaded, ulong Downloaded) ForMonth(int year, int month)
    {
        var values = days.Values.Where(item => item.Day.Year == year && item.Day.Month == month);
        return (
            SumSaturating(values.Select(item => item.UploadedBytes)),
            SumSaturating(values.Select(item => item.DownloadedBytes)));
    }

    public IReadOnlyList<DailyTraffic> Export() => days.Values.OrderBy(item => item.Day).ToArray();

    void Trim(DateOnly oldest)
    {
        foreach (var day in days.Keys.Where(day => day < oldest).ToArray())
            days.Remove(day);
    }

    static ulong AddSaturating(ulong left, ulong right) =>
        ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

    static ulong SumSaturating(IEnumerable<ulong> values)
    {
        var total = 0UL;
        foreach (var value in values) total = AddSaturating(total, value);
        return total;
    }
}

internal sealed class ProcessorUtilitySampler : IDisposable
{
    const uint PdhOk = 0;
    const uint PdhFormatDouble = 0x00000200;
    const string CounterPath = @"\Processor Information(_Total)\% Processor Utility";

    IntPtr query;
    IntPtr counter;
    bool initializationAttempted;

    public bool TryRead(out double utility)
    {
        utility = 0;
        if (!initializationAttempted)
        {
            initializationAttempted = true;
            if (!TryInitialize()) return false;
            return false;
        }
        if (query == IntPtr.Zero || PdhCollectQueryData(query) != PdhOk) return false;
        if (PdhGetFormattedCounterValue(
                counter,
                PdhFormatDouble,
                out _,
                out var formatted) != PdhOk)
            return false;

        var normalized = Normalize(formatted.Status, formatted.Value);
        if (normalized is null) return false;
        utility = normalized.Value;
        return true;
    }

    bool TryInitialize()
    {
        if (PdhOpenQueryW(null, IntPtr.Zero, out query) != PdhOk) return false;
        if (PdhAddEnglishCounterW(query, CounterPath, IntPtr.Zero, out counter) != PdhOk ||
            PdhCollectQueryData(query) != PdhOk)
        {
            Dispose();
            return false;
        }
        return true;
    }

    internal static double? Normalize(uint status, double value)
    {
        if (status is not 0 and not 1 || !double.IsFinite(value)) return null;
        return Math.Clamp(value, 0, 100);
    }

    public void Dispose()
    {
        if (query == IntPtr.Zero) return;
        PdhCloseQuery(query);
        query = IntPtr.Zero;
        counter = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct PdhFormattedCounterValue
    {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public double Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhAddEnglishCounterW(
        IntPtr query,
        string fullCounterPath,
        IntPtr userData,
        out IntPtr counter);

    [DllImport("pdh.dll")]
    static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    static extern uint PdhGetFormattedCounterValue(
        IntPtr counter,
        uint format,
        out uint counterType,
        out PdhFormattedCounterValue value);

    [DllImport("pdh.dll")]
    static extern uint PdhCloseQuery(IntPtr query);
}

public sealed class SystemTelemetryService : IDisposable
{
    readonly object gate = new();
    readonly string storagePath;
    readonly System.Threading.Timer timer;
    readonly TrafficAccumulator traffic;
    readonly ProcessorUtilitySampler processorUtility = new();
    SystemTelemetrySnapshot current = SystemTelemetrySnapshot.Empty;
    CpuTimes? previousCpuTimes;
    long? latencyMilliseconds;
    DateTimeOffset nextLatencyCheck = DateTimeOffset.MinValue;
    DateTimeOffset nextSave = DateTimeOffset.MinValue;
    int sampling;
    bool disposed;

    public SystemTelemetryService()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ChronoIsle");
        Directory.CreateDirectory(directory);
        storagePath = Path.Combine(directory, "traffic-history.json");
        traffic = new TrafficAccumulator(LoadTraffic(storagePath));
        timer = new System.Threading.Timer(_ => _ = SampleAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public event Action<SystemTelemetrySnapshot>? SnapshotChanged;
    public SystemTelemetrySnapshot Current => Volatile.Read(ref current);

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        timer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    async Task SampleAsync()
    {
        if (disposed || Interlocked.Exchange(ref sampling, 1) != 0) return;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var (uploaded, downloaded, hasNetwork) = ReadNetworkTotals();
            var delta = traffic.Update(now, uploaded, downloaded);
            if (now >= nextLatencyCheck)
            {
                nextLatencyCheck = now.AddSeconds(5.5);
                latencyMilliseconds = await MeasureLatencyAsync();
            }

            var localNow = now.ToLocalTime();
            var day = DateOnly.FromDateTime(localNow.Date);
            var today = traffic.ForDay(day);
            var month = traffic.ForMonth(localNow.Year, localNow.Month);
            var snapshot = new SystemTelemetrySnapshot(
                delta.UploadSpeed,
                delta.DownloadSpeed,
                today.UploadedBytes,
                today.DownloadedBytes,
                month.Uploaded,
                month.Downloaded,
                ReadCpuPercent(),
                ReadMemoryPercent(),
                latencyMilliseconds,
                ClassifyNetwork(hasNetwork, latencyMilliseconds, delta.UploadSpeed + delta.DownloadSpeed),
                traffic.Recent(day, 7),
                now);
            Volatile.Write(ref current, snapshot);
            SnapshotChanged?.Invoke(snapshot);

            if (now >= nextSave)
            {
                nextSave = now.AddSeconds(10);
                SaveTraffic();
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"System telemetry sample failed: {exception.Message}");
        }
        finally
        {
            Volatile.Write(ref sampling, 0);
        }
    }

    static (ulong Uploaded, ulong Downloaded, bool HasNetwork) ReadNetworkTotals()
    {
        var uploaded = 0UL;
        var downloaded = 0UL;
        var hasNetwork = false;
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up ||
                adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;
            try
            {
                var stats = adapter.GetIPv4Statistics();
                uploaded = AddSaturating(uploaded, (ulong)Math.Max(0, stats.BytesSent));
                downloaded = AddSaturating(downloaded, (ulong)Math.Max(0, stats.BytesReceived));
                hasNetwork = true;
            }
            catch { }
        }
        return (uploaded, downloaded, hasNetwork);
    }

    static NetworkHealth ClassifyNetwork(bool hasNetwork, long? latency, double trafficBytesPerSecond)
    {
        if (!hasNetwork) return NetworkHealth.Offline;
        if (latency is null) return trafficBytesPerSecond >= 128 * 1024 ? NetworkHealth.Busy : NetworkHealth.Poor;
        return latency < 150 ? NetworkHealth.Connected : NetworkHealth.Poor;
    }

    static async Task<long?> MeasureLatencyAsync()
    {
        try
        {
            using var client = new TcpClient();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
            await client.ConnectAsync("223.5.5.5", 53, timeout.Token);
            return stopwatch.ElapsedMilliseconds;
        }
        catch { return null; }
    }

    double ReadCpuPercent()
    {
        if (processorUtility.TryRead(out var utility)) return utility;
        return ReadProcessorTimePercent();
    }

    double ReadProcessorTimePercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        var currentTimes = new CpuTimes(ToUInt64(idle), ToUInt64(kernel), ToUInt64(user));
        var previous = previousCpuTimes;
        previousCpuTimes = currentTimes;
        if (previous is null) return 0;
        var idleDelta = currentTimes.Idle - previous.Value.Idle;
        var totalDelta = (currentTimes.Kernel - previous.Value.Kernel) + (currentTimes.User - previous.Value.User);
        if (totalDelta == 0 || idleDelta > totalDelta) return 0;
        return Math.Clamp((totalDelta - idleDelta) * 100d / totalDelta, 0, 100);
    }

    static double ReadMemoryPercent()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? status.MemoryLoad : 0;
    }

    void SaveTraffic()
    {
        try
        {
            lock (gate)
                File.WriteAllText(storagePath, JsonSerializer.Serialize(traffic.Export()));
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Traffic history save failed: {exception.Message}");
        }
    }

    static IReadOnlyList<DailyTraffic> LoadTraffic(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<List<DailyTraffic>>(File.ReadAllText(path)) ?? [];
        }
        catch { return []; }
    }

    static ulong AddSaturating(ulong left, ulong right) =>
        ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        timer.Dispose();
        processorUtility.Dispose();
        SaveTraffic();
    }

    readonly record struct CpuTimes(ulong Idle, ulong Kernel, ulong User);

    [StructLayout(LayoutKind.Sequential)]
    struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    static ulong ToUInt64(FileTime value) => ((ulong)value.High << 32) | value.Low;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}

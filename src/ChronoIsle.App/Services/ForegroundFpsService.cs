using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace ChronoIsle.App.Services;

public readonly record struct ForegroundFrameSample(DateTimeOffset ObservedAtUtc);

public sealed record ForegroundFpsSnapshot(
    int? ProcessId,
    double? FramesPerSecond);

public static class ForegroundFrameRate
{
    public static double? Calculate(IEnumerable<ForegroundFrameSample> samples, DateTimeOffset now)
    {
        var ordered = samples.OrderBy(sample => sample.ObservedAtUtc).ToArray();
        if (ordered.Length < 2) return null;

        var latest = ordered[^1].ObservedAtUtc;
        if (now - latest > TimeSpan.FromSeconds(2)) return null;

        var window = ordered.Where(sample => sample.ObservedAtUtc >= latest.AddSeconds(-1)).ToArray();
        var duration = latest - window[0].ObservedAtUtc;
        return duration > TimeSpan.Zero
            ? (window.Length - 1) / duration.TotalSeconds
            : null;
    }
}

internal static class ForegroundApplicationProcessTree
{
    const uint Th32csSnapProcess = 0x00000002;
    static readonly IntPtr InvalidHandleValue = new(-1);

    public static HashSet<int> GetProcessIds(int rootProcessId)
    {
        var processIds = new HashSet<int> { rootProcessId };
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == InvalidHandleValue) return processIds;

        try
        {
            var childrenByParent = new Dictionary<int, List<int>>();
            var entry = new ProcessEntry32 { DwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry)) return processIds;
            do
            {
                var parentId = unchecked((int)entry.Th32ParentProcessID);
                var processId = unchecked((int)entry.Th32ProcessID);
                if (!childrenByParent.TryGetValue(parentId, out var children))
                    childrenByParent[parentId] = children = [];
                children.Add(processId);
            }
            while (Process32Next(snapshot, ref entry));

            var pending = new Queue<int>(processIds);
            while (pending.TryDequeue(out var parentId))
                if (childrenByParent.TryGetValue(parentId, out var children))
                    foreach (var childId in children)
                        if (processIds.Add(childId)) pending.Enqueue(childId);
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return processIds;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry32
    {
        public uint DwSize;
        public uint CntUsage;
        public uint Th32ProcessID;
        public IntPtr Th32DefaultHeapID;
        public uint Th32ModuleID;
        public uint CntThreads;
        public uint Th32ParentProcessID;
        public int PcPriClassBase;
        public uint DwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(IntPtr handle);
}

public sealed class ForegroundFpsService : IDisposable
{
    const string DxgKrnlProvider = "Microsoft-Windows-DxgKrnl";
    const ulong DxgKrnlPresentKeyword = 0x0000000008000000;
    const int DxgKrnlPresentTask = 107;
    const int DxgKrnlPresentEventId = 184;

    readonly object gate = new();
    readonly System.Threading.Timer focusTimer;
    readonly List<ForegroundFrameSample> frameSamples = [];
    static readonly TimeSpan FrameHistory = TimeSpan.FromSeconds(3);
    TraceEventSession? traceSession;
    Task? tracePump;
    HashSet<int> trackedProcessIds = [];
    int? capturedProcessId;
    int sampling;
    int started;
    bool disposed;
    ForegroundFpsSnapshot current = new(null, null);

    public ForegroundFpsService()
    {
        focusTimer = new System.Threading.Timer(_ => SampleForeground(), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public event Action<ForegroundFpsSnapshot>? SnapshotChanged;
    public ForegroundFpsSnapshot Current => Volatile.Read(ref current);

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Volatile.Write(ref started, 1);
        StopLegacyPresentMonSessions();
        StartTraceCapture();
        focusTimer.Change(TimeSpan.Zero, TimeSpan.FromMilliseconds(250));
    }

    public void Stop()
    {
        if (disposed) return;
        Volatile.Write(ref started, 0);
        focusTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        StopTraceCapture();
        ClearTrackedApplication();
        Publish(null, null);
    }

    void StartTraceCapture()
    {
        lock (gate)
            if (tracePump is not null) return;

        tracePump = Task.Run(() =>
        {
            TraceEventSession? session = null;
            try
            {
                session = new TraceEventSession($"ChronoIsleFps{Environment.ProcessId}")
                {
                    StopOnDispose = true
                };
                session.Source.Dynamic.All += OnTraceEvent;
                session.EnableProvider(DxgKrnlProvider, TraceEventLevel.Always, DxgKrnlPresentKeyword);
                lock (gate)
                {
                    if (disposed || Volatile.Read(ref started) == 0)
                    {
                        session.Dispose();
                        return;
                    }
                    traceSession = session;
                }
                session.Source.Process();
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Foreground FPS ETW capture failed: {exception.Message}");
            }
            finally
            {
                session?.Dispose();
                lock (gate)
                {
                    if (ReferenceEquals(traceSession, session)) traceSession = null;
                    tracePump = null;
                }
            }
        });
    }

    static void StopLegacyPresentMonSessions()
    {
        foreach (var sessionName in GetLegacyPresentMonSessionNames(TraceEventSession.GetActiveSessionNames()))
            try
            {
                using var session = new TraceEventSession(sessionName)
                {
                    StopOnDispose = true
                };
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Foreground FPS legacy session cleanup failed: {exception.Message}");
            }
    }

    static IEnumerable<string> GetLegacyPresentMonSessionNames(IEnumerable<string> sessionNames) =>
        sessionNames.Where(name => name.StartsWith("ChronoIsleFps-", StringComparison.Ordinal));

    void StopTraceCapture()
    {
        TraceEventSession? session;
        lock (gate)
        {
            session = traceSession;
            traceSession = null;
        }
        session?.Dispose();
    }

    void SampleForeground()
    {
        if (disposed || Volatile.Read(ref started) == 0 || Interlocked.Exchange(ref sampling, 1) != 0) return;
        try
        {
            var window = GetForegroundWindow();
            GetWindowThreadProcessId(window, out var foregroundProcessId);
            if (foregroundProcessId == 0 || foregroundProcessId == Environment.ProcessId)
            {
                ClearTrackedApplication();
                Publish(null, null);
                return;
            }

            var processId = unchecked((int)foregroundProcessId);
            TrackForegroundApplication(processId);
            Publish(processId, FramesPerSecond());
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Foreground FPS sample failed: {exception.Message}");
            Publish(capturedProcessId, null);
        }
        finally
        {
            Volatile.Write(ref sampling, 0);
        }
    }

    void TrackForegroundApplication(int processId)
    {
        var processIds = ForegroundApplicationProcessTree.GetProcessIds(processId);
        lock (gate)
        {
            if (capturedProcessId != processId && !trackedProcessIds.Overlaps(processIds))
            {
                capturedProcessId = processId;
                frameSamples.Clear();
            }
            else if (capturedProcessId is null)
                capturedProcessId = processId;
            trackedProcessIds = processIds;
        }
    }

    void ClearTrackedApplication()
    {
        lock (gate)
        {
            capturedProcessId = null;
            trackedProcessIds = [];
            frameSamples.Clear();
        }
    }

    void OnTraceEvent(TraceEvent traceEvent)
    {
        if (!string.Equals(traceEvent.ProviderName, DxgKrnlProvider, StringComparison.Ordinal) ||
            ((int)traceEvent.Task != DxgKrnlPresentTask && (int)traceEvent.ID != DxgKrnlPresentEventId)) return;

        lock (gate)
        {
            if (!trackedProcessIds.Contains(traceEvent.ProcessID)) return;
            var timestamp = new DateTimeOffset(traceEvent.TimeStamp).ToUniversalTime();
            frameSamples.Add(new ForegroundFrameSample(timestamp));
            TrimSamples(timestamp);
        }
    }

    double? FramesPerSecond()
    {
        lock (gate)
        {
            return ForegroundFrameRate.Calculate(frameSamples, DateTimeOffset.UtcNow);
        }
    }

    void TrimSamples(DateTimeOffset now) =>
        frameSamples.RemoveAll(sample => sample.ObservedAtUtc < now - FrameHistory);

    void Publish(int? processId, double? framesPerSecond)
    {
        var snapshot = new ForegroundFpsSnapshot(processId, framesPerSecond);
        Volatile.Write(ref current, snapshot);
        SnapshotChanged?.Invoke(snapshot);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Volatile.Write(ref started, 0);
        focusTimer.Dispose();
        StopTraceCapture();
        SnapshotChanged = null;
    }

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}

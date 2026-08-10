using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace ChronoIsle.App.Services;

public readonly record struct PresentMonFrameSample(
    DateTimeOffset ObservedAtUtc,
    double MillisecondsBetweenPresents);

public sealed record ForegroundFpsSnapshot(
    int? ProcessId,
    double? FramesPerSecond);

public sealed class PresentMonOutputParser
{
    int intervalColumn = -1;

    public double? TryReadMillisecondsBetweenPresents(string line)
    {
        var values = line.Split(',');
        if (intervalColumn < 0)
        {
            intervalColumn = Array.FindIndex(values, value =>
                string.Equals(value, "MsBetweenPresents", StringComparison.Ordinal));
            return null;
        }

        return intervalColumn < values.Length &&
               double.TryParse(values[intervalColumn], NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds) &&
               milliseconds > 0 && double.IsFinite(milliseconds)
            ? milliseconds
            : null;
    }
}

public static class PresentMonFrameRate
{
    public static double? Calculate(IEnumerable<PresentMonFrameSample> samples, DateTimeOffset now)
    {
        var recent = samples.Where(sample =>
            sample.ObservedAtUtc >= now.AddSeconds(-1) &&
            sample.MillisecondsBetweenPresents > 0 &&
            double.IsFinite(sample.MillisecondsBetweenPresents)).ToArray();
        return recent.Length == 0
            ? null
            : 1000d / recent.Average(sample => sample.MillisecondsBetweenPresents);
    }
}

public sealed class ForegroundFpsService : IDisposable
{
    readonly object gate = new();
    readonly System.Threading.Timer focusTimer;
    readonly List<PresentMonFrameSample> frameSamples = [];
    Process? capture;
    int? capturedProcessId;
    int captureVersion;
    int sampling;
    int started;
    bool disposed;
    ForegroundFpsSnapshot current = new(null, null);

    public ForegroundFpsService()
    {
        focusTimer = new System.Threading.Timer(_ => _ = SampleForegroundAsync(), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public event Action<ForegroundFpsSnapshot>? SnapshotChanged;
    public ForegroundFpsSnapshot Current => Volatile.Read(ref current);

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Volatile.Write(ref started, 1);
        focusTimer.Change(TimeSpan.Zero, TimeSpan.FromMilliseconds(250));
    }

    public void Stop()
    {
        if (disposed) return;
        Volatile.Write(ref started, 0);
        focusTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        StopCapture();
        Publish(null, null);
    }

    async Task SampleForegroundAsync()
    {
        if (disposed || Volatile.Read(ref started) == 0 || Interlocked.Exchange(ref sampling, 1) != 0) return;
        try
        {
            var hwnd = GetForegroundWindow();
            GetWindowThreadProcessId(hwnd, out var foregroundProcessId);
            if (foregroundProcessId == 0 || foregroundProcessId == Environment.ProcessId)
            {
                if (capturedProcessId is not null) StopCapture();
                Publish(null, null);
                return;
            }

            var processId = unchecked((int)foregroundProcessId);
            if (Volatile.Read(ref started) != 0 && capturedProcessId != processId)
                await StartCaptureAsync(processId);

            Publish(capturedProcessId, FramesPerSecond());
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

    Task StartCaptureAsync(int processId)
    {
        StopCapture();
        var executable = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "PresentMon",
            "PresentMon-2.4.0-x64.exe");
        if (!File.Exists(executable))
        {
            Publish(processId, null);
            return Task.CompletedTask;
        }

        try
        {
            var parser = new PresentMonOutputParser();
            var version = Interlocked.Increment(ref captureVersion);
            var process = new Process
            {
                StartInfo = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                },
                EnableRaisingEvents = true
            };
            process.StartInfo.ArgumentList.Add("--process_id");
            process.StartInfo.ArgumentList.Add(processId.ToString(CultureInfo.InvariantCulture));
            process.StartInfo.ArgumentList.Add("--output_stdout");
            process.StartInfo.ArgumentList.Add("--no_csv");
            process.StartInfo.ArgumentList.Add("--no_console_stats");
            process.StartInfo.ArgumentList.Add("--session_name");
            process.StartInfo.ArgumentList.Add($"ChronoIsleFps-{processId}");
            process.StartInfo.ArgumentList.Add("--stop_existing_session");
            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data is not null &&
                    version == Volatile.Read(ref captureVersion) &&
                    parser.TryReadMillisecondsBetweenPresents(args.Data) is { } milliseconds)
                    AddSample(new PresentMonFrameSample(DateTimeOffset.UtcNow, milliseconds));
            };
            process.Exited += (_, _) =>
            {
                if (version == Volatile.Read(ref captureVersion))
                    Publish(processId, null);
            };
            if (!process.Start())
            {
                process.Dispose();
                Publish(processId, null);
                return Task.CompletedTask;
            }

            lock (gate)
            {
                capture = process;
                capturedProcessId = processId;
                frameSamples.Clear();
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Foreground FPS capture failed: {exception.Message}");
            Publish(processId, null);
        }

        return Task.CompletedTask;
    }

    void AddSample(PresentMonFrameSample sample)
    {
        lock (gate)
        {
            frameSamples.Add(sample);
            TrimSamples(sample.ObservedAtUtc);
        }
    }

    double? FramesPerSecond()
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            TrimSamples(now);
            return PresentMonFrameRate.Calculate(frameSamples, now);
        }
    }

    void TrimSamples(DateTimeOffset now) =>
        frameSamples.RemoveAll(sample => sample.ObservedAtUtc < now.AddSeconds(-1));

    void StopCapture()
    {
        Process? process;
        lock (gate)
        {
            Interlocked.Increment(ref captureVersion);
            process = capture;
            capture = null;
            capturedProcessId = null;
            frameSamples.Clear();
        }
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Foreground FPS capture stop failed: {exception.Message}");
        }
        finally
        {
            process.Dispose();
        }
    }

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
        StopCapture();
        SnapshotChanged = null;
    }

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}

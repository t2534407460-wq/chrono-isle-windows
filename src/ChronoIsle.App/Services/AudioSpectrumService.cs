using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Dsp;
using NAudio.Dmo;
using NAudio.Wave;

namespace ChronoIsle.App.Services.Media;

internal static class AudioSpectrumAnalyzer
{
    internal const int SampleCount = 2048;
    static readonly (double Low, double High)[] Bands =
    [
        (50, 140),
        (140, 300),
        (300, 650),
        (650, 1300),
        (1300, 2600),
        (2600, 5200),
        (5200, 10000)
    ];

    public static double[] Analyze(ReadOnlySpan<float> samples, int sampleRate)
    {
        if (samples.Length < SampleCount || sampleRate <= 0) return new double[Bands.Length];
        var spectrum = new Complex[SampleCount];
        for (var index = 0; index < SampleCount; index++)
        {
            spectrum[index].X = (float)(samples[index] * FastFourierTransform.HammingWindow(index, SampleCount));
            spectrum[index].Y = 0;
        }
        FastFourierTransform.FFT(true, 11, spectrum);

        var result = new double[Bands.Length];
        for (var bandIndex = 0; bandIndex < Bands.Length; bandIndex++)
        {
            var band = Bands[bandIndex];
            var start = Math.Max(1, (int)Math.Floor(band.Low * SampleCount / sampleRate));
            var end = Math.Min(SampleCount / 2 - 1, (int)Math.Ceiling(band.High * SampleCount / sampleRate));
            double energy = 0;
            for (var bin = start; bin <= end; bin++)
                energy += spectrum[bin].X * spectrum[bin].X + spectrum[bin].Y * spectrum[bin].Y;
            var rms = Math.Sqrt(energy / Math.Max(1, end - start + 1));
            result[bandIndex] = Math.Clamp(Math.Log10(1 + rms * 18) / 2.2, 0, 1);
        }
        return result;
    }
}

internal readonly record struct ArtworkPalette(
    byte FirstRed,
    byte FirstGreen,
    byte FirstBlue,
    byte SecondRed,
    byte SecondGreen,
    byte SecondBlue)
{
    public static ArtworkPalette Neutral => new(112, 112, 118, 242, 242, 247);
}

internal static class ArtworkPaletteExtractor
{
    public static ArtworkPalette Extract(byte[]? artworkBytes)
    {
        if (artworkBytes is null || artworkBytes.Length == 0) return ArtworkPalette.Neutral;
        try
        {
            using var stream = new MemoryStream(artworkBytes, writable: false);
            using var bitmap = new System.Drawing.Bitmap(stream);
            var buckets = new Dictionary<int, ColorBucket>();
            var stepX = Math.Max(1, bitmap.Width / 28);
            var stepY = Math.Max(1, bitmap.Height / 28);
            for (var y = stepY / 2; y < bitmap.Height; y += stepY)
            for (var x = stepX / 2; x < bitmap.Width; x += stepX)
            {
                var color = bitmap.GetPixel(x, y);
                if (color.A < 96) continue;
                var key = (color.R >> 5) << 6 | (color.G >> 5) << 3 | color.B >> 5;
                buckets.TryGetValue(key, out var bucket);
                buckets[key] = bucket.Add(color.R, color.G, color.B);
            }
            if (buckets.Count == 0) return ArtworkPalette.Neutral;

            var colors = buckets.Values
                .Select(bucket => bucket.Average())
                .OrderByDescending(color => color.Count)
                .ToArray();
            var first = colors[0];
            var second = colors.Length == 1
                ? CreateTonalPartner(first)
                : colors.Skip(1).MaxBy(color =>
                    color.Count * (0.28 + ColorDistanceSquared(first, color) / (3d * 255 * 255) * 3.2));
            if (ColorDistanceSquared(first, second) < 28 * 28)
                second = CreateTonalPartner(first);
            if (Luminance(first) > Luminance(second)) (first, second) = (second, first);
            return new ArtworkPalette(first.Red, first.Green, first.Blue, second.Red, second.Green, second.Blue);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Artwork palette extraction failed: {exception.Message}");
            return ArtworkPalette.Neutral;
        }
    }

    static PaletteColor CreateTonalPartner(PaletteColor color)
    {
        var luminance = Luminance(color);
        var factor = luminance < 128 ? 1.85 : 0.42;
        return new PaletteColor(
            (byte)Math.Clamp((int)Math.Round(color.Red * factor + (factor > 1 ? 24 : 0)), 0, 255),
            (byte)Math.Clamp((int)Math.Round(color.Green * factor + (factor > 1 ? 24 : 0)), 0, 255),
            (byte)Math.Clamp((int)Math.Round(color.Blue * factor + (factor > 1 ? 24 : 0)), 0, 255),
            color.Count);
    }

    static double ColorDistanceSquared(PaletteColor left, PaletteColor right)
    {
        var red = left.Red - right.Red;
        var green = left.Green - right.Green;
        var blue = left.Blue - right.Blue;
        return red * red + green * green + blue * blue;
    }

    static double Luminance(PaletteColor color) =>
        color.Red * 0.2126 + color.Green * 0.7152 + color.Blue * 0.0722;

    readonly record struct PaletteColor(byte Red, byte Green, byte Blue, int Count);

    readonly record struct ColorBucket(int Count, long Red, long Green, long Blue)
    {
        public ColorBucket Add(byte red, byte green, byte blue) =>
            new(Count + 1, Red + red, Green + green, Blue + blue);

        public PaletteColor Average() => new(
            (byte)(Red / Count),
            (byte)(Green / Count),
            (byte)(Blue / Count),
            Count);
    }
}

public sealed class AudioSpectrumService : IDisposable
{
    static readonly string[] MusicProcessNames = ["cloudmusic", "QQMusic", "Mineradio"];
    readonly object captureGate = new();
    readonly object spectrumGate = new();
    readonly float[] samples = new float[AudioSpectrumAnalyzer.SampleCount];
    readonly double[] smoothed = new double[7];
    readonly System.Threading.Timer silenceTimer;
    WasapiLoopbackCapture? capture;
    MMDevice? captureDevice;
    string? captureDeviceId;
    int sampleCount;
    long lastCapturedAt;
    long lastPublishedAt;
    long lastDeviceProbeAt;
    bool silenceSettled = true;
    bool disposed;

    public AudioSpectrumService() =>
        silenceTimer = new System.Threading.Timer(_ => PublishSilenceIfNeeded(), null, Timeout.Infinite, Timeout.Infinite);

    public event Action<double[]>? SpectrumChanged;

    public void Start()
    {
        if (disposed) return;
        RefreshCaptureDevice(force: true);
        silenceTimer.Change(33, 33);
    }

    public void RefreshCaptureDevice() => RefreshCaptureDevice(force: false);

    void RefreshCaptureDevice(bool force)
    {
        if (disposed) return;
        var now = Environment.TickCount64;
        if (!force && now - Interlocked.Read(ref lastDeviceProbeAt) < 2000) return;
        Interlocked.Exchange(ref lastDeviceProbeAt, now);
        var preferredDeviceId = FindPreferredRenderDeviceId();
        lock (captureGate)
        {
            if (disposed || !force && capture is not null &&
                string.Equals(captureDeviceId, preferredDeviceId, StringComparison.Ordinal))
                return;
            DisposeCaptureUnsafe();
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                captureDevice = string.IsNullOrWhiteSpace(preferredDeviceId)
                    ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                    : enumerator.GetDevice(preferredDeviceId);
                captureDeviceId = captureDevice.ID;
                capture = new WasapiLoopbackCapture(captureDevice);
                capture.DataAvailable += Capture_DataAvailable;
                capture.RecordingStopped += Capture_RecordingStopped;
                sampleCount = 0;
                Interlocked.Exchange(ref lastCapturedAt, Environment.TickCount64);
                capture.StartRecording();
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Audio spectrum unavailable: {exception.Message}");
                DisposeCaptureUnsafe();
            }
        }
    }

    static string? FindPreferredRenderDeviceId()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var bestId = defaultDevice.ID;
            var bestScore = 1d;
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            for (var deviceIndex = 0; deviceIndex < devices.Count; deviceIndex++)
            {
                using var device = devices[deviceIndex];
                var score = string.Equals(device.ID, defaultDevice.ID, StringComparison.Ordinal) ? 1d : 0d;
                try
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    for (var sessionIndex = 0; sessionIndex < sessions.Count; sessionIndex++)
                    {
                        using var session = sessions[sessionIndex];
                        if (session.State != AudioSessionState.AudioSessionStateActive) continue;
                        var peak = session.AudioMeterInformation.MasterPeakValue;
                        score = Math.Max(score, peak * 10);
                        var processId = session.GetProcessID;
                        if (processId == 0) continue;
                        try
                        {
                            using var process = System.Diagnostics.Process.GetProcessById(checked((int)processId));
                            if (MusicProcessNames.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase))
                                score = Math.Max(score, 100 + peak * 1000);
                        }
                        catch { }
                    }
                }
                catch { }
                if (score <= bestScore) continue;
                bestScore = score;
                bestId = device.ID;
            }
            return bestId;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Audio output selection failed: {exception.Message}");
            return null;
        }
    }

    void Capture_DataAvailable(object? sender, WaveInEventArgs e)
    {
        var activeCapture = capture;
        if (disposed || activeCapture is null || e.BytesRecorded <= 0) return;
        Interlocked.Exchange(ref lastCapturedAt, Environment.TickCount64);
        var format = activeCapture.WaveFormat;
        var bytesPerSample = Math.Max(1, format.BitsPerSample / 8);
        var frameSize = Math.Max(bytesPerSample, format.BlockAlign);
        for (var offset = 0; offset + frameSize <= e.BytesRecorded; offset += frameSize)
        {
            double mono = 0;
            for (var channel = 0; channel < Math.Max(1, format.Channels); channel++)
                mono += ReadSample(e.Buffer, offset + channel * bytesPerSample, format);
            samples[sampleCount++] = (float)(mono / Math.Max(1, format.Channels));
            if (sampleCount < samples.Length) continue;
            lock (spectrumGate)
                PublishSpectrum(AudioSpectrumAnalyzer.Analyze(samples, format.SampleRate));
            sampleCount = 0;
        }
    }

    void PublishSilenceIfNeeded()
    {
        if (disposed || Environment.TickCount64 - Interlocked.Read(ref lastCapturedAt) < 110) return;
        lock (spectrumGate) PublishSpectrum(new double[smoothed.Length]);
    }

    void PublishSpectrum(double[] spectrum)
    {
        var now = Environment.TickCount64;
        var hasSignal = spectrum.Any(value => value > 0.001);
        if (!hasSignal && silenceSettled) return;
        if (hasSignal) silenceSettled = false;
        for (var index = 0; index < smoothed.Length; index++)
        {
            var target = spectrum[index];
            var factor = target >= smoothed[index] ? 0.76 : 0.34;
            smoothed[index] += (target - smoothed[index]) * factor;
        }
        if (now - lastPublishedAt < 32) return;
        if (!hasSignal && smoothed.All(value => value <= 0.002))
        {
            Array.Clear(smoothed);
            silenceSettled = true;
        }
        lastPublishedAt = now;
        SpectrumChanged?.Invoke((double[])smoothed.Clone());
    }

    internal static float ReadSample(byte[] buffer, int offset, WaveFormat format)
    {
        var isIeeeFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
                          format is WaveFormatExtensible extensible &&
                          extensible.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT;
        if (isIeeeFloat && format.BitsPerSample == 32)
            return BitConverter.ToSingle(buffer, offset);
        if (format.BitsPerSample == 16)
            return BitConverter.ToInt16(buffer, offset) / 32768f;
        if (format.BitsPerSample == 24)
        {
            var value = buffer[offset] | buffer[offset + 1] << 8 | buffer[offset + 2] << 16;
            if ((value & 0x800000) != 0) value |= unchecked((int)0xff000000);
            return value / 8388608f;
        }
        if (format.BitsPerSample == 32)
            return BitConverter.ToInt32(buffer, offset) / 2147483648f;
        return 0;
    }

    void Capture_RecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
            System.Diagnostics.Debug.WriteLine($"Audio spectrum stopped: {e.Exception.Message}");
    }

    void DisposeCaptureUnsafe()
    {
        if (capture is not null)
        {
            capture.DataAvailable -= Capture_DataAvailable;
            capture.RecordingStopped -= Capture_RecordingStopped;
            try { capture.StopRecording(); } catch { }
            capture.Dispose();
            capture = null;
        }
        captureDevice?.Dispose();
        captureDevice = null;
        captureDeviceId = null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        silenceTimer.Dispose();
        lock (captureGate) DisposeCaptureUnsafe();
    }
}
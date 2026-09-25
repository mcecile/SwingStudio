using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SwingStudio.Capture;

public sealed class MicrophoneLevelMeter : IDisposable
{
    private readonly object _gate = new();
    private WasapiRecorder? _recorder;
    private MMDevice? _device;
    private MMDeviceEnumerator? _enumerator;
    private Action<double>? _onLevel;
    private Action<byte[], WaveFormat>? _onAudio;
    private WaveFormat? _format;
    private int _generation;
    private float _pendingPeak;
    private long _nextReport;

    public string? DeviceId { get; private set; }

    public void Start(string deviceId, Action<double> onLevel, Action<byte[], WaveFormat>? onAudio = null)
    {
        Stop();
        _onLevel = onLevel;
        _onAudio = onAudio;
        var generation = Interlocked.Increment(ref _generation);
        MMDeviceEnumerator? enumerator = null;
        MMDevice? device = null;
        WasapiRecorder? recorder = null;
        try
        {
            enumerator = new MMDeviceEnumerator();
            device = enumerator.GetDevice(deviceId);
            var raw = true;
            try
            {
                recorder = StartRecorder(device, generation, raw);
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException)
            {
                Log.Warn($"Microphone {device.FriendlyName} does not support a raw stream, so Windows audio enhancements stay on: {ex.Message}");
                raw = false;
                recorder = StartRecorder(device, generation, raw);
            }

            lock (_gate)
            {
                if (generation != _generation)
                {
                    recorder.Dispose();
                    device.Dispose();
                    enumerator.Dispose();
                    return;
                }

                _format = recorder.WaveFormat;
                Log.Info($"Microphone started: {device.FriendlyName}, {_format.SampleRate} Hz, {_format.Channels} channel(s), {_format.BitsPerSample}-bit {_format.Encoding}, {(raw ? "raw" : "processed")}.");
                _enumerator = enumerator;
                _device = device;
                _recorder = recorder;
                DeviceId = deviceId;
            }
        }
        catch
        {
            recorder?.Dispose();
            device?.Dispose();
            enumerator?.Dispose();
            throw;
        }
    }

    public void Stop()
    {
        WasapiRecorder? recorder;
        MMDevice? device;
        MMDeviceEnumerator? enumerator;
        lock (_gate)
        {
            Interlocked.Increment(ref _generation);
            recorder = _recorder;
            device = _device;
            enumerator = _enumerator;
            _recorder = null;
            _device = null;
            _enumerator = null;
            _format = null;
            DeviceId = null;
            _onAudio = null;
            _pendingPeak = 0;
            _nextReport = 0;
        }

        recorder?.Dispose();
        device?.Dispose();
        enumerator?.Dispose();
    }

    public void Dispose() => Stop();

    private WasapiRecorder StartRecorder(MMDevice device, int generation, bool raw)
    {
        var builder = new WasapiRecorderBuilder().WithDevice(device).WithSharedMode();
        if (raw)
        {
            builder = builder.WithRawMode();
        }

        var recorder = builder.Build();
        try
        {
            recorder.DataAvailable += (buffer, _, _, _) => OnData(generation, buffer);
            recorder.StartRecording();
            return recorder;
        }
        catch
        {
            recorder.Dispose();
            throw;
        }
    }

    public static double PeakLevel(ReadOnlySpan<byte> buffer, WaveFormat format)
    {
        if (buffer.IsEmpty || format.BlockAlign <= 0 || format.SampleRate <= 0)
        {
            return 0;
        }

        var windowBytes = format.BlockAlign * Math.Max(1, (int)(format.SampleRate * 0.05));
        if (windowBytes > buffer.Length)
        {
            windowBytes = buffer.Length - (buffer.Length % format.BlockAlign);
        }
        else
        {
            windowBytes -= windowBytes % format.BlockAlign;
        }

        if (windowBytes <= 0)
        {
            return 0;
        }

        var start = buffer.Length - windowBytes;
        var peak = 0f;
        var encoding = format.Encoding;
        if (encoding == WaveFormatEncoding.Extensible && format.BitsPerSample == 32)
        {
            encoding = WaveFormatEncoding.IeeeFloat;
        }

        if (encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            for (var offset = start; offset + 4 <= buffer.Length; offset += 4)
            {
                peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(buffer[offset..])));
            }
        }
        else if (encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
        {
            for (var offset = start; offset + 2 <= buffer.Length; offset += 2)
            {
                peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(buffer[offset..])) / 32768f);
            }
        }
        else if (encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 32)
        {
            for (var offset = start; offset + 4 <= buffer.Length; offset += 4)
            {
                peak = Math.Max(peak, Math.Abs(BitConverter.ToInt32(buffer[offset..])) / 2147483648f);
            }
        }

        return Math.Clamp(peak, 0, 1) * 100;
    }

    private void OnData(int generation, ReadOnlySpan<byte> buffer)
    {
        if (generation != Volatile.Read(ref _generation) || buffer.IsEmpty)
        {
            return;
        }

        WaveFormat? format;
        lock (_gate)
        {
            format = _format;
        }

        if (format is null)
        {
            return;
        }

        _onAudio?.Invoke(buffer.ToArray(), format);

        var peak = (float)PeakLevel(buffer, format);
        var now = Stopwatch.GetTimestamp();
        float report;
        lock (_gate)
        {
            if (generation != _generation)
            {
                return;
            }

            _pendingPeak = Math.Max(_pendingPeak, peak);
            if (now < _nextReport)
            {
                return;
            }

            report = _pendingPeak;
            _pendingPeak = 0;
            _nextReport = now + (long)(Stopwatch.Frequency * 0.033);
        }

        _onLevel?.Invoke(report);
    }
}

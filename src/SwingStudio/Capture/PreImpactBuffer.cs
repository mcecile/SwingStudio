using NAudio.Wave;
using OpenCvSharp;

namespace SwingStudio.Capture;

public sealed class MonitorClock
{
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    public double ElapsedMilliseconds => _clock.Elapsed.TotalMilliseconds;
}

public sealed class FrameRing : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<TimedFrame> _frames = new();
    private bool _disposed;

    public void Add(double timeMs, Mat source, double windowSeconds)
    {
        var copy = source.Clone();
        lock (_gate)
        {
            if (_disposed)
            {
                copy.Dispose();
                return;
            }

            _frames.Enqueue(new TimedFrame(timeMs, copy));
            DiscardOlderThan(timeMs - windowSeconds * 1000);
        }
    }

    public double SpanMilliseconds(double nowMs, double windowMilliseconds)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return 0;
            }

            DiscardOlderThan(nowMs - windowMilliseconds);
            if (_frames.Count == 0)
            {
                return 0;
            }

            return Math.Min(windowMilliseconds, Math.Max(0, nowMs - _frames.Peek().TimeMs));
        }
    }

    public FrameSlice Slice(double fromMs, double toMs)
    {
        var slice = new FrameSlice();
        lock (_gate)
        {
            if (_disposed)
            {
                return slice;
            }

            foreach (var frame in _frames)
            {
                if (frame.TimeMs < fromMs || frame.TimeMs > toMs)
                {
                    continue;
                }

                slice.Frames.Add(new TimedFrame(frame.TimeMs, frame.Frame.Clone()));
            }
        }

        return slice;
    }

    public void Clear()
    {
        lock (_gate)
        {
            DiscardOlderThan(double.MaxValue);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            DiscardOlderThan(double.MaxValue);
        }
    }

    private void DiscardOlderThan(double cutoffMs)
    {
        while (_frames.Count > 0 && _frames.Peek().TimeMs < cutoffMs)
        {
            _frames.Dequeue().Frame.Dispose();
        }
    }

    internal readonly record struct TimedFrame(double TimeMs, Mat Frame);
}

public sealed class FrameSlice : IDisposable
{
    internal List<FrameRing.TimedFrame> Frames { get; } = [];

    public void Dispose()
    {
        foreach (var frame in Frames)
        {
            frame.Frame.Dispose();
        }

        Frames.Clear();
    }
}

public sealed class AudioRing : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<AudioPacket> _packets = new();
    private WaveFormat? _format;
    private bool _disposed;

    public void Add(double timeMs, ReadOnlySpan<byte> data, WaveFormat format, double windowSeconds)
    {
        var copy = data.ToArray();
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _format ??= format;
            _packets.Enqueue(new AudioPacket(timeMs, copy));
            var cutoff = timeMs - windowSeconds * 1000;
            while (_packets.Count > 0 && _packets.Peek().TimeMs < cutoff)
            {
                _packets.Dequeue();
            }
        }
    }

    public AudioSlice Slice(double fromMs, double toMs)
    {
        lock (_gate)
        {
            var slice = new AudioSlice { Format = _format };
            if (_disposed || _format is null)
            {
                return slice;
            }

            foreach (var packet in _packets)
            {
                var duration = packet.Data.Length / (double)_format.BlockAlign / _format.SampleRate * 1000;
                if (packet.TimeMs < toMs && packet.TimeMs + duration > fromMs)
                {
                    slice.Packets.Add(new AudioPacket(packet.TimeMs, packet.Data.ToArray()));
                }
            }

            return slice;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _packets.Clear();
            _format = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _packets.Clear();
            _format = null;
        }
    }

    internal readonly record struct AudioPacket(double TimeMs, byte[] Data);
}

public sealed class AudioSlice
{
    public WaveFormat? Format { get; init; }

    internal List<AudioRing.AudioPacket> Packets { get; } = [];
}

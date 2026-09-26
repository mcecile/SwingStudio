namespace SwingStudio.Session;

/// <summary>RBJ high-pass used by the live trigger, contact time, and calibration.</summary>
public sealed class HighPassFilter
{
    public const double CutoffHz = 2000;

    private readonly double _b0;
    private readonly double _b1;
    private readonly double _b2;
    private readonly double _a1;
    private readonly double _a2;
    private readonly double[] _x1;
    private readonly double[] _x2;
    private readonly double[] _y1;
    private readonly double[] _y2;

    public HighPassFilter(int sampleRate, int channels)
    {
        SampleRate = Math.Max(1, sampleRate);
        Channels = Math.Max(1, channels);
        var w0 = 2 * Math.PI * CutoffHz / SampleRate;
        var cos = Math.Cos(w0);
        var alpha = Math.Sin(w0) / (2 * Math.Sqrt(0.5));
        var a0 = 1 + alpha;
        _b0 = (1 + cos) / 2 / a0;
        _b1 = -(1 + cos) / a0;
        _b2 = _b0;
        _a1 = -2 * cos / a0;
        _a2 = (1 - alpha) / a0;
        _x1 = new double[Channels];
        _x2 = new double[Channels];
        _y1 = new double[Channels];
        _y2 = new double[Channels];
    }

    public int SampleRate { get; }

    public int Channels { get; }

    public static float[] Apply(float[] samples, int sampleRate)
    {
        var filter = new HighPassFilter(sampleRate, 1);
        var output = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            output[i] = filter.Process(samples[i], 0);
        }

        return output;
    }

    public float Process(float sample, int channel)
    {
        var ch = Math.Clamp(channel, 0, Channels - 1);
        double x0 = sample;
        var y0 = _b0 * x0 + _b1 * _x1[ch] + _b2 * _x2[ch] - _a1 * _y1[ch] - _a2 * _y2[ch];
        _x2[ch] = _x1[ch];
        _x1[ch] = x0;
        _y2[ch] = _y1[ch];
        _y1[ch] = y0;
        return (float)y0;
    }

    /// <summary>Filters every interleaved sample so the state stays continuous, and returns the peak of the last <paramref name="windowFrames"/> frames as 0–100.</summary>
    public float Peak(ReadOnlySpan<float> interleaved, int windowFrames)
    {
        var frames = interleaved.Length / Channels;
        if (frames <= 0)
        {
            return 0;
        }

        var start = Math.Max(0, frames - Math.Max(1, windowFrames));
        var peak = 0f;
        for (var frame = 0; frame < frames; frame++)
        {
            for (var channel = 0; channel < Channels; channel++)
            {
                var value = Math.Abs(Process(interleaved[frame * Channels + channel], channel));
                if (frame >= start)
                {
                    peak = Math.Max(peak, value);
                }
            }
        }

        return Math.Clamp(peak, 0, 1) * 100;
    }
}

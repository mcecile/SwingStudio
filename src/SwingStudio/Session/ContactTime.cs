namespace SwingStudio.Session;

public static class ContactTime
{
    public static double Resolve(
        float[] samples,
        int sampleRate,
        double audioStartMs,
        double takeLengthMs,
        double triggerMs,
        double offsetMs)
    {
        var fallback = triggerMs + offsetMs;
        if (samples.Length == 0 || sampleRate <= 0)
        {
            return Clamp(fallback, takeLengthMs);
        }

        var highPass = HighPass(samples, sampleRate, 2000);
        var highEnvelope = Envelope(highPass, sampleRate);
        var fullEnvelope = Envelope(samples, sampleRate);
        var highPeak = Peak(highEnvelope);
        var highNoise = Median(highEnvelope);
        var useHighPass = highPeak > 0.02 && highPeak > highNoise * 4;
        var envelope = useHighPass ? highEnvelope : fullEnvelope;
        var peak = useHighPass ? highPeak : Peak(fullEnvelope);
        if (peak <= 0)
        {
            return Clamp(fallback, takeLengthMs);
        }

        var onset = OnsetSample(envelope, peak);
        var contact = audioStartMs + onset * 1000d / sampleRate + offsetMs;
        return Clamp(contact, takeLengthMs);
    }

    private static double Clamp(double contactMs, double takeLengthMs) => Math.Clamp(contactMs, 0, Math.Max(0, takeLengthMs));

    private static int OnsetSample(float[] envelope, float peak)
    {
        var peakIndex = 0;
        for (var i = 1; i < envelope.Length; i++)
        {
            if (envelope[i] > envelope[peakIndex])
            {
                peakIndex = i;
            }
        }

        var threshold = peak * 0.2f;
        var onset = peakIndex;
        while (onset > 0 && envelope[onset - 1] > threshold)
        {
            onset--;
        }

        return onset;
    }

    private static float[] HighPass(float[] samples, int sampleRate, double cutoffHz)
    {
        var output = new float[samples.Length];
        var dt = 1d / sampleRate;
        var rc = 1d / (2 * Math.PI * cutoffHz);
        var a = rc / (rc + dt);
        double previousIn = 0;
        double previousOut = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            var current = a * (previousOut + samples[i] - previousIn);
            output[i] = (float)current;
            previousIn = samples[i];
            previousOut = current;
        }

        return output;
    }

    private static float[] Envelope(float[] samples, int sampleRate)
    {
        var output = new float[samples.Length];
        var alpha = 1 - Math.Exp(-1d / (sampleRate * 0.003));
        double envelope = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            envelope += alpha * (Math.Abs(samples[i]) - envelope);
            output[i] = (float)envelope;
        }

        return output;
    }

    private static float Peak(float[] samples)
    {
        var peak = 0f;
        foreach (var sample in samples)
        {
            if (sample > peak)
            {
                peak = sample;
            }
        }

        return peak;
    }

    private static float Median(float[] samples)
    {
        if (samples.Length == 0)
        {
            return 0;
        }

        var copy = samples.ToArray();
        Array.Sort(copy);
        return copy[copy.Length / 2];
    }
}

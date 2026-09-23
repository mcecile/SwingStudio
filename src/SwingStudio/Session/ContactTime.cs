namespace SwingStudio.Session;

public static class ContactTime
{
    public static double Resolve(
        float[] samples,
        int sampleRate,
        double audioStartMs,
        double takeLengthMs,
        double triggerMs,
        double offsetMs,
        double threshold)
    {
        var fallback = triggerMs + offsetMs;
        if (samples.Length == 0 || sampleRate <= 0)
        {
            return Clamp(fallback, takeLengthMs);
        }

        var triggerIndex = SampleIndex(triggerMs - audioStartMs, sampleRate, samples.Length);
        var levelThreshold = (float)(Math.Clamp(threshold, 0, 100) / 100d);
        var onset = OnsetSample(samples, sampleRate, triggerIndex, levelThreshold);
        if (onset < 0)
        {
            return Clamp(fallback, takeLengthMs);
        }

        var contact = audioStartMs + onset * 1000d / sampleRate + offsetMs;
        return Clamp(contact, takeLengthMs);
    }

    private static double Clamp(double contactMs, double takeLengthMs) => Math.Clamp(contactMs, 0, Math.Max(0, takeLengthMs));

    private static int SampleIndex(double timeMs, int sampleRate, int length)
    {
        var index = (int)Math.Round(timeMs * sampleRate / 1000d);
        return Math.Clamp(index, 0, Math.Max(0, length - 1));
    }

    private static int OnsetSample(float[] samples, int sampleRate, int triggerIndex, float levelThreshold)
    {
        var hold = Math.Max(1, (int)Math.Round(sampleRate * 0.003));
        var lookback = Math.Max(hold, (int)Math.Round(sampleRate * 0.15));
        var forward = Math.Max(1, (int)Math.Round(sampleRate * 0.02));
        var windowStart = Math.Max(0, triggerIndex - lookback);
        var first = FirstAbove(samples, windowStart, triggerIndex, levelThreshold);
        if (first < 0)
        {
            return FirstAbove(samples, triggerIndex + 1, Math.Min(samples.Length - 1, triggerIndex + forward), levelThreshold);
        }

        if (first > windowStart)
        {
            return first;
        }

        var onset = first;
        var quiet = 0;
        for (var i = first - 1; i >= 0; i--)
        {
            if (Above(samples, i, levelThreshold))
            {
                onset = i;
                quiet = 0;
                continue;
            }

            quiet++;
            if (quiet >= hold)
            {
                break;
            }
        }

        return onset;
    }

    private static int FirstAbove(float[] samples, int start, int end, float levelThreshold)
    {
        if (start < 0 || start >= samples.Length || end < start)
        {
            return -1;
        }

        var last = Math.Min(end, samples.Length - 1);
        for (var i = start; i <= last; i++)
        {
            if (Above(samples, i, levelThreshold))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool Above(float[] samples, int index, float levelThreshold)
    {
        return Math.Abs(samples[index]) >= levelThreshold;
    }
}

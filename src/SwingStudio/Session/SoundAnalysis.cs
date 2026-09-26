using System.Text.Json.Serialization;

namespace SwingStudio.Session;

public sealed class CalibrationReport
{
    public DateTimeOffset CreatedAt { get; init; }

    public bool Complete { get; set; }

    public string? Microphone { get; init; }

    public string? SourceFormat { get; init; }

    public int SampleRate { get; init; }

    public int Channels { get; init; }

    public int ThresholdAtStart { get; init; }

    public int OffsetMsAtStart { get; init; }

    public double DetectLevel { get; set; }

    public RoomStats? Room { get; set; }

    public List<SwingSound> Swings { get; } = [];

    public CalibrationAdvice? Advice { get; set; }
}

public sealed class RoomStats
{
    public string File { get; init; } = "";

    public double DurationMs { get; init; }

    public double MeterPeak { get; init; }

    public double MeterP99 { get; init; }

    public double RmsDbfs { get; init; }

    public double HighPassMeterPeak { get; init; }

    public List<RoomBand> Bands { get; init; } = [];
}

public sealed class RoomBand
{
    public string Name { get; init; } = "";

    public double MeterPeak { get; init; }

    public double MeterP99 { get; init; }

    public double RmsDbfs { get; init; }
}

public sealed class SwingSound
{
    public int Index { get; init; }

    public string File { get; init; } = "";

    public double DurationMs { get; init; }

    public double DetectedAtMs { get; init; }

    public double DetectLevel { get; init; }

    public List<SoundEvent> Events { get; init; } = [];

    [JsonIgnore]
    public SoundEvent? Strike => Events.FirstOrDefault(sound => sound.Role == SoundAnalysis.StrikeRole);
}

public sealed class SoundEvent
{
    public string Role { get; set; } = "";

    public double StartMs { get; init; }

    public double FromStrikeMs { get; set; }

    public double MeterPeak { get; init; }

    public double MonoPeak { get; init; }

    public double HighPassMeterPeak { get; init; }

    public double RiseMs { get; init; }

    public double DecayMs { get; init; }

    public double DurationMs { get; init; }

    public int ClippedSamples { get; init; }

    public string DominantBand { get; init; } = "";

    public List<EventBand> Bands { get; init; } = [];
}

public sealed class EventBand
{
    public string Name { get; init; } = "";

    public double Share { get; init; }

    public double RmsDbfs { get; init; }
}

public sealed class CalibrationAdvice
{
    public int? ProposedThreshold { get; set; }

    public double RoomMeterPeak { get; set; }

    public double QuietestStrike { get; set; }

    public double LoudestStrike { get; set; }

    public double? Margin { get; set; }

    public double HighPassRoomPeak { get; set; }

    public double HighPassQuietestStrike { get; set; }

    public double? HighPassMargin { get; set; }

    public bool HighPassBetter { get; set; }

    public double? CurrentWindowsLevel { get; set; }

    public double? SuggestedWindowsLevel { get; set; }

    public double? GainDb { get; set; }

    public string? GainAdvice { get; set; }

    public List<string> Warnings { get; } = [];

    public List<string> Notes { get; } = [];
}

public static class SoundAnalysis
{
    public const string StrikeRole = "strike";
    public const string BeforeRole = "before";
    public const string AfterRole = "after";
    public const string LoudestAfterRole = "after-loudest";
    private const double ClipLevel = 0.99;
    private const double HighPassHz = 2000;

    private static readonly (string Name, double? Low, double? High)[] BandEdges =
    [
        ("under 500 Hz", null, 500),
        ("500 Hz-2 kHz", 500, 2000),
        ("2-6 kHz", 2000, 6000),
        ("over 6 kHz", 6000, null)
    ];

    public static RoomStats Room(float[] interleaved, int channels, int sampleRate, string file)
    {
        var meter = Meter(interleaved, channels);
        var mono = Mono(interleaved, channels);
        var bands = BandEdges.Select(band =>
        {
            var filtered = Filter(mono, sampleRate, band.Low, band.High);
            var levels = filtered.Select(value => Math.Abs(value) * 100f).ToArray();
            return new RoomBand
            {
                Name = band.Name,
                MeterPeak = Round(Max(levels)),
                MeterP99 = Round(Percentile(levels, 0.99)),
                RmsDbfs = Round(RmsDbfs(filtered, 0, filtered.Length))
            };
        }).ToList();

        return new RoomStats
        {
            File = file,
            DurationMs = Round(meter.Length * 1000d / sampleRate),
            MeterPeak = Round(Max(meter)),
            MeterP99 = Round(Percentile(meter, 0.99)),
            RmsDbfs = Round(RmsDbfs(mono, 0, mono.Length)),
            HighPassMeterPeak = Round(PeakLevel(Filter(mono, sampleRate, HighPassHz, null), 0, mono.Length)),
            Bands = bands
        };
    }

    public static SwingSound Swing(int index, float[] interleaved, int channels, int sampleRate, long detectedFrame, RoomStats? room, double detectLevel, string file)
    {
        var meter = Meter(interleaved, channels);
        var mono = Mono(interleaved, channels);
        var frames = meter.Length;
        var roomPeak = room?.MeterPeak ?? 0;
        var startLevel = Math.Min(Math.Max(roomPeak * 1.5, 1.5), detectLevel * 0.75);
        var quietFrames = Math.Max(1, (int)Math.Round(sampleRate * 0.01));
        var segments = Segments(Envelope(meter, sampleRate, 0.005), startLevel, quietFrames);
        var envelope = Envelope(meter, sampleRate, 0.001);

        var highPass = Filter(mono, sampleRate, HighPassHz, null);
        var bandSignals = BandEdges.Select(band => Filter(mono, sampleRate, band.Low, band.High)).ToArray();
        var window = Math.Max(1, (int)Math.Round(sampleRate * 0.02));
        var events = new List<SoundEvent>();
        foreach (var (onset, end) in segments)
        {
            var peakIndex = onset;
            for (var i = onset; i <= end; i++)
            {
                if (meter[i] > meter[peakIndex])
                {
                    peakIndex = i;
                }
            }

            var peak = meter[peakIndex];
            var rise10 = FirstAtLeast(envelope, onset, peakIndex, peak * 0.1f);
            var rise90 = FirstAtLeast(envelope, onset, peakIndex, peak * 0.9f);
            var decay = peakIndex;
            while (decay < frames - 1 && envelope[decay] >= peak * 0.1f)
            {
                decay++;
            }

            var clipped = 0;
            for (var i = onset * channels; i < Math.Min(interleaved.Length, (end + 1) * channels); i++)
            {
                if (Math.Abs(interleaved[i]) >= ClipLevel)
                {
                    clipped++;
                }
            }

            var windowEnd = Math.Min(frames, onset + window);
            var energies = bandSignals.Select(signal => Energy(signal, onset, windowEnd)).ToArray();
            var total = energies.Sum();
            var bands = BandEdges.Select((band, bandIndex) => new EventBand
            {
                Name = band.Name,
                Share = Round(total > 0 ? energies[bandIndex] / total : 0),
                RmsDbfs = Round(RmsDbfs(bandSignals[bandIndex], onset, windowEnd - onset))
            }).ToList();
            var dominant = total > 0 ? BandEdges[Array.IndexOf(energies, energies.Max())].Name : "";

            events.Add(new SoundEvent
            {
                StartMs = Round(Ms(onset, sampleRate)),
                MeterPeak = Round(peak),
                MonoPeak = Round(PeakLevel(mono, onset, end + 1)),
                HighPassMeterPeak = Round(PeakLevel(highPass, onset, end + 1)),
                RiseMs = Round(Ms(rise90 - rise10, sampleRate)),
                DecayMs = Round(Ms(decay - peakIndex, sampleRate)),
                DurationMs = Round(Ms(end - onset + 1, sampleRate)),
                ClippedSamples = clipped,
                DominantBand = dominant,
                Bands = bands
            });
        }

        AssignRoles(events, detectLevel);
        return new SwingSound
        {
            Index = index,
            File = file,
            DurationMs = Round(Ms(frames, sampleRate)),
            DetectedAtMs = Round(Ms(detectedFrame, sampleRate)),
            DetectLevel = Round(detectLevel),
            Events = events
        };
    }

    public static CalibrationAdvice Recommend(RoomStats? room, IReadOnlyList<SwingSound> swings, int channels, double? windowsLevel = null)
    {
        var advice = new CalibrationAdvice();
        advice.CurrentWindowsLevel = windowsLevel;
        var strikes = swings.Where(swing => swing.Strike is not null).Select(swing => (swing.Index, Strike: swing.Strike!)).ToList();
        if (swings.Count < 3)
        {
            advice.Warnings.Add($"Only {swings.Count} of 3 swings were recorded.");
        }

        if (strikes.Count == 0)
        {
            advice.Warnings.Add("No strikes were recorded, so no threshold is proposed.");
            return advice;
        }

        var fullRoom = room?.MeterPeak ?? 0;
        var highPassRoom = room?.HighPassMeterPeak ?? 0;
        var quietest = strikes.Min(item => item.Strike.HighPassMeterPeak);
        var loudest = strikes.Max(item => item.Strike.HighPassMeterPeak);
        var roomPeak = highPassRoom;
        advice.RoomMeterPeak = roomPeak;
        advice.QuietestStrike = quietest;
        advice.LoudestStrike = loudest;
        advice.Margin = roomPeak > 0 ? Round(quietest / roomPeak) : null;

        if (quietest <= roomPeak)
        {
            advice.Warnings.Add($"The quietest strike ({quietest:0.#}) was not louder than the room ({roomPeak:0.#}), so no threshold is proposed.");
        }
        else
        {
            advice.ProposedThreshold = ProposeThreshold(roomPeak, quietest);
            advice.Notes.Add("Room was measured while quiet. If music or talking fires the trigger, raise the threshold.");
        }

        if (advice.Margin is double margin && margin < 2)
        {
            advice.Warnings.Add($"The quietest strike is only {margin:0.0}x the loudest room sound; the trigger may miss strikes or fire on noise.");
        }

        if (roomPeak >= 50)
        {
            advice.Warnings.Add($"The room itself reaches {roomPeak:0} on the meter; turn the mic gain down or move the mic.");
        }

        AddGainAdvice(advice, strikes, windowsLevel);

        if (quietest > 0 && loudest / quietest > 3)
        {
            advice.Notes.Add($"Strike peaks ranged from {quietest:0.#} to {loudest:0.#}. That is normal across clubs; a driver is much louder than a soft iron.");
        }

        if (advice.ProposedThreshold is int proposed)
        {
            foreach (var (index, strike) in strikes)
            {
                if (strike.HighPassMeterPeak < proposed)
                {
                    advice.Warnings.Add($"Swing {index}: the above-2 kHz peak ({strike.HighPassMeterPeak:0.#}) is below {proposed}, so contact time would fall back to the trigger time.");
                }
            }
        }

        foreach (var swing in swings)
        {
            if (swing.Strike is not SoundEvent strike)
            {
                continue;
            }

            var loudestAfter = swing.Events.FirstOrDefault(sound => sound.Role == LoudestAfterRole);
            if (loudestAfter is not null)
            {
                var comparison = loudestAfter.MeterPeak > strike.MeterPeak ? "louder than" : "quieter than";
                advice.Notes.Add($"Swing {swing.Index}: loudest later sound at +{loudestAfter.FromStrikeMs:0} ms peaked at {loudestAfter.MeterPeak:0.#}, {comparison} the strike ({strike.MeterPeak:0.#}); strike is mostly {strike.DominantBand}, later sound mostly {loudestAfter.DominantBand}.");
            }
        }

        var highPassQuietest = quietest;
        advice.HighPassRoomPeak = highPassRoom;
        advice.HighPassQuietestStrike = highPassQuietest;
        advice.HighPassMargin = advice.Margin;
        var fullQuietest = strikes.Min(item => item.Strike.MeterPeak);
        var fullMargin = fullRoom > 0 ? Round(fullQuietest / fullRoom) : (double?)null;
        advice.HighPassBetter = fullMargin is double full && advice.HighPassMargin is double high && high > full * 1.5;
        advice.Notes.Add("The trigger and contact time listen above 2 kHz.");
        if (fullMargin is double compared)
        {
            advice.Notes.Add($"Above 2 kHz separates strikes from the room {advice.HighPassMargin:0.0}x versus {compared:0.0}x full-band.");
        }

        if (channels > 1)
        {
            advice.Notes.Add($"The mic delivers {channels} channels; the trigger uses the loudest filtered channel, contact time uses the filtered channel average.");
        }

        return advice;
    }

    private const double TargetPeak = 70;
    private const double QuietOk = 25;
    private const double LoudOk = 85;
    private const double MinThreshold = 5;
    private const double RoomHeadroom = 8;
    private const double QuietStrikeShare = 0.4;
    private const double MaxShareOfQuietest = 0.7;

    private static int ProposeThreshold(double roomPeak, double quietestStrike)
    {
        var fromRoom = roomPeak * RoomHeadroom;
        var fromStrike = quietestStrike * QuietStrikeShare;
        var ceiling = quietestStrike * MaxShareOfQuietest;
        var threshold = Math.Max(MinThreshold, Math.Max(fromRoom, fromStrike));
        if (ceiling >= MinThreshold)
        {
            threshold = Math.Min(threshold, ceiling);
        }

        return (int)Math.Clamp(Math.Round(threshold), 1, 99);
    }

    private static void AddGainAdvice(CalibrationAdvice advice, List<(int Index, SoundEvent Strike)> strikes, double? windowsLevel)
    {
        var clipped = strikes.Where(item => item.Strike.ClippedSamples > 0 || item.Strike.HighPassMeterPeak >= 99).ToList();
        var quietest = strikes.Min(item => item.Strike.HighPassMeterPeak);
        var loudest = strikes.Max(item => item.Strike.HighPassMeterPeak);
        double factor;
        string direction;
        if (clipped.Count > 0)
        {
            var worst = clipped.Max(item => item.Strike.ClippedSamples);
            factor = worst > 500 ? 0.35 : worst > 50 ? 0.5 : 0.7;
            direction = "down";
        }
        else if (loudest > LoudOk)
        {
            factor = TargetPeak / loudest;
            direction = "down";
        }
        else if (quietest > 0 && quietest < QuietOk)
        {
            factor = Math.Min(TargetPeak / quietest, LoudOk / Math.Max(loudest, 1));
            if (factor < 1.15)
            {
                advice.GainAdvice = "Mic gain looks good; leave the knob where it is.";
                advice.Notes.Add(advice.GainAdvice);
                return;
            }

            direction = "up";
        }
        else
        {
            advice.GainAdvice = "Mic gain looks good; leave the knob where it is.";
            advice.Notes.Add(advice.GainAdvice);
            return;
        }

        advice.GainDb = Round(20 * Math.Log10(factor));
        if (windowsLevel is double current && current >= 1)
        {
            advice.SuggestedWindowsLevel = Math.Clamp(Math.Round(current * factor), 5, 100);
        }

        var clips = clipped.Count == 0
            ? ""
            : $" {clipped.Count} of {strikes.Count} strikes clipped ({string.Join(", ", clipped.Select(item => $"swing {item.Index}: {item.Strike.ClippedSamples}"))}).";
        var level = loudest > LoudOk && clipped.Count == 0
            ? $" Loudest strike peaked at {loudest:0}."
            : quietest < QuietOk && clipped.Count == 0
                ? $" Quietest strike peaked at {quietest:0}."
                : "";
        advice.GainAdvice = $"Turn the mic gain {direction} {KnobAmount(factor)}.{WindowsAmount(windowsLevel, factor)}{clips}{level} Recalibrate after you change it.";
        advice.Warnings.Add(advice.GainAdvice);
    }

    private static string KnobAmount(double factor)
    {
        var db = Math.Abs(20 * Math.Log10(factor));
        if (factor < 1)
        {
            if (db < 4.5)
            {
                return "a little (about 3 dB)";
            }

            if (db < 7.5)
            {
                return "about half (6 dB)";
            }

            if (db < 10.5)
            {
                return "to about a third (9 dB)";
            }

            return "to about a quarter (12 dB)";
        }

        if (db < 4.5)
        {
            return "a little (about 3 dB)";
        }

        if (db < 7.5)
        {
            return "about double (6 dB)";
        }

        return "about triple (10 dB)";
    }

    private static string WindowsAmount(double? windowsLevel, double factor)
    {
        if (windowsLevel is not double current || current < 1)
        {
            return "";
        }

        if (factor > 1 && current >= 95)
        {
            return " The Windows microphone level is already at the top, so use the hardware gain knob.";
        }

        if (factor < 1 && current <= 15)
        {
            return " The Windows microphone level is already low, so use the hardware gain knob.";
        }

        var suggested = (int)Math.Clamp(Math.Round(current * factor), 5, 100);
        if (Math.Abs(suggested - current) < 2)
        {
            return "";
        }

        return $" Or set the Windows microphone level from {current:0} to {suggested}.";
    }

    public static float[] Trace(float[] interleaved, int channels, int sampleRate, double bucketMs)
    {
        var filtered = new float[interleaved.Length];
        var filter = new HighPassFilter(sampleRate, Math.Max(1, channels));
        for (var i = 0; i < interleaved.Length; i++)
        {
            filtered[i] = filter.Process(interleaved[i], i % filter.Channels);
        }

        var meter = Meter(filtered, channels);
        var bucket = Math.Max(1, (int)Math.Round(sampleRate * bucketMs / 1000));
        var trace = new float[(meter.Length + bucket - 1) / bucket];
        for (var i = 0; i < meter.Length; i++)
        {
            trace[i / bucket] = Math.Max(trace[i / bucket], meter[i]);
        }

        return trace;
    }

    public static double MeterPeak(float[] interleaved)
    {
        var peak = 0f;
        foreach (var sample in interleaved)
        {
            peak = Math.Max(peak, Math.Abs(sample));
        }

        return Math.Min(peak, 1) * 100;
    }

    public static double HighPassMeterPeak(float[] interleaved, int channels, int sampleRate)
    {
        var filter = new HighPassFilter(sampleRate, Math.Max(1, channels));
        return filter.Peak(interleaved, int.MaxValue);
    }

    private static void AssignRoles(List<SoundEvent> events, double detectLevel)
    {
        if (events.Count == 0)
        {
            return;
        }

        var strikeIndex = events.FindIndex(sound => sound.HighPassMeterPeak >= detectLevel * 0.99);
        if (strikeIndex < 0)
        {
            strikeIndex = events.IndexOf(events.MaxBy(sound => sound.HighPassMeterPeak)!);
        }

        var strike = events[strikeIndex];
        SoundEvent? loudestAfter = null;
        for (var i = 0; i < events.Count; i++)
        {
            var sound = events[i];
            sound.FromStrikeMs = Round(sound.StartMs - strike.StartMs);
            sound.Role = i < strikeIndex ? BeforeRole : i == strikeIndex ? StrikeRole : AfterRole;
            if (i > strikeIndex && (loudestAfter is null || sound.MeterPeak > loudestAfter.MeterPeak))
            {
                loudestAfter = sound;
            }
        }

        if (loudestAfter is not null)
        {
            loudestAfter.Role = LoudestAfterRole;
        }
    }

    private static float[] Envelope(float[] meter, int sampleRate, double releaseSeconds)
    {
        var release = Math.Exp(-1d / (sampleRate * releaseSeconds));
        var envelope = new float[meter.Length];
        double level = 0;
        for (var i = 0; i < meter.Length; i++)
        {
            level = Math.Max(meter[i], level * release);
            envelope[i] = (float)level;
        }

        return envelope;
    }

    private static List<(int Onset, int End)> Segments(float[] envelope, double startLevel, int quietFrames)
    {
        var segments = new List<(int, int)>();
        var onset = -1;
        var lastLoud = -1;
        var peak = 0f;
        var floor = 0f;
        for (var i = 0; i < envelope.Length; i++)
        {
            if (onset < 0)
            {
                floor = Math.Min(floor, envelope[i]);
                if (envelope[i] >= startLevel && envelope[i] >= floor * 2)
                {
                    onset = i;
                    lastLoud = i;
                    peak = envelope[i];
                }

                continue;
            }

            peak = Math.Max(peak, envelope[i]);
            if (envelope[i] >= Math.Max(startLevel, peak * 0.25))
            {
                lastLoud = i;
            }
            else if (i - lastLoud > quietFrames)
            {
                segments.Add((onset, lastLoud));
                onset = -1;
                floor = envelope[i];
            }
        }

        if (onset >= 0)
        {
            segments.Add((onset, lastLoud));
        }

        return segments;
    }

    private static float[] Meter(float[] interleaved, int channels)
    {
        channels = Math.Max(1, channels);
        var meter = new float[interleaved.Length / channels];
        for (var frame = 0; frame < meter.Length; frame++)
        {
            var peak = 0f;
            for (var channel = 0; channel < channels; channel++)
            {
                peak = Math.Max(peak, Math.Abs(interleaved[frame * channels + channel]));
            }

            meter[frame] = Math.Min(peak, 1) * 100;
        }

        return meter;
    }

    private static float[] Mono(float[] interleaved, int channels)
    {
        channels = Math.Max(1, channels);
        var mono = new float[interleaved.Length / channels];
        for (var frame = 0; frame < mono.Length; frame++)
        {
            float sum = 0;
            for (var channel = 0; channel < channels; channel++)
            {
                sum += interleaved[frame * channels + channel];
            }

            mono[frame] = sum / channels;
        }

        return mono;
    }

    private static float[] Filter(float[] samples, int sampleRate, double? lowHz, double? highHz)
    {
        var nyquistLimit = sampleRate * 0.45;
        if (lowHz is double low && low >= nyquistLimit)
        {
            return new float[samples.Length];
        }

        var output = samples;
        if (lowHz is double highPassHz)
        {
            output = Biquad(output, sampleRate, highPassHz, highPass: true);
        }

        if (highHz is double lowPassHz && lowPassHz < nyquistLimit)
        {
            output = Biquad(output, sampleRate, lowPassHz, highPass: false);
        }

        return ReferenceEquals(output, samples) ? samples.ToArray() : output;
    }

    private static float[] Biquad(float[] input, int sampleRate, double cutoffHz, bool highPass)
    {
        var w0 = 2 * Math.PI * cutoffHz / sampleRate;
        var cos = Math.Cos(w0);
        var alpha = Math.Sin(w0) / (2 * Math.Sqrt(0.5));
        var b0 = highPass ? (1 + cos) / 2 : (1 - cos) / 2;
        var b1 = highPass ? -(1 + cos) : 1 - cos;
        var b2 = b0;
        var a0 = 1 + alpha;
        var a1 = -2 * cos;
        var a2 = 1 - alpha;
        b0 /= a0;
        b1 /= a0;
        b2 /= a0;
        a1 /= a0;
        a2 /= a0;

        var output = new float[input.Length];
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (var i = 0; i < input.Length; i++)
        {
            double x0 = input[i];
            var y0 = b0 * x0 + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            output[i] = (float)y0;
            x2 = x1;
            x1 = x0;
            y2 = y1;
            y1 = y0;
        }

        return output;
    }

    private static int FirstAtLeast(float[] values, int from, int to, float level)
    {
        for (var i = from; i <= to; i++)
        {
            if (values[i] >= level)
            {
                return i;
            }
        }

        return to;
    }

    private static double Energy(float[] signal, int from, int to)
    {
        double sum = 0;
        for (var i = from; i < to; i++)
        {
            sum += signal[i] * (double)signal[i];
        }

        return sum;
    }

    private static double RmsDbfs(float[] signal, int from, int count)
    {
        if (count <= 0)
        {
            return -120;
        }

        var rms = Math.Sqrt(Energy(signal, from, from + count) / count);
        return rms > 1e-6 ? 20 * Math.Log10(rms) : -120;
    }

    private static double PeakLevel(float[] signal, int from, int to)
    {
        var peak = 0f;
        for (var i = from; i < Math.Min(to, signal.Length); i++)
        {
            peak = Math.Max(peak, Math.Abs(signal[i]));
        }

        return Math.Min(peak, 1) * 100;
    }

    private static float Max(float[] values) => values.Length == 0 ? 0 : values.Max();

    private static float Percentile(float[] values, double fraction)
    {
        if (values.Length == 0)
        {
            return 0;
        }

        var sorted = values.ToArray();
        Array.Sort(sorted);
        return sorted[Math.Clamp((int)(fraction * (sorted.Length - 1)), 0, sorted.Length - 1)];
    }

    private static double Ms(long frames, int sampleRate) => frames * 1000d / sampleRate;

    private static double Round(double value) => double.IsFinite(value) ? Math.Round(value, 3) : 0;
}

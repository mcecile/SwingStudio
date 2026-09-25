using System.Globalization;
using System.IO;
using System.Text.Json;
using NAudio.Wave;

namespace SwingStudio.Session;

public static class CalibrationStore
{
    public const string ReportFileName = "calibration.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static void Save(string folder, CalibrationReport report, float[]? room, IReadOnlyList<float[]?> swings, int sampleRate, int channels)
    {
        Directory.CreateDirectory(folder);
        var format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        WriteOrDelete(Path.Combine(folder, "room.wav"), room, format);
        for (var i = 0; i < swings.Count; i++)
        {
            WriteOrDelete(Path.Combine(folder, $"swing{i + 1}.wav"), swings[i], format);
        }

        File.WriteAllText(Path.Combine(folder, ReportFileName), JsonSerializer.Serialize(report, JsonOptions));
    }

    public static void LogReport(CalibrationReport report, string folder)
    {
        Log.Info(Invariant($"Calibration {(report.Complete ? "complete" : "incomplete")}: {report.Microphone}, {report.SampleRate} Hz, {report.Channels} channel(s), {report.SourceFormat}; detect level {report.DetectLevel:0.##}; threshold at start {report.ThresholdAtStart}, offset {report.OffsetMsAtStart} ms."));
        if (report.Room is RoomStats room)
        {
            var bands = string.Join("; ", room.Bands.Select(band => Invariant($"{band.Name} peak {band.MeterPeak:0.##} p99 {band.MeterP99:0.##} rms {band.RmsDbfs:0.#} dBFS")));
            Log.Info(Invariant($"Calibration room: {room.DurationMs:0} ms, peak {room.MeterPeak:0.##}, p99 {room.MeterP99:0.##}, rms {room.RmsDbfs:0.#} dBFS, above-2kHz peak {room.HighPassMeterPeak:0.##}. Bands: {bands}."));
        }

        foreach (var swing in report.Swings)
        {
            Log.Info(Invariant($"Calibration swing {swing.Index}: {swing.DurationMs:0} ms clip, detected at {swing.DetectedAtMs:0} ms, {swing.Events.Count} sound(s)."));
            foreach (var sound in swing.Events)
            {
                var bands = string.Join(", ", sound.Bands.Select(band => Invariant($"{band.Name} {band.Share:P0} ({band.RmsDbfs:0.#} dBFS)")));
                Log.Info(Invariant($"Calibration swing {swing.Index} sound: role {sound.Role}, at {sound.StartMs:0.0} ms ({sound.FromStrikeMs:+0.0;-0.0;0} ms from strike), peak {sound.MeterPeak:0.##}, channel-average peak {sound.MonoPeak:0.##}, above-2kHz peak {sound.HighPassMeterPeak:0.##}, rise {sound.RiseMs:0.00} ms, decay {sound.DecayMs:0.0} ms, duration {sound.DurationMs:0.0} ms, clipped {sound.ClippedSamples}, dominant {sound.DominantBand}. First 20 ms: {bands}."));
            }
        }

        if (report.Advice is CalibrationAdvice advice)
        {
            Log.Info(Invariant($"Calibration advice: proposed threshold {(advice.ProposedThreshold?.ToString(CultureInfo.InvariantCulture) ?? "none")}, room peak {advice.RoomMeterPeak:0.##}, strikes {advice.QuietestStrike:0.##} to {advice.LoudestStrike:0.##}, margin {Ratio(advice.Margin)}; above 2 kHz: room {advice.HighPassRoomPeak:0.##}, quietest strike {advice.HighPassQuietestStrike:0.##}, margin {Ratio(advice.HighPassMargin)}, better {advice.HighPassBetter}."));
            foreach (var warning in advice.Warnings)
            {
                Log.Warn("Calibration: " + warning);
            }

            foreach (var note in advice.Notes)
            {
                Log.Info("Calibration note: " + note);
            }
        }

        Log.Info("Calibration files: " + folder);
    }

    private static void WriteOrDelete(string path, float[]? samples, WaveFormat format)
    {
        if (samples is null)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return;
        }

        using var writer = new WaveFileWriter(path, format);
        writer.WriteSamples(samples, 0, samples.Length);
    }

    private static string Ratio(double? value) => value is double ratio ? ratio.ToString("0.0", CultureInfo.InvariantCulture) + "x" : "n/a";

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

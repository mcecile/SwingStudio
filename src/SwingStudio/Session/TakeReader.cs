using System.Globalization;
using System.IO;
using OpenCvSharp;
using SwingStudio.Capture;

namespace SwingStudio.Session;

public sealed class LoadedSwing : IDisposable
{
    public FrameSlice CameraA { get; } = new();

    public FrameSlice CameraB { get; } = new();

    public double ContactMs { get; init; }

    public void Dispose()
    {
        CameraA.Dispose();
        CameraB.Dispose();
    }
}

public static class TakeReader
{
    public static LoadedSwing Read(string folder)
    {
        var session = SwingCatalog.ReadSession(folder) ?? throw new IOException("This swing could not be read.");
        var loaded = new LoadedSwing { ContactMs = session.ContactMs ?? 0 };
        try
        {
            ReadCamera(folder, SwingSession.CameraAVideoFileName, SwingSession.CameraATimestampsFileName, loaded.CameraA);
            ReadCamera(folder, SwingSession.CameraBVideoFileName, SwingSession.CameraBTimestampsFileName, loaded.CameraB);
            return loaded;
        }
        catch
        {
            loaded.Dispose();
            throw;
        }
    }

    private static void ReadCamera(string folder, string videoName, string timestampName, FrameSlice slice)
    {
        var video = Path.Combine(folder, videoName);
        if (!File.Exists(video))
        {
            return;
        }

        var timestamps = ReadTimestamps(Path.Combine(folder, timestampName));
        using var capture = new VideoCapture(video);
        if (!capture.IsOpened())
        {
            throw new IOException("The swing video could not be opened.");
        }

        using var frame = new Mat();
        var index = 0;
        while (capture.Read(frame))
        {
            if (frame.Empty())
            {
                break;
            }

            var time = timestamps.TryGetValue(index, out var sessionMs) ? sessionMs : index * (1000d / 30d);
            slice.Frames.Add(new FrameRing.TimedFrame(time, frame.Clone()));
            index++;
        }
    }

    private static Dictionary<int, double> ReadTimestamps(string path)
    {
        var timestamps = new Dictionary<int, double>();
        if (!File.Exists(path))
        {
            return timestamps;
        }

        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var parts = line.Split(',');
            if (parts.Length < 2)
            {
                continue;
            }

            if (int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var frame)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var sessionMs))
            {
                timestamps[frame] = sessionMs;
            }
        }

        return timestamps;
    }
}

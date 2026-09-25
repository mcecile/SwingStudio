using System.Globalization;
using System.IO;
using System.Text.Json;
using NAudio.Wave;
using OpenCvSharp;
using SwingStudio.Capture;

namespace SwingStudio.Session;

public static class TakeWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static void Write(
        SwingSession session,
        FrameSlice cameraA,
        FrameSlice cameraB,
        AudioSlice audio,
        double windowStartMs,
        double triggerMs,
        double offsetMs,
        double triggerThreshold)
    {
        Directory.CreateDirectory(session.FolderPath);
        var takeLengthMs = LastTime(cameraA, cameraB, audio, windowStartMs);
        WriteAudio(session, audio, windowStartMs, takeLengthMs, triggerMs, offsetMs, triggerThreshold);
        session.AchievedModeA = WriteCamera(session, cameraA, SwingSession.CameraAVideoFileName, SwingSession.CameraATimestampsFileName, windowStartMs);
        session.AchievedModeB = WriteCamera(session, cameraB, SwingSession.CameraBVideoFileName, SwingSession.CameraBTimestampsFileName, windowStartMs);
        File.WriteAllText(Path.Combine(session.FolderPath, SwingSession.ManifestFileName), JsonSerializer.Serialize(session, JsonOptions));
    }

    private static void WriteAudio(SwingSession session, AudioSlice audio, double windowStartMs, double takeLengthMs, double triggerMs, double offsetMs, double triggerThreshold)
    {
        if (audio.Format is not WaveFormat format || audio.Packets.Count == 0)
        {
            Log.Warn("The take has no audio; contact uses the trigger time.");
            session.ContactMs = Math.Clamp(triggerMs + offsetMs, 0, Math.Max(0, takeLengthMs));
            return;
        }

        var mono = ToMono(audio, format);
        var audioStartMs = audio.Packets[0].TimeMs - windowStartMs;
        session.SampleRate = format.SampleRate;
        session.AudioStartMs = audioStartMs;
        session.ContactMs = ContactTime.Resolve(mono, format.SampleRate, audioStartMs, takeLengthMs, triggerMs, offsetMs, triggerThreshold);

        var pcmFormat = new WaveFormat(format.SampleRate, 16, format.Channels);
        using var writer = new WaveFileWriter(Path.Combine(session.FolderPath, SwingSession.MicrophoneFileName), pcmFormat);
        foreach (var packet in audio.Packets)
        {
            var pcm = ToPcm16(packet.Data, format);
            writer.Write(pcm, 0, pcm.Length);
        }
    }

    private static CaptureMode? WriteCamera(SwingSession session, FrameSlice frames, string videoName, string timestampName, double windowStartMs)
    {
        if (frames.Frames.Count == 0)
        {
            return null;
        }

        var first = frames.Frames[0].Frame;
        var width = first.Width;
        var height = first.Height;
        var span = frames.Frames[^1].TimeMs - frames.Frames[0].TimeMs;
        var fps = span > 1 ? (frames.Frames.Count - 1) * 1000d / span : 30;
        fps = Math.Clamp(fps, 1, 240);
        var path = Path.Combine(session.FolderPath, videoName);
        using (var writer = OpenWriter(path, fps, width, height))
        {
            foreach (var frame in frames.Frames)
            {
                using var prepared = Prepare(frame.Frame, width, height);
                writer.Write(prepared);
            }
        }

        using (var timestamps = new StreamWriter(Path.Combine(session.FolderPath, timestampName)))
        {
            timestamps.WriteLine("frame,sessionMs");
            for (var index = 0; index < frames.Frames.Count; index++)
            {
                var sessionMs = frames.Frames[index].TimeMs - windowStartMs;
                timestamps.Write(index.ToString(CultureInfo.InvariantCulture));
                timestamps.Write(',');
                timestamps.WriteLine(sessionMs.ToString("0.###", CultureInfo.InvariantCulture));
            }
        }

        return new CaptureMode
        {
            Width = width,
            Height = height,
            FramesPerSecond = Math.Round(fps, 2),
            FourCc = session.RequestedMode.FourCc
        };
    }

    private static VideoWriter OpenWriter(string path, double fps, int width, int height)
    {
        var size = new Size(width, height);
        foreach (var fourCc in new[] { "mp4v", "avc1", "MJPG" })
        {
            var writer = new VideoWriter(path, FourCC.FromString(fourCc), fps, size);
            if (writer.IsOpened())
            {
                if (fourCc != "mp4v")
                {
                    Log.Warn($"{Path.GetFileName(path)} is written with {fourCc} because mp4v did not open.");
                }

                return writer;
            }

            writer.Dispose();
        }

        Log.Error($"No video codec opened for {path} ({width}x{height}, {fps:0.##} fps).");
        throw new IOException("The camera video could not be opened for writing.");
    }

    private static Mat Prepare(Mat frame, int width, int height)
    {
        Mat source = frame;
        Mat? resized = null;
        if (frame.Width != width || frame.Height != height)
        {
            resized = new Mat();
            Cv2.Resize(frame, resized, new Size(width, height));
            source = resized;
        }

        if (source.Channels() == 3)
        {
            return resized ?? source.Clone();
        }

        var color = new Mat();
        var conversion = source.Channels() == 1 ? ColorConversionCodes.GRAY2BGR : ColorConversionCodes.BGRA2BGR;
        Cv2.CvtColor(source, color, conversion);
        resized?.Dispose();
        return color;
    }

    private static double LastTime(FrameSlice cameraA, FrameSlice cameraB, AudioSlice audio, double windowStartMs)
    {
        var last = windowStartMs;
        if (cameraA.Frames.Count > 0)
        {
            last = Math.Max(last, cameraA.Frames[^1].TimeMs);
        }

        if (cameraB.Frames.Count > 0)
        {
            last = Math.Max(last, cameraB.Frames[^1].TimeMs);
        }

        if (audio.Format is WaveFormat format && audio.Packets.Count > 0)
        {
            var packet = audio.Packets[^1];
            var duration = packet.Data.Length / (double)format.BlockAlign / format.SampleRate * 1000;
            last = Math.Max(last, packet.TimeMs + duration);
        }

        return Math.Max(0, last - windowStartMs);
    }

    private static float[] ToMono(AudioSlice audio, WaveFormat format)
    {
        var channels = Math.Max(1, format.Channels);
        var bytesPerSample = Math.Max(1, format.BitsPerSample / 8);
        var block = Math.Max(bytesPerSample, format.BlockAlign);
        var frameCount = audio.Packets.Sum(packet => packet.Data.Length / block);
        var samples = new float[frameCount];
        var index = 0;
        foreach (var packet in audio.Packets)
        {
            var data = packet.Data;
            for (var offset = 0; offset + block <= data.Length; offset += block)
            {
                float sum = 0;
                for (var channel = 0; channel < channels; channel++)
                {
                    sum += ReadSample(data, offset + channel * bytesPerSample, format);
                }

                samples[index++] = sum / channels;
            }
        }

        return samples;
    }

    private static byte[] ToPcm16(byte[] data, WaveFormat format)
    {
        var channels = Math.Max(1, format.Channels);
        var bytesPerSample = Math.Max(1, format.BitsPerSample / 8);
        var block = Math.Max(bytesPerSample, format.BlockAlign);
        var frameCount = data.Length / block;
        var pcm = new byte[frameCount * channels * 2];
        var index = 0;
        for (var offset = 0; offset + block <= data.Length; offset += block)
        {
            for (var channel = 0; channel < channels; channel++)
            {
                var sample = Math.Clamp(ReadSample(data, offset + channel * bytesPerSample, format), -1, 1);
                var value = (short)Math.Round(sample * 32767);
                pcm[index++] = (byte)value;
                pcm[index++] = (byte)(value >> 8);
            }
        }

        return pcm;
    }

    internal static float[] ToFloat(byte[] data, WaveFormat format)
    {
        var bytesPerSample = Math.Max(1, format.BitsPerSample / 8);
        var samples = new float[data.Length / bytesPerSample];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = ReadSample(data, i * bytesPerSample, format);
        }

        return samples;
    }

    private static float ReadSample(byte[] data, int offset, WaveFormat format)
    {
        if (offset < 0 || offset >= data.Length)
        {
            return 0;
        }

        var encoding = format.Encoding;
        if (encoding == WaveFormatEncoding.Extensible && format.BitsPerSample == 32)
        {
            encoding = WaveFormatEncoding.IeeeFloat;
        }

        if (encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32 && offset + 4 <= data.Length)
        {
            return BitConverter.ToSingle(data, offset);
        }

        if (encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16 && offset + 2 <= data.Length)
        {
            return BitConverter.ToInt16(data, offset) / 32768f;
        }

        if (encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 32 && offset + 4 <= data.Length)
        {
            return BitConverter.ToInt32(data, offset) / 2147483648f;
        }

        return 0;
    }
}

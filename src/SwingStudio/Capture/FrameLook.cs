using OpenCvSharp;

namespace SwingStudio.Capture;

/// <summary>Grayscale plus a shadow lift for IR MJPG frames. Used on display and save, not on the grab.</summary>
public static class FrameLook
{
    public const double Gamma = 0.72;

    private static readonly byte[] LutBytes = BuildLut(Gamma);
    private static readonly Mat Lut = Mat.FromPixelData(1, 256, MatType.CV_8UC1, LutBytes);

    public static Mat ApplyGray(Mat frame)
    {
        using var gray = ToGray(frame);
        var lifted = new Mat();
        Cv2.LUT(gray, Lut, lifted);
        return lifted;
    }

    public static Mat ApplyBgr(Mat frame)
    {
        using var gray = ApplyGray(frame);
        var color = new Mat();
        Cv2.CvtColor(gray, color, ColorConversionCodes.GRAY2BGR);
        return color;
    }

    private static Mat ToGray(Mat frame)
    {
        if (frame.Channels() == 1)
        {
            return frame.Clone();
        }

        var gray = new Mat();
        Cv2.CvtColor(frame, gray, frame.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
        return gray;
    }

    private static byte[] BuildLut(double gamma)
    {
        var bytes = new byte[256];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)Math.Clamp(Math.Round(255 * Math.Pow(i / 255d, gamma)), 0, 255);
        }

        return bytes;
    }
}

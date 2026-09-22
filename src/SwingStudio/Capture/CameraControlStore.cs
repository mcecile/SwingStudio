using OpenCvSharp;

namespace SwingStudio.Capture;

public static class CameraControlStore
{
    private static readonly Control[] Controls =
    [
        new(VideoCaptureProperties.AutoExposure, true),
        new(VideoCaptureProperties.AutoFocus, true),
        new(VideoCaptureProperties.AutoWB, true),
        new(VideoCaptureProperties.Exposure, true),
        new(VideoCaptureProperties.Gain, true),
        new(VideoCaptureProperties.Brightness, false),
        new(VideoCaptureProperties.Contrast, false),
        new(VideoCaptureProperties.Saturation, false),
        new(VideoCaptureProperties.Hue, false),
        new(VideoCaptureProperties.Sharpness, false),
        new(VideoCaptureProperties.Gamma, false),
        new(VideoCaptureProperties.BackLight, false),
        new(VideoCaptureProperties.WhiteBalanceBlueU, false),
        new(VideoCaptureProperties.Focus, false),
        new(VideoCaptureProperties.Zoom, false)
    ];

    public static void Apply(VideoCapture capture, IReadOnlyDictionary<string, double> values)
    {
        foreach (var control in Controls)
        {
            if (values.TryGetValue(control.Property.ToString(), out var value))
            {
                capture.Set(control.Property, value);
            }
        }
    }

    public static Dictionary<string, double> Read(VideoCapture capture)
    {
        var values = new Dictionary<string, double>();
        foreach (var control in Controls)
        {
            var value = capture.Get(control.Property);
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                continue;
            }

            if (value == 0 && !control.KeepZero)
            {
                continue;
            }

            values[control.Property.ToString()] = value;
        }

        return values;
    }

    private readonly record struct Control(VideoCaptureProperties Property, bool KeepZero);
}

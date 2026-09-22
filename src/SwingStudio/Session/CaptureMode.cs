namespace SwingStudio.Session;

public sealed class CaptureMode
{
    public int Width { get; set; } = 640;

    public int Height { get; set; } = 480;

    public double FramesPerSecond { get; set; } = 210;

    public string FourCc { get; set; } = "MJPG";
}

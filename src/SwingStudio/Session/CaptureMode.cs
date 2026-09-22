namespace SwingStudio.Session;

public sealed class CaptureMode
{
    public int Width { get; set; } = 640;

    public int Height { get; set; } = 480;

    public double FramesPerSecond { get; set; } = 210;

    public string FourCc { get; set; } = "MJPG";

    /// <summary>True when this row is the saved request and Camera A did not offer it.</summary>
    public bool IsSavedRequest { get; set; }

    public override string ToString() => Label;

    public string Label
    {
        get
        {
            var format = FourCc.Equals("MJPG", StringComparison.OrdinalIgnoreCase) ? "MJPEG" : FourCc;
            var frames = Math.Abs(FramesPerSecond - Math.Round(FramesPerSecond)) < 0.05
                ? FramesPerSecond.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
                : FramesPerSecond.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            var label = $"{Width}×{Height}  {format}  {frames} fps";
            return IsSavedRequest ? $"{label} (saved)" : label;
        }
    }

    public bool Matches(int width, int height, double framesPerSecond, string fourCc) =>
        Width == width
        && Height == height
        && Math.Abs(FramesPerSecond - framesPerSecond) < 0.1
        && FourCc.Equals(fourCc, StringComparison.OrdinalIgnoreCase);
}

namespace SwingStudio.Capture;

public sealed record CameraDevice(int Index, string Name, string DevicePath)
{
    public override string ToString() => Name;
}

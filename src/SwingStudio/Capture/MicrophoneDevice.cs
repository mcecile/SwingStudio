namespace SwingStudio.Capture;

public sealed record MicrophoneDevice(string Id, string Name)
{
    public override string ToString() => Name;
}

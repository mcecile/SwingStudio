using NAudio.Wave;

namespace SwingStudio;

public sealed class CalibrationHost(
    string? microphoneName,
    Action<Action<byte[], WaveFormat>?> setAudioTap,
    Action<bool> setCalibrating)
{
    public string? MicrophoneName { get; } = microphoneName;

    public bool HasMicrophone => !string.IsNullOrEmpty(MicrophoneName);

    public void Begin(Action<byte[], WaveFormat> onAudio)
    {
        setCalibrating(true);
        setAudioTap(onAudio);
    }

    public void End()
    {
        setAudioTap(null);
        setCalibrating(false);
    }
}

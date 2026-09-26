using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SwingStudio;

public sealed class CalibrationHost(
    string? microphoneName,
    string? deviceId,
    Action<Action<byte[], WaveFormat>?> setAudioTap,
    Action<bool> setCalibrating)
{
    public string? MicrophoneName { get; } = microphoneName;

    public bool HasMicrophone => !string.IsNullOrEmpty(MicrophoneName);

    public double? WindowsCaptureLevel()
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return null;
        }

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDevice(deviceId);
            if (device.AudioEndpointVolume.Mute)
            {
                return 0;
            }

            return Math.Round(device.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
        }
        catch
        {
            return null;
        }
    }

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

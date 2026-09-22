using NAudio.CoreAudioApi;

namespace SwingStudio.Capture;

public static class MicrophoneEnumerator
{
    public static IReadOnlyList<MicrophoneDevice> List()
    {
        var microphones = new List<MicrophoneDevice>();
        using var enumerator = new MMDeviceEnumerator();
        using var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        for (var index = 0; index < devices.Count; index++)
        {
            using var device = devices[index];
            if (!string.IsNullOrWhiteSpace(device.ID))
            {
                microphones.Add(new MicrophoneDevice(device.ID, device.FriendlyName));
            }
        }

        return microphones;
    }
}

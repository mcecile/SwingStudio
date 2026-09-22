namespace SwingStudio.Settings;

public sealed class AppSettings
{
    public double LeftPaneShare { get; set; } = 0.5;

    public double? WindowLeft { get; set; }

    public double? WindowTop { get; set; }

    public double WindowWidth { get; set; } = 1280;

    public double WindowHeight { get; set; } = 800;

    public string? CameraADevicePath { get; set; }

    public string? CameraAFriendlyName { get; set; }

    public string? CameraBDevicePath { get; set; }

    public string? CameraBFriendlyName { get; set; }

    public string? MicrophoneDeviceId { get; set; }

    public int TriggerThreshold { get; set; } = 50;

    public double SecondsBeforeImpact { get; set; } = 1.0;

    public double SecondsAfterImpact { get; set; } = 0.5;

    /// <summary>How many times a take plays before live view returns. Allowed values are 1, 2, or 3.</summary>
    public int ReplaysBeforeLive { get; set; } = 2;

    public int CaptureWidth { get; set; } = 640;

    public int CaptureHeight { get; set; } = 480;

    public double CaptureFramesPerSecond { get; set; } = 210;

    public string CaptureFourCc { get; set; } = "MJPG";

    public string SessionFolder { get; set; } = AppPaths.SessionsRoot;

    public int ContactOffsetMs { get; set; }
}

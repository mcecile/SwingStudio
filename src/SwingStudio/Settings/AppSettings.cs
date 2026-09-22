namespace SwingStudio.Settings;

public sealed class AppSettings
{
    public double LeftPaneShare { get; set; } = 0.5;

    /// <summary>Share of the camera-and-pressure stack given to the cameras.</summary>
    public double CameraRowShare { get; set; } = 0.75;

    /// <summary>Share of the pressure row given to the foot map.</summary>
    public double PressurePaneShare { get; set; } = 0.5;

    public double? WindowLeft { get; set; }

    public double? WindowTop { get; set; }

    public double WindowWidth { get; set; } = 1280;

    public double WindowHeight { get; set; } = 800;

    public bool WindowMaximized { get; set; }

    public string? CameraADevicePath { get; set; }

    public string? CameraAFriendlyName { get; set; }

    public string? CameraBDevicePath { get; set; }

    public string? CameraBFriendlyName { get; set; }

    /// <summary>DirectShow camera controls keyed by device path, then property name.</summary>
    public Dictionary<string, Dictionary<string, double>> CameraControls { get; set; } = [];

    public string? MicrophoneDeviceId { get; set; }

    public string? MicrophoneFriendlyName { get; set; }

    public int TriggerThreshold { get; set; } = 50;

    public double SecondsBeforeImpact { get; set; } = 1.0;

    public double SecondsAfterImpact { get; set; } = 0.5;

    /// <summary>How many times a take plays before live view returns. Allowed values are 1, 2, or 3.</summary>
    public int ReplaysBeforeLive { get; set; } = 2;

    /// <summary>Playback rate. One of 1/4, 1/3, 1/2, or 1/1.</summary>
    public string PlaybackSpeed { get; set; } = "1/1";

    public int CaptureWidth { get; set; } = 640;

    public int CaptureHeight { get; set; } = 480;

    public double CaptureFramesPerSecond { get; set; } = 210;

    public string CaptureFourCc { get; set; } = "MJPG";

    public string SessionFolder { get; set; } = AppPaths.SessionsRoot;

    /// <summary>How many unsaved swings to keep. Saved swings are not counted.</summary>
    public int SwingsToKeep { get; set; } = 10;

    public int ContactOffsetMs { get; set; }
}

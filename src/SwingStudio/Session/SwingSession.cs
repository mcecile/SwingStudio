namespace SwingStudio.Session;

public sealed class SwingSession
{
    public const string ManifestFileName = "session.json";
    public const string CameraAVideoFileName = "cameraA.mp4";
    public const string CameraBVideoFileName = "cameraB.mp4";
    public const string CameraATimestampsFileName = "cameraA.timestamps.csv";
    public const string CameraBTimestampsFileName = "cameraB.timestamps.csv";
    public const string MicrophoneFileName = "mic.wav";
    public const string TracksFileName = "tracks.json";
    public const string PressureFileName = "pressure.csv";

    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTimeOffset StartedAt { get; set; }

    public string? CameraADevicePath { get; set; }

    public string? CameraBDevicePath { get; set; }

    public CaptureMode RequestedMode { get; set; } = new();

    public CaptureMode? AchievedModeA { get; set; }

    public CaptureMode? AchievedModeB { get; set; }

    public string? MicrophoneDeviceId { get; set; }

    public int? SampleRate { get; set; }

    public double? AudioStartMs { get; set; }

    public double? ContactMs { get; set; }

    public string FolderPath => AppPaths.SessionFolder(Id);
}

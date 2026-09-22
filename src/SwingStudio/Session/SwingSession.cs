using System.IO;
using System.Text.Json.Serialization;

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

    public string? Name { get; set; }

    public bool Saved { get; set; }

    public List<SwingStroke> Drawings { get; set; } = [];

    [JsonIgnore]
    public string? SessionRoot { get; set; }

    [JsonIgnore]
    public string FolderPath => Path.Combine(
        string.IsNullOrWhiteSpace(SessionRoot) ? AppPaths.SessionsRoot : SessionRoot,
        Id.ToString("N"));
}

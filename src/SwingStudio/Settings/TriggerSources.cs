namespace SwingStudio.Settings;

public static class TriggerSources
{
    public const string Microphone = "Microphone";

    public const string LaunchMonitor = "LaunchMonitor";

    public static string Normalize(string? source) =>
        string.Equals(source, LaunchMonitor, StringComparison.Ordinal) ? LaunchMonitor : Microphone;

    public static bool IsLaunchMonitor(string? source) => Normalize(source) == LaunchMonitor;
}

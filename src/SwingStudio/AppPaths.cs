using System.IO;

namespace SwingStudio;

public static class AppPaths
{
    public static string SettingsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SwingStudio");

    public static string SettingsFile { get; } = Path.Combine(SettingsDirectory, "settings.json");

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SwingStudio",
        "logs");

    public static string SessionsRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "SwingStudio",
        "Sessions");

    public static string SessionFolder(Guid id) => Path.Combine(SessionsRoot, id.ToString("N"));
}

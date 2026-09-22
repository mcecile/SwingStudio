using System.Globalization;
using System.IO;
using System.Text.Json;

namespace SwingStudio.Session;

public sealed class SwingEntry
{
    public required string FolderPath { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public string? Name { get; init; }

    public bool Saved { get; init; }

    public string Label { get; set; } = "";
}

public static class SwingCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static IReadOnlyList<SwingEntry> List(string? sessionRoot)
    {
        if (string.IsNullOrWhiteSpace(sessionRoot) || !Directory.Exists(sessionRoot))
        {
            return [];
        }

        var entries = new List<SwingEntry>();
        foreach (var folder in Directory.EnumerateDirectories(sessionRoot))
        {
            var session = ReadSession(folder);
            if (session is null)
            {
                continue;
            }

            entries.Add(new SwingEntry
            {
                FolderPath = folder,
                StartedAt = session.StartedAt,
                Name = session.Name,
                Saved = session.Saved,
                Label = LabelFor(session)
            });
        }

        return entries.OrderByDescending(entry => entry.StartedAt).ToList();
    }

    public static void Trim(string? sessionRoot, int keep, params string?[] protectedFolders)
    {
        var unsaved = List(sessionRoot).Where(entry => !entry.Saved).OrderBy(entry => entry.StartedAt).ToList();
        var overflow = unsaved.Count - Math.Clamp(keep, 1, 50);
        foreach (var entry in unsaved)
        {
            if (overflow <= 0)
            {
                break;
            }

            if (protectedFolders.Any(folder => SameFolder(entry.FolderPath, folder)))
            {
                continue;
            }

            try
            {
                Directory.Delete(entry.FolderPath, true);
                overflow--;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public static void SaveName(string folder, string name)
    {
        var session = ReadSession(folder) ?? throw new IOException("This swing could not be read.");
        session.Name = name.Trim();
        session.Saved = true;
        WriteSession(folder, session);
    }

    public static void SaveDrawings(string folder, IReadOnlyList<SwingStroke> drawings)
    {
        var session = ReadSession(folder) ?? throw new IOException("This swing could not be read.");
        session.Drawings = drawings.ToList();
        WriteSession(folder, session);
    }

    public static SwingSession? ReadSession(string folder)
    {
        var manifest = Path.Combine(folder, SwingSession.ManifestFileName);
        if (!File.Exists(manifest))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SwingSession>(File.ReadAllText(manifest), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static bool SameFolder(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteSession(string folder, SwingSession session)
    {
        var manifest = Path.Combine(folder, SwingSession.ManifestFileName);
        File.WriteAllText(manifest, JsonSerializer.Serialize(session, JsonOptions));
    }

    private static string LabelFor(SwingSession session)
    {
        if (session.Saved && !string.IsNullOrWhiteSpace(session.Name))
        {
            return session.Name.Trim();
        }

        return session.StartedAt.ToLocalTime().ToString("h:mm:ss tt", CultureInfo.CurrentCulture);
    }
}

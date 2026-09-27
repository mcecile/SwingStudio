using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SwingStudio.Capture;

internal sealed record CameraOccupant(int ProcessId, string Name, string? FileName);

internal static class CameraOccupants
{
    private const int ErrorMoreData = 234;
    private const uint RebootReasonNone = 0;

    public static IReadOnlyList<CameraOccupant> Find(string? devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
        {
            return [];
        }

        var found = new Dictionary<int, CameraOccupant>();
        foreach (var path in CandidatePaths(devicePath))
        {
            foreach (var occupant in QueryRestartManager(path))
            {
                if (occupant.ProcessId == Environment.ProcessId)
                {
                    continue;
                }

                found[occupant.ProcessId] = occupant;
            }
        }

        return [.. found.Values];
    }

    private static List<string> CandidatePaths(string devicePath)
    {
        var paths = new List<string>();
        Add(paths, devicePath);
        var trimmed = devicePath.TrimEnd('\\');
        if (trimmed.EndsWith(@"\global", StringComparison.OrdinalIgnoreCase))
        {
            Add(paths, trimmed[..^7]);
        }

        if (TryInstanceId(devicePath, out var instanceId))
        {
            Add(paths, instanceId);
            Add(paths, @"\\?\" + instanceId.Replace('\\', '#'));
        }

        return paths;
    }

    private static void Add(List<string> paths, string path)
    {
        if (!paths.Exists(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase)))
        {
            paths.Add(path);
        }
    }

    private static bool TryInstanceId(string devicePath, out string instanceId)
    {
        instanceId = "";
        var start = devicePath.IndexOf("usb#", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return false;
        }

        var end = devicePath.IndexOf("#{", start, StringComparison.OrdinalIgnoreCase);
        if (end < 0)
        {
            end = devicePath.Length;
        }

        instanceId = devicePath[start..end].Replace('#', '\\');
        return instanceId.Contains('\\', StringComparison.Ordinal);
    }

    private static List<CameraOccupant> QueryRestartManager(string path)
    {
        var occupants = new List<CameraOccupant>();
        var key = Guid.NewGuid().ToString();
        if (RmStartSession(out var session, 0, key) != 0)
        {
            return occupants;
        }

        try
        {
            if (RmRegisterResources(session, 1, [path], 0, null, 0, null) != 0)
            {
                return occupants;
            }

            uint needed = 0;
            uint count = 0;
            uint reboot = RebootReasonNone;
            var result = RmGetList(session, out needed, ref count, null, ref reboot);
            if (result != 0 && result != ErrorMoreData)
            {
                return occupants;
            }

            if (needed == 0)
            {
                return occupants;
            }

            var info = new RmProcessInfo[needed];
            count = needed;
            if (RmGetList(session, out needed, ref count, info, ref reboot) != 0)
            {
                return occupants;
            }

            for (var index = 0; index < count; index++)
            {
                var processId = info[index].Process.ProcessId;
                var name = info[index].AppName;
                string? fileName = null;
                try
                {
                    using var process = Process.GetProcessById(processId);
                    fileName = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        name = process.ProcessName;
                    }
                }
                catch (Exception)
                {
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        name = "Unknown";
                    }
                }

                occupants.Add(new CameraOccupant(processId, name, fileName));
            }
        }
        finally
        {
            RmEndSession(session);
        }

        return occupants;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, int flags, string key);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint session,
        uint fileCount,
        string[]? files,
        uint processCount,
        RmUniqueProcess[]? processes,
        uint serviceCount,
        string[]? services);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmGetList(
        uint session,
        out uint needed,
        ref uint count,
        [In, Out] RmProcessInfo[]? apps,
        ref uint rebootReasons);

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string AppName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string ServiceShortName;

        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool Restartable;
    }
}

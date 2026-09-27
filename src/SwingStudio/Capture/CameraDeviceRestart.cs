using System.Runtime.InteropServices;

namespace SwingStudio.Capture;

internal static class CameraDeviceRestart
{
    private const int DeviceIdLength = 200;
    private const uint DisableUiNotOk = 0x4;

    public static string? Restart(string devicePath)
    {
        if (!TryInstanceId(devicePath, out var instanceId))
        {
            Log.Warn($"The camera device path could not be restarted ({devicePath}).");
            return null;
        }

        var result = CM_Locate_DevNode(out var node, instanceId, 0);
        if (result != 0)
        {
            Log.Warn($"Camera {instanceId} was not found ({result}).");
            return null;
        }

        var target = node;
        if (CM_Get_Parent(out var parent, node, 0) == 0)
        {
            target = parent;
        }

        var id = DeviceId(target) ?? instanceId;
        result = CM_Disable_DevNode(target, DisableUiNotOk);
        if (result != 0)
        {
            Log.Warn($"Camera {id} could not be stopped ({result}).");
            return null;
        }

        result = CM_Enable_DevNode(target, 0);
        if (result != 0)
        {
            Log.Error($"Camera {id} could not be started again ({result}).");
            return null;
        }

        Log.Info($"Restarted camera {id}.");
        return id;
    }

    private static string? DeviceId(uint node)
    {
        var buffer = new char[DeviceIdLength];
        if (CM_Get_Device_ID(node, buffer, buffer.Length, 0) != 0)
        {
            return null;
        }

        var end = Array.IndexOf(buffer, '\0');
        return end < 0 ? new string(buffer) : new string(buffer, 0, end);
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

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNode(out uint node, string instanceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(out uint parent, uint node, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID(uint node, char[] buffer, int length, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Disable_DevNode(uint node, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Enable_DevNode(uint node, uint flags);
}

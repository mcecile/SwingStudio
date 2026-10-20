using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;

namespace SwingStudio.Capture;

public enum LaunchMonitorWatchState
{
    Watching,
    NpcapMissing,
    AdapterNotFound,
    OpenFailed
}

/// <summary>Watches the ProTee VX Labs TCP session for the first shotTrigger packet of a burst.</summary>
public sealed class LaunchMonitorWatcher : IDisposable
{
    private const int BurstGapMs = 500;
    private const string Filter = "tcp port 5566 and src host 169.254.254.200";

    private readonly object _gate = new();
    private LibPcapLiveDevice? _device;
    private Action? _onShot;
    private long _ignoreUntil;

    public LaunchMonitorWatchState Start(Action onShot)
    {
        Stop();
        _onShot = onShot;
        LibPcapLiveDeviceList devices;
        try
        {
            devices = LibPcapLiveDeviceList.Instance;
        }
        catch (Exception ex) when (IsNpcapMissing(ex))
        {
            Log.Error("Npcap is not installed.", ex);
            return LaunchMonitorWatchState.NpcapMissing;
        }

        var device = FindLinkLocal(devices);
        if (device is null)
        {
            Log.Warn("ProTee VX Labs adapter not found. No wired adapter has a 169.254 address.");
            return LaunchMonitorWatchState.AdapterNotFound;
        }

        try
        {
            device.Open(new DeviceConfiguration
            {
                Mode = DeviceModes.None,
                ReadTimeout = 1000
            });
            device.Filter = Filter;
            device.OnPacketArrival += OnPacket;
            device.StartCapture();
        }
        catch (Exception ex)
        {
            Log.Error($"Could not open the ProTee VX Labs adapter ({device.Description}).", ex);
            try
            {
                device.Close();
            }
            catch (Exception closeEx)
            {
                Log.Warn("The ProTee VX Labs adapter could not be closed after a failed open.", closeEx);
            }

            return LaunchMonitorWatchState.OpenFailed;
        }

        _device = device;
        Log.Info($"Watching ProTee VX Labs on {device.Description}.");
        return LaunchMonitorWatchState.Watching;
    }

    public void Stop()
    {
        var device = _device;
        _device = null;
        _onShot = null;
        if (device is null)
        {
            return;
        }

        try
        {
            device.OnPacketArrival -= OnPacket;
            if (device.Started)
            {
                device.StopCapture();
            }

            device.Close();
        }
        catch (Exception ex)
        {
            Log.Warn("The ProTee VX Labs watcher could not be stopped.", ex);
        }
    }

    public void Dispose() => Stop();

    private void OnPacket(object sender, PacketCapture capture)
    {
        var onShot = _onShot;
        if (onShot is null)
        {
            return;
        }

        byte[] payload;
        try
        {
            var raw = capture.GetPacket();
            var packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
            payload = packet.Extract<TcpPacket>()?.PayloadData ?? [];
        }
        catch (Exception ex)
        {
            Log.Warn("A ProTee VX Labs packet could not be read.", ex);
            return;
        }

        if (!ShotTrigger.Contains(payload))
        {
            return;
        }

        var now = Environment.TickCount64;
        lock (_gate)
        {
            if (now < _ignoreUntil)
            {
                return;
            }

            _ignoreUntil = now + BurstGapMs;
        }

        onShot();
    }

    /// <summary>True when the adapter being watched is no longer the best ProTee candidate, or nothing is being watched.</summary>
    public bool NeedsAdapterChange()
    {
        var device = _device;
        var nic = FindAdapter();
        if (device is null)
        {
            return nic is not null;
        }

        return nic is null || !IsSameAdapter(device, nic);
    }

    private static LibPcapLiveDevice? FindLinkLocal(LibPcapLiveDeviceList devices)
    {
        var nic = FindAdapter();
        return nic is null ? null : devices.FirstOrDefault(device => IsSameAdapter(device, nic));
    }

    private static NetworkInterface? FindAdapter() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(item => item.OperationalStatus == OperationalStatus.Up)
            .Where(IsCandidate)
            .Where(HasLinkLocal)
            .OrderBy(item => item.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet ? 0 : 1)
            .FirstOrDefault();

    // A disconnected Bluetooth PAN adapter gives itself a 169.254 address and reports itself as Ethernet.
    private static readonly string[] ExcludedAdapterWords = ["Bluetooth", "Virtual", "Hyper-V", "VMware", "VirtualBox", "Loopback", "TAP-", "VPN", "WSL", "Wintun", "WireGuard"];

    private static bool IsCandidate(NetworkInterface nic)
    {
        if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
        {
            return false;
        }

        return !ExcludedAdapterWords.Any(word =>
            nic.Description.Contains(word, StringComparison.OrdinalIgnoreCase)
            || nic.Name.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSameAdapter(LibPcapLiveDevice device, NetworkInterface nic)
    {
        var mac = nic.GetPhysicalAddress();
        if (mac.GetAddressBytes().Length > 0 && device.MacAddress is not null && device.MacAddress.Equals(mac))
        {
            return true;
        }

        return device.Name.Contains(nic.Id, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasLinkLocal(NetworkInterface nic) =>
        nic.GetIPProperties().UnicastAddresses.Any(address => IsLinkLocal(address.Address));

    private static bool IsLinkLocal(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork
        && address.GetAddressBytes() is [169, 254, _, _];

    private static bool IsNpcapMissing(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is DllNotFoundException or TypeInitializationException)
            {
                return true;
            }

            if (current.Message.Contains("Npcap", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("wpcap", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

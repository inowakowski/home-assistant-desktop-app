using System.Net.NetworkInformation;
using HADA.Core.Abstractions;
using HADA.Core.Unix;

namespace HADA.Platform.MacOS;

/// <summary>The lid and the graphics processor, as the I/O Registry has them.</summary>
/// <param name="run">Runs a tool; replaced in tests.</param>
public sealed class MacHardware(Func<string, string[], ToolResult?>? run = null) : IHardwareReadings
{
    private readonly Func<string, string[], ToolResult?> _run = run ?? Tools.Run;

    public bool? ReadLidOpen() =>
        _run("ioreg", ["-r", "-k", "AppleClamshellState", "-d", "4"]) is { Succeeded: true } result ? MacParsers.ParseLidOpen(result.Output) : null;

    /// <summary>Apple's own graphics, AMD's and Intel's all say how busy they are; a Mac that does not, says so at once.</summary>
    public bool HasGpuLoad => ReadGpuLoadPercent() is not null;

    public int? ReadGpuLoadPercent() =>
        _run("ioreg", ["-r", "-d", "1", "-w", "0", "-c", "IOAccelerator"]) is { Succeeded: true } result ? MacParsers.ParseGpuLoad(result.Output) : null;
}

/// <summary>A connected USB device: what to call it, and the id a sensor watches it by.</summary>
/// <param name="MatchId">Vendor and product the way Windows writes them, <c>VID_0BDA&amp;PID_8153</c>, so a sensor set up on one system finds the device on another.</param>
/// <param name="Id">What <see cref="MacUsbDevices.IsPresent"/> looks in: <c>USB\VID_0BDA&amp;PID_8153\serial</c>, as Windows' instance ids are.</param>
public sealed record MacUsbDevice(string Name, string MatchId, string Id);

/// <summary>The USB devices connected right now, from the I/O Registry's USB plane.</summary>
/// <param name="run">Runs a tool; replaced in tests.</param>
public sealed class MacUsbDevices(Func<string, string[], ToolResult?>? run = null) : IDeviceDirectory
{
    private readonly Func<string, string[], ToolResult?> _run = run ?? Tools.Run;

    public bool IsPresent(string idFragment) =>
        idFragment.Length > 0 && Connected().Any(device => device.Id.Contains(idFragment, StringComparison.OrdinalIgnoreCase));

    /// <summary>Connected USB devices, one entry per model, for picking one to watch.</summary>
    public IReadOnlyList<MacUsbDevice> ConnectedModels() =>
        [.. Connected()
            .DistinctBy(device => device.MatchId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)];

    private IReadOnlyList<MacUsbDevice> Connected() =>
        _run("ioreg", ["-a", "-p", "IOUSB", "-l", "-w", "0"]) is { Succeeded: true } result ? MacParsers.ParseUsbDevices(result.Output) : [];
}

/// <summary>
/// The Mac's network ports and its Wi-Fi network. Recent versions of macOS keep the Wi-Fi network's name to apps
/// that were allowed to know the location; without, the network is reported as connected, with no name.
/// </summary>
/// <param name="run">Runs a tool; replaced in tests.</param>
public sealed class MacNetwork(Func<string, string[], ToolResult?>? run = null)
{
    private readonly Func<string, string[], ToolResult?> _run = run ?? Tools.Run;
    private IReadOnlyList<HardwarePort>? _ports;

    /// <summary>The ports, asked for once: they change only with the hardware.</summary>
    public IReadOnlyList<HardwarePort> Ports => _ports ??=
        _run("networksetup", ["-listallhardwareports"]) is { Succeeded: true } result ? MacParsers.ParseHardwarePorts(result.Output) : [];

    public WifiStatus ReadWifi()
    {
        if (Ports.FirstOrDefault(port => port.IsWifi) is not { Device.Length: > 0 } wifi)
        {
            return new WifiStatus(HasAdapter: false, Ssid: null, SignalPercent: null);
        }

        var (connected, name) = Ssid(wifi.Device);
        return new WifiStatus(HasAdapter: true, name, SignalPercent: null, IsConnectedUnnamed: connected && name is null);
    }

    /// <summary>
    /// The networks connected now: the Wi-Fi network by its name, where macOS tells it, and every other port with
    /// an address by the port's own name, such as <c>Thunderbolt Ethernet</c>.
    /// </summary>
    public IReadOnlyList<string> ConnectedNetworks()
    {
        var names = new List<string>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up
                    || !adapter.GetIPProperties().UnicastAddresses.Any(address => address.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    || Ports.FirstOrDefault(port => port.Device == adapter.Name) is not { Name.Length: > 0 } port)
                {
                    continue;
                }

                if (port.IsWifi)
                {
                    if (Ssid(port.Device).Name is { } ssid)
                    {
                        names.Add(ssid);
                    }
                }
                else
                {
                    names.Add(port.Name);
                }
            }
        }
        catch (NetworkInformationException)
        {
            // Nothing to name.
        }

        return names;
    }

    private (bool Connected, string? Name) Ssid(string device) =>
        _run("ipconfig", ["getsummary", device]) is { Succeeded: true } result ? MacParsers.ParseSsid(result.Output) : (false, null);
}

/// <summary>The networks the Mac is connected to, for the connections that only go out at home.</summary>
public sealed class MacNetworkLocation : INetworkLocation, IDisposable
{
    private readonly MacNetwork _network;

    public MacNetworkLocation(MacNetwork network)
    {
        _network = network;
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
    }

    public event EventHandler? Changed;

    public IReadOnlyList<string> ConnectedNetworks => _network.ConnectedNetworks();

    /// <summary>The names of the networks connected right now, for the window to offer.</summary>
    public static IReadOnlyList<string> Read() => new MacNetwork().ConnectedNetworks();

    public void Dispose() => NetworkChange.NetworkAddressChanged -= OnAddressChanged;

    private void OnAddressChanged(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);
}

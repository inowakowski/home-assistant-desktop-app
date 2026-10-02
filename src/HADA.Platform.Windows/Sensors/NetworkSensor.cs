using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Messaging;
using HADA.Platform.Windows.Interop;
using Microsoft.Extensions.Hosting;

namespace HADA.Platform.Windows.Sensors;

/// <param name="Address">The IPv4 address this computer uses to reach other networks.</param>
/// <param name="InterfaceName">The connection's name in Windows, e.g. <c>Wi-Fi</c> or <c>Ethernet 2</c>.</param>
/// <param name="ConnectionType"><c>ethernet</c>, <c>wifi</c> or <c>other</c>.</param>
/// <param name="MacAddress">The adapter's hardware address as <c>AA:BB:CC:DD:EE:FF</c>, which Wake-on-LAN needs; empty when it has none.</param>
public sealed record NetworkAddressInfo(string Address, string InterfaceName, string ConnectionType, string MacAddress = "");

public static class NetworkAddress
{
    // Any address beyond the local network will do; nothing is sent to it.
    private static readonly IPEndPoint FarAway = new(IPAddress.Parse("192.0.2.1"), 9);

    /// <summary>
    /// The address of the connection Windows routes through, which on a computer with several adapters (a dock,
    /// Wi-Fi, a VPN, virtual switches) is the one that matters. Returns <see langword="null"/> without a network.
    /// </summary>
    public static NetworkAddressInfo? TryRead()
    {
        IPAddress local;
        try
        {
            // Connecting a UDP socket sends nothing; it only makes Windows pick the route, and with it the address.
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(FarAway);
            local = ((IPEndPoint)socket.LocalEndPoint!).Address;
        }
        catch (SocketException)
        {
            return null;
        }

        if (IPAddress.IsLoopback(local) || local.Equals(IPAddress.Any))
        {
            return null;
        }

        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.GetIPProperties().UnicastAddresses.Any(candidate => candidate.Address.Equals(local)))
                {
                    return new NetworkAddressInfo(
                        local.ToString(),
                        adapter.Name,
                        TypeOf(adapter.NetworkInterfaceType),
                        string.Join(':', adapter.GetPhysicalAddress().GetAddressBytes().Select(part => part.ToString("X2", System.Globalization.CultureInfo.InvariantCulture))));
                }
            }
        }
        catch (NetworkInformationException)
        {
            // The address is still worth reporting.
        }

        return new NetworkAddressInfo(local.ToString(), string.Empty, "other");
    }

    private static string TypeOf(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => "ethernet",
        NetworkInterfaceType.Wireless80211 => "wifi",
        _ => "other",
    };
}

/// <param name="HasAdapter">False on a computer without Wi-Fi.</param>
/// <param name="Ssid">The network's name, or <see langword="null"/> when not connected, or when Windows does not tell.</param>
/// <param name="SignalPercent">Signal quality, 0-100.</param>
public readonly record struct WifiStatus(bool HasAdapter, string? Ssid, int? SignalPercent);

/// <summary>Reads the connected Wi-Fi network through the WLAN API.</summary>
public static class WifiNetwork
{
    private const uint ClientVersion = 2;
    private const int CurrentConnection = 7;

    // WLAN_INTERFACE_INFO_LIST: two DWORDs, then per adapter a GUID, a 256-character description and a state.
    private const int InterfaceListHeaderSize = 8;
    private const int InterfaceInfoSize = 16 + 512 + 4;

    // WLAN_CONNECTION_ATTRIBUTES: state and mode (4 bytes each), a 256-character profile name, then the
    // association attributes, which start with the SSID (a length and 32 bytes).
    private const int SsidLengthOffset = 4 + 4 + 512;
    private const int SsidOffset = SsidLengthOffset + 4;
    private const int MaxSsidLength = 32;

    // After the SSID: BSS type (4), BSSID (6, padded to 8), PHY type (4) and PHY index (4).
    private const int SignalQualityOffset = SsidOffset + MaxSsidLength + 4 + 8 + 4 + 4;

    /// <summary>
    /// From Windows 11 24H2 on, Windows hides the network's name from apps without access to location; the
    /// adapter is then reported as present but not connected.
    /// </summary>
    public static WifiStatus TryRead()
    {
        nint client;
        try
        {
            if (NativeMethods.WlanOpenHandle(ClientVersion, 0, out _, out client) != 0)
            {
                return default;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Windows editions without the WLAN service.
            return default;
        }

        try
        {
            if (NativeMethods.WlanEnumInterfaces(client, 0, out var list) != 0 || list == 0)
            {
                return default;
            }

            try
            {
                var count = Marshal.ReadInt32(list);
                for (var i = 0; i < count; i++)
                {
                    var adapterId = Marshal.PtrToStructure<Guid>(list + InterfaceListHeaderSize + (i * InterfaceInfoSize));
                    if (ReadConnection(client, adapterId) is { } connection)
                    {
                        return connection;
                    }
                }

                return new WifiStatus(count > 0, null, null);
            }
            finally
            {
                NativeMethods.WlanFreeMemory(list);
            }
        }
        finally
        {
            _ = NativeMethods.WlanCloseHandle(client, 0);
        }
    }

    private static WifiStatus? ReadConnection(nint client, Guid adapterId)
    {
        // Fails with ERROR_INVALID_STATE while the adapter is not connected.
        if (NativeMethods.WlanQueryInterface(client, adapterId, CurrentConnection, 0, out var size, out var data, 0) != 0 || data == 0)
        {
            return null;
        }

        try
        {
            if (size < SignalQualityOffset + sizeof(int))
            {
                return null;
            }

            var length = Math.Clamp(Marshal.ReadInt32(data, SsidLengthOffset), 0, MaxSsidLength);
            var ssid = new byte[length];
            Marshal.Copy(data + SsidOffset, ssid, 0, length);
            return new WifiStatus(true, Encoding.UTF8.GetString(ssid), Math.Clamp(Marshal.ReadInt32(data, SignalQualityOffset), 0, 100));
        }
        finally
        {
            NativeMethods.WlanFreeMemory(data);
        }
    }
}

/// <summary>
/// Publishes the computer's IP address and, on computers with Wi-Fi, the network it is connected to.
/// Runs in the service, so both are reported with nobody signed in.
/// </summary>
public sealed class NetworkSensor(IEventBus bus, IEntityRegistry registry) : BackgroundService
{
    public const string AddressEntityId = "ip_address";
    public const string WifiEntityId = "wifi_network";

    /// <summary>State of <see cref="WifiEntityId"/> while Wi-Fi is not connected. A state cannot be empty.</summary>
    public const string NotConnected = "not_connected";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await registry.RegisterAsync(
            new EntityDescriptor { Id = AddressEntityId, Name = "IP address", Kind = EntityKind.Sensor, Icon = "mdi:ip-network" },
            stoppingToken);

        // Registered when an adapter is first seen: at boot the WLAN service may start after this one does.
        var hasWifi = false;
        var publisher = new ChangeOnlyPublisher(bus);
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            // Without a network there is no address, and nobody to tell; the last one stays.
            if (NetworkAddress.TryRead() is { } address)
            {
                await publisher.PublishAsync(
                    AddressEntityId,
                    address.Address,
                    new Dictionary<string, object?>
                    {
                        ["interface"] = address.InterfaceName,
                        ["connection_type"] = address.ConnectionType,
                        ["mac_address"] = address.MacAddress,
                    },
                    stoppingToken);
            }

            var wifi = WifiNetwork.TryRead();
            if (wifi.HasAdapter && !hasWifi)
            {
                hasWifi = true;
                await registry.RegisterAsync(
                    new EntityDescriptor { Id = WifiEntityId, Name = "Wi-Fi network", Kind = EntityKind.Sensor, Icon = "mdi:wifi" },
                    stoppingToken);
            }

            if (hasWifi)
            {
                await publisher.PublishAsync(
                    WifiEntityId,
                    string.IsNullOrEmpty(wifi.Ssid) ? NotConnected : wifi.Ssid,
                    new Dictionary<string, object?> { ["signal"] = wifi.SignalPercent },
                    stoppingToken);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

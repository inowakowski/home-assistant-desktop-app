using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Hosting;
using HADA.Core.Messaging;

namespace HADA.Core.Sensors;

/// <param name="Address">The IPv4 address this computer uses to reach other networks.</param>
/// <param name="InterfaceName">The connection's name in the system, e.g. <c>Wi-Fi</c> or <c>Ethernet 2</c> on Windows, <c>wlp2s0</c> on Linux.</param>
/// <param name="ConnectionType"><c>ethernet</c>, <c>wifi</c> or <c>other</c>.</param>
/// <param name="MacAddress">The adapter's hardware address as <c>AA:BB:CC:DD:EE:FF</c>, which Wake-on-LAN needs; empty when it has none.</param>
public sealed record NetworkAddressInfo(string Address, string InterfaceName, string ConnectionType, string MacAddress = "");

public static class NetworkAddress
{
    // Any address beyond the local network will do; nothing is sent to it.
    private static readonly IPEndPoint FarAway = new(IPAddress.Parse("192.0.2.1"), 9);

    /// <summary>
    /// The address of the connection the system routes through, which on a computer with several adapters (a dock,
    /// Wi-Fi, a VPN, virtual switches) is the one that matters. Returns <see langword="null"/> without a network.
    /// </summary>
    public static NetworkAddressInfo? TryRead()
    {
        IPAddress local;
        try
        {
            // Connecting a UDP socket sends nothing; it only makes the system pick the route, and with it the address.
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
                        TypeOf(adapter),
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

    // Linux reports a Wi-Fi adapter as Ethernet; the kernel's wireless folder for it tells them apart.
    private static string TypeOf(NetworkInterface adapter) =>
        OperatingSystem.IsLinux() && Directory.Exists(Path.Combine("/sys/class/net", adapter.Name, "wireless"))
            ? "wifi"
            : TypeOf(adapter.NetworkInterfaceType);

    private static string TypeOf(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => "ethernet",
        NetworkInterfaceType.Wireless80211 => "wifi",
        _ => "other",
    };
}

/// <summary>
/// Publishes the computer's IP address and, on computers with Wi-Fi, the network it is connected to.
/// Runs in the service, so both are reported with nobody signed in.
/// </summary>
public sealed class NetworkSensor(IEventBus bus, IEntityRegistry registry, ISystemReadings system) : EagerBackgroundService
{
    public const string AddressEntityId = BuiltInEntityIds.IpAddress;
    public const string WifiEntityId = BuiltInEntityIds.WifiNetwork;

    /// <summary>State of <see cref="WifiEntityId"/> while Wi-Fi is not connected. A state cannot be empty.</summary>
    public const string NotConnected = "not_connected";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await registry.RegisterAsync(
            new EntityDescriptor { Id = AddressEntityId, Name = "IP address", Kind = EntityKind.Sensor, Icon = "mdi:ip-network" },
            stoppingToken);

        // Registered when an adapter is first seen: at boot the system's Wi-Fi may start after this service does.
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

            var wifi = system.ReadWifi();
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

using System.Diagnostics;
using System.Globalization;
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

/// <summary>What has come in and gone out over a connection since the system started it, in bytes.</summary>
/// <param name="InterfaceName">The connection's name in the system, as in <see cref="NetworkAddressInfo.InterfaceName"/>.</param>
public readonly record struct NetworkTraffic(string InterfaceName, long BytesReceived, long BytesSent);

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
        if (RoutedAddress() is not { } local)
        {
            return null;
        }

        try
        {
            if (AdapterWith(local) is { } adapter)
            {
                return new NetworkAddressInfo(
                    local.ToString(),
                    adapter.Name,
                    TypeOf(adapter),
                    string.Join(':', adapter.GetPhysicalAddress().GetAddressBytes().Select(part => part.ToString("X2", CultureInfo.InvariantCulture))));
            }
        }
        catch (NetworkInformationException)
        {
            // The address is still worth reporting.
        }

        return new NetworkAddressInfo(local.ToString(), string.Empty, "other");
    }

    /// <summary>
    /// What has come in and gone out so far over the connection the system routes through, the one
    /// <see cref="TryRead"/> names. Returns <see langword="null"/> without a network, or where the system does not count it.
    /// </summary>
    public static NetworkTraffic? TryReadTraffic()
    {
        if (RoutedAddress() is not { } local)
        {
            return null;
        }

        try
        {
            if (AdapterWith(local) is not { } adapter)
            {
                return null;
            }

            var statistics = adapter.GetIPStatistics();
            return new NetworkTraffic(adapter.Name, statistics.BytesReceived, statistics.BytesSent);
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static IPAddress? RoutedAddress()
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

        return IPAddress.IsLoopback(local) || local.Equals(IPAddress.Any) ? null : local;
    }

    private static NetworkInterface? AdapterWith(IPAddress local) =>
        NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(adapter => adapter.GetIPProperties().UnicastAddresses.Any(candidate => candidate.Address.Equals(local)));

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

    /// <summary>State of <see cref="WifiEntityId"/> while connected to a network the system does not name.</summary>
    public const string ConnectedUnnamed = "connected";

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
                    !string.IsNullOrEmpty(wifi.Ssid) ? wifi.Ssid : wifi.IsConnectedUnnamed ? ConnectedUnnamed : NotConnected,
                    new Dictionary<string, object?> { ["signal"] = wifi.SignalPercent },
                    stoppingToken);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>
/// Publishes how fast data comes in and goes out over the connection the system routes through, the one
/// <c>ip_address</c> names, in kB/s over the last 10 seconds. Runs in the service.
/// </summary>
/// <param name="read">Reads the connection's counters; <see cref="NetworkAddress.TryReadTraffic"/> unless a test gives another.</param>
public sealed class NetworkTrafficSensor(IEventBus bus, IEntityRegistry registry, Func<NetworkTraffic?>? read = null) : EagerBackgroundService
{
    public const string DownloadEntityId = BuiltInEntityIds.NetworkDownload;
    public const string UploadEntityId = BuiltInEntityIds.NetworkUpload;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private readonly Func<NetworkTraffic?> _read = read ?? NetworkAddress.TryReadTraffic;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Left out where the system has a network but does not count what goes through it.
        var previous = _read();
        if (previous is null && NetworkAddress.TryRead() is not null)
        {
            return;
        }

        await registry.RegisterAsync(Descriptor(DownloadEntityId, "Network download", "mdi:download-network"), stoppingToken);
        await registry.RegisterAsync(Descriptor(UploadEntityId, "Network upload", "mdi:upload-network"), stoppingToken);

        var publisher = new ChangeOnlyPublisher(bus);
        var previousAt = Stopwatch.GetTimestamp();
        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var current = _read();
            var now = Stopwatch.GetTimestamp();
            if (Rates(previous, current, Stopwatch.GetElapsedTime(previousAt, now)) is { } rates)
            {
                var attributes = new Dictionary<string, object?> { ["interface"] = current!.Value.InterfaceName };
                await publisher.PublishAsync(DownloadEntityId, Format(rates.Download), attributes, stoppingToken);
                await publisher.PublishAsync(UploadEntityId, Format(rates.Upload), attributes, stoppingToken);
            }

            previous = current;
            previousAt = now;
        }
    }

    /// <summary>
    /// kB/s in and out between two readings of the same connection; <see langword="null"/> when there is nothing to
    /// compare, as after the computer moved to another connection or its counters started over.
    /// </summary>
    public static (double Download, double Upload)? Rates(NetworkTraffic? previous, NetworkTraffic? current, TimeSpan elapsed)
    {
        if (previous is not { } before || current is not { } after || elapsed <= TimeSpan.Zero
            || before.InterfaceName != after.InterfaceName
            || after.BytesReceived < before.BytesReceived || after.BytesSent < before.BytesSent)
        {
            return null;
        }

        return (
            (after.BytesReceived - before.BytesReceived) / 1000.0 / elapsed.TotalSeconds,
            (after.BytesSent - before.BytesSent) / 1000.0 / elapsed.TotalSeconds);
    }

    private static string Format(double kilobytesPerSecond) =>
        Math.Round(kilobytesPerSecond, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);

    private static EntityDescriptor Descriptor(string id, string name, string icon) => new()
    {
        Id = id,
        Name = name,
        Kind = EntityKind.Sensor,
        DeviceClass = "data_rate",
        UnitOfMeasurement = "kB/s",
        StateClass = "measurement",
        Icon = icon,
    };
}

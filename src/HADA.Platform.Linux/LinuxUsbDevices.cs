using System.Globalization;
using HADA.Core.Abstractions;

namespace HADA.Platform.Linux;

/// <summary>A connected USB device: what to call it, and the id a sensor watches it by.</summary>
/// <param name="MatchId">Vendor and product the way Windows writes them, <c>VID_0BDA&amp;PID_8153</c>, so a sensor set up on either system finds the device on the other.</param>
/// <param name="Id">What <see cref="LinuxUsbDevices.IsPresent"/> looks in: <c>USB\VID_0BDA&amp;PID_8153\serial</c>, as Windows' instance ids are.</param>
public sealed record LinuxUsbDevice(string Name, string MatchId, string Id);

/// <summary>
/// The USB devices connected right now, as the kernel lists them in <c>/sys/bus/usb/devices</c>: a folder for
/// each device, with its vendor and product id, and the names it gives itself.
/// </summary>
/// <param name="root">Where the kernel's folders are; replaced in tests.</param>
public sealed class LinuxUsbDevices(string root = "/") : IDeviceDirectory
{
    // The Linux Foundation's vendor id, which the root hubs of the USB controllers have.
    private const string RootHubVendor = "1d6b";

    public bool IsPresent(string idFragment) =>
        idFragment.Length > 0 && Connected().Any(device => device.Id.Contains(idFragment, StringComparison.OrdinalIgnoreCase));

    /// <summary>Connected USB devices, one entry per model, for picking one to watch; the root hubs are left out.</summary>
    public IReadOnlyList<LinuxUsbDevice> ConnectedModels() =>
        [.. Connected()
            .DistinctBy(device => device.MatchId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)];

    private IEnumerable<LinuxUsbDevice> Connected()
    {
        var folder = Path.Combine(root, "sys", "bus", "usb", "devices");
        if (!Directory.Exists(folder))
        {
            yield break;
        }

        foreach (var device in Directory.EnumerateDirectories(folder))
        {
            // The interfaces of a device, 1-1:1.0, have no ids of their own.
            var vendor = Text(device, "idVendor");
            var product = Text(device, "idProduct");
            if (vendor is not { Length: 4 } || product is not { Length: 4 } || vendor == RootHubVendor)
            {
                continue;
            }

            var matchId = string.Create(CultureInfo.InvariantCulture, $"VID_{vendor.ToUpperInvariant()}&PID_{product.ToUpperInvariant()}");
            var serial = Text(device, "serial");
            var name = Text(device, "product") ?? Text(device, "manufacturer") ?? matchId;
            yield return new LinuxUsbDevice(name, matchId, serial is null ? $"USB\\{matchId}" : $"USB\\{matchId}\\{serial}");
        }
    }

    private static string? Text(string device, string file) =>
        Proc.ReadText(Path.Combine(device, file))?.Trim() is { Length: > 0 } text ? text : null;
}

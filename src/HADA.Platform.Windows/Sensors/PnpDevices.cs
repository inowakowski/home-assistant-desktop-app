using System.Text.RegularExpressions;
using HADA.Platform.Windows.Interop;

namespace HADA.Platform.Windows.Sensors;

/// <param name="MatchId">The part of the instance id that identifies the model, e.g. <c>VID_0BDA&amp;PID_8153</c>.</param>
public sealed record UsbDeviceInfo(string Name, string MatchId, string InstanceId);

/// <summary>
/// The devices Windows currently sees: a USB-C hub or dock, a mouse, a monitor. Works in a service, and does not
/// depend on whether a connected monitor has power, which makes a dock's own devices the reliable sign of "docked".
/// </summary>
public static partial class PnpDevices
{
    private const uint Success = 0;
    private const uint BufferTooSmall = 0x1A;
    private const uint FilterEnumerator = 0x1;
    private const uint FilterPresent = 0x100;
    private const uint StringProperty = 0x12;

    /// <summary>DEVPKEY_NAME: the name Device Manager shows.</summary>
    private static readonly DevicePropertyKey NameKey = new() { Category = new Guid("b725f130-47ef-101a-a5f1-02608c9eebac"), Id = 10 };

    /// <summary>
    /// Instance ids of every device that is connected right now, e.g. <c>USB\VID_0BDA&amp;PID_8153\000001</c>.
    /// </summary>
    /// <param name="enumerator">Limits the list to one bus, e.g. <c>USB</c>.</param>
    public static unsafe IReadOnlyList<string> PresentInstanceIds(string? enumerator = null)
    {
        var flags = FilterPresent | (enumerator is null ? 0 : FilterEnumerator);

        // Devices can come and go between asking for the size and fetching the list, so allow a few attempts.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (NativeMethods.CM_Get_Device_ID_List_Size(out var length, enumerator, flags) != Success || length == 0)
            {
                return [];
            }

            var buffer = new char[length];
            uint result;
            fixed (char* chars = buffer)
            {
                result = NativeMethods.CM_Get_Device_ID_List(enumerator, chars, length, flags);
            }

            if (result == Success)
            {
                return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            }

            if (result != BufferTooSmall)
            {
                return [];
            }
        }

        return [];
    }

    /// <summary>Whether a connected device's instance id contains <paramref name="idFragment"/>, ignoring case.</summary>
    public static bool IsPresent(string idFragment) =>
        idFragment.Length > 0
        && PresentInstanceIds().Any(id => id.Contains(idFragment, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Connected USB devices with a vendor and product id, one entry per model, for picking one to watch.
    /// Root hubs and the functions of a composite device (<c>&amp;MI_00</c>) are left out.
    /// </summary>
    public static IReadOnlyList<UsbDeviceInfo> ConnectedUsbDevices()
    {
        var devices = new Dictionary<string, UsbDeviceInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var instanceId in PresentInstanceIds("USB"))
        {
            var match = VendorAndProduct().Match(instanceId);
            if (!match.Success || instanceId.Contains("&MI_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var matchId = match.Value.ToUpperInvariant();
            devices.TryAdd(matchId, new UsbDeviceInfo(TryGetName(instanceId) ?? matchId, matchId, instanceId));
        }

        return [.. devices.Values.OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    private static unsafe string? TryGetName(string instanceId)
    {
        if (NativeMethods.CM_Locate_DevNode(out var device, instanceId, 0) != Success)
        {
            return null;
        }

        var buffer = stackalloc byte[512];
        var size = 512u;
        if (NativeMethods.CM_Get_DevNode_Property(device, in NameKey, out var type, buffer, ref size, 0) != Success
            || type != StringProperty
            || size < sizeof(char))
        {
            return null;
        }

        var name = new string((char*)buffer, 0, (int)(size / sizeof(char))).TrimEnd('\0').Trim();
        return name.Length > 0 ? name : null;
    }

    [GeneratedRegex(@"VID_[0-9A-F]{4}&PID_[0-9A-F]{4}", RegexOptions.IgnoreCase)]
    private static partial Regex VendorAndProduct();
}

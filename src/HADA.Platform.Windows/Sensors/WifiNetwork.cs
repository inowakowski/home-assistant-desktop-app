using System.Runtime.InteropServices;
using System.Text;
using HADA.Core.Abstractions;
using HADA.Platform.Windows.Interop;

namespace HADA.Platform.Windows.Sensors;

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

using Microsoft.Win32;

namespace HADA.Platform.Windows;

/// <summary>Which kind of Windows this is.</summary>
public static class WindowsEdition
{
    private static readonly Lazy<bool> Server = new(() => IsServerProductType(ReadProductType()));

    /// <summary>
    /// Whether this is Windows Server, which nobody uses as a desktop to be kept quiet; setting the quiet hours there
    /// even crashes the service behind them (QuietHours.dll in WpnUserService, which also shows the notifications).
    /// </summary>
    public static bool IsServer => Server.Value;

    /// <summary>
    /// The product type of the registry: "WinNT" on a workstation, "ServerNT" on a server and "LanmanNT" on a domain
    /// controller. Anything else, or nothing, is not told apart from a workstation.
    /// </summary>
    public static bool IsServerProductType(string? productType) =>
        string.Equals(productType, "ServerNT", StringComparison.OrdinalIgnoreCase)
        || string.Equals(productType, "LanmanNT", StringComparison.OrdinalIgnoreCase);

    private static string? ReadProductType()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\ProductOptions");
            return key?.GetValue("ProductType") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using HADA.Platform.Windows.Network;
using HADA.Platform.Windows.Sensors;
using HADA.Presentation;
using HADA.Presentation.ViewModels;

namespace HADA.Tray;

/// <summary>What the window's view models get from Windows and WPF.</summary>
internal sealed class WindowsDesktop : IDesktop
{
    public bool StartsWithSystem
    {
        get => Autostart.IsEnabled;
        set => Autostart.IsEnabled = value;
    }

    /// <summary>
    /// Opens an address in the user's default browser. From the administrator copy of the window that is left to
    /// the desktop's own Explorer, so that the browser runs as the signed-in user, where their Home Assistant
    /// session is, and not with an administrator's rights.
    /// </summary>
    public static void Open(Uri address) => Open(address, Elevation.IsElevated);

    /// <param name="throughExplorer">
    /// Whether Explorer is asked to open it. It takes plain addresses only: one with a query string it does not
    /// hand to a browser, and opens a window of its own instead.
    /// </param>
    public static void Open(Uri address, bool throughExplorer)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps)
        {
            return;
        }

        if (throughExplorer && address.Query.Length == 0)
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
            {
                UseShellExecute = false,
            };
            start.ArgumentList.Add(address.AbsoluteUri);
            using var explorer = Process.Start(start);
            return;
        }

        using var process = Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true });
    }

    public void OpenInBrowser(Uri address) => Open(address);

    public void CopyText(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch (COMException)
        {
            // Another application is holding the clipboard; the user can simply try again.
        }
    }

    public void StartInstaller(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();

    public IReadOnlyList<InstalledBrowser> InstalledBrowsers() => Browsers.Installed();

    public IReadOnlyList<UsbDeviceOption> ConnectedUsbDevices() =>
        [.. PnpDevices.ConnectedUsbDevices().Select(device => new UsbDeviceOption(device.MatchId, device.Name))];

    public IReadOnlyList<string> ConnectedNetworks() => WindowsNetworkLocation.Read();
}

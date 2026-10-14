using System.Diagnostics;
using System.Runtime.InteropServices;
using HADA.Core;
using HADA.Platform.Windows.Desktop;
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

    public void OpenInBrowser(Uri address) => Shell.OpenInBrowser(address);

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

using System.Diagnostics;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using HADA.Core;
using HADA.Platform.Linux;
using HADA.Platform.Windows.Desktop;
using HADA.Platform.Windows.Network;
using HADA.Platform.Windows.Sensors;
using HADA.Presentation;
using HADA.Presentation.ViewModels;

namespace HADA.App;

/// <summary>
/// What the window's view models get from the system and from Avalonia. Windows has all of it; on other systems
/// what HADA cannot do there yet answers as if there were nothing.
/// </summary>
internal sealed class AppDesktop(TopLevel window) : IDesktop
{
    public bool StartsWithSystem
    {
        get => OperatingSystem.IsWindows() ? Autostart.IsEnabled : OperatingSystem.IsLinux() && LinuxAutostart.IsEnabled;
        set
        {
            if (OperatingSystem.IsWindows())
            {
                Autostart.IsEnabled = value;
            }
            else if (OperatingSystem.IsLinux())
            {
                LinuxAutostart.IsEnabled = value;
            }
        }
    }

    public void CopyText(string text) => _ = window.Clipboard?.SetTextAsync(text);

    public void OpenInBrowser(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (OperatingSystem.IsWindows())
        {
            Shell.OpenInBrowser(address);
        }
        else if (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps)
        {
            using var browser = Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true });
        }
    }

    public void StartInstaller(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();

    public IReadOnlyList<InstalledBrowser> InstalledBrowsers() => OperatingSystem.IsWindows() ? Browsers.Installed() : [];

    public IReadOnlyList<UsbDeviceOption> ConnectedUsbDevices() => OperatingSystem.IsWindows() ? ConnectedWindowsDevices() : [];

    public IReadOnlyList<string> ConnectedNetworks() =>
        OperatingSystem.IsWindows() ? WindowsNetworkLocation.Read() : OperatingSystem.IsLinux() ? LinuxNetworkLocation.Read() : [];

    [SupportedOSPlatform("windows")]
    private static UsbDeviceOption[] ConnectedWindowsDevices() =>
        [.. PnpDevices.ConnectedUsbDevices().Select(device => new UsbDeviceOption(device.MatchId, device.Name))];
}

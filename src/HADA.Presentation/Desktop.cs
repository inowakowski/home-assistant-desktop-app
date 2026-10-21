using HADA.Core;
using HADA.Presentation.ViewModels;

namespace HADA.Presentation;

/// <summary>
/// What the window's view models need from the system and from the toolkit that shows them. The Windows tray app
/// provides it; any other app that shows these view models provides its own.
/// </summary>
public interface IDesktop
{
    /// <summary>Puts text on the clipboard. Does nothing when another program holds the clipboard; the user can try again.</summary>
    void CopyText(string text);

    /// <summary>Opens an <c>http</c> or <c>https</c> address in the user's browser, running as the signed-in user.</summary>
    void OpenInBrowser(Uri address);

    /// <summary>Starts the Windows installer that was downloaded and checked. Other systems install their packages by hand.</summary>
    void StartInstaller(string path);

    /// <summary>Whether HADA starts when the user signs in.</summary>
    bool StartsWithSystem { get; set; }

    /// <summary>The web browsers on this computer, sorted by name, for opening the addresses of notifications in one of them.</summary>
    IReadOnlyList<InstalledBrowser> InstalledBrowsers();

    /// <summary>The USB devices connected right now, one entry per model, for picking one to watch.</summary>
    IReadOnlyList<UsbDeviceOption> ConnectedUsbDevices();

    /// <summary>The names of the networks connected to right now; empty when the system does not tell.</summary>
    IReadOnlyList<string> ConnectedNetworks();
}

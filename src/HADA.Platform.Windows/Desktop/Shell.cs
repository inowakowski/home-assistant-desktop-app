using System.Diagnostics;

namespace HADA.Platform.Windows.Desktop;

/// <summary>Starting things on the desktop of the signed-in user.</summary>
public static class Shell
{
    /// <summary>
    /// Opens an address in the user's default browser. From the administrator copy of the window that is left to
    /// the desktop's own Explorer, so that the browser runs as the signed-in user, where their Home Assistant
    /// session is, and not with an administrator's rights.
    /// </summary>
    public static void OpenInBrowser(Uri address) => OpenInBrowser(address, Elevation.IsElevated);

    /// <param name="throughExplorer">
    /// Whether Explorer is asked to open it. It takes plain addresses only: one with a query string it does not
    /// hand to a browser, and opens a window of its own instead.
    /// </param>
    public static void OpenInBrowser(Uri address, bool throughExplorer)
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
}

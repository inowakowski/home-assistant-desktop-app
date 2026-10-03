using System.Diagnostics;
using System.IO;
using System.Security;
using HADA.Core.Entities;
using Microsoft.Win32;

namespace HADA.Tray;

/// <param name="Name">As the browser calls itself, e.g. "Firefox".</param>
/// <param name="Path">Its program file.</param>
internal sealed record InstalledBrowser(string Name, string Path);

/// <summary>
/// The web browsers installed on this computer, as they registered themselves with Windows, and opening an
/// address in one of them rather than in whichever is the default.
/// </summary>
internal static class Browsers
{
    private const string ClientsKey = @"SOFTWARE\Clients\StartMenuInternet";

    /// <summary>Sorted by name; one program is listed once, however many times it registered.</summary>
    public static IReadOnlyList<InstalledBrowser> Installed()
    {
        var browsers = new Dictionary<string, InstalledBrowser>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in (RegistryHive[])[RegistryHive.CurrentUser, RegistryHive.LocalMachine])
        {
            foreach (var view in (RegistryView[])[RegistryView.Registry64, RegistryView.Registry32])
            {
                try
                {
                    using var root = RegistryKey.OpenBaseKey(hive, view);
                    using var clients = root.OpenSubKey(ClientsKey);
                    foreach (var id in clients?.GetSubKeyNames() ?? [])
                    {
                        using var client = clients!.OpenSubKey(id);
                        using var command = client?.OpenSubKey(@"shell\open\command");
                        // Internet Explorer still registers itself, and only hands the address on to Edge.
                        if (ProgramOf(command?.GetValue(null) as string) is { } path
                            && File.Exists(path)
                            && !string.Equals(System.IO.Path.GetFileName(path), "iexplore.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            var name = client!.GetValue(null) as string;
                            browsers.TryAdd(path, new InstalledBrowser(string.IsNullOrWhiteSpace(name) ? id : name.Trim(), path));
                        }
                    }
                }
                catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
                {
                    // What cannot be read is not offered.
                }
            }
        }

        return [.. browsers.Values.OrderBy(browser => browser.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>
    /// Opens an <c>http</c> or <c>https</c> address in the browser whose program file is <paramref name="browserPath"/>,
    /// if that is one of the installed browsers; in the default browser otherwise, or when that one does not start.
    /// Anything that is not such an address is not opened at all.
    /// </summary>
    public static void Open(string address, string? browserPath)
    {
        if (NotificationContent.ToHttpUrl(address) is not { } url)
        {
            return;
        }

        try
        {
            // Only a browser Windows knows as one: the preference is a path, and must not come to start anything else.
            if (browserPath is { Length: > 0 }
                && Installed().FirstOrDefault(browser => string.Equals(browser.Path, browserPath, StringComparison.OrdinalIgnoreCase)) is { } browser)
            {
                var start = new ProcessStartInfo(browser.Path) { UseShellExecute = false };

                // As one argument, however the address reads.
                start.ArgumentList.Add(url);
                using var process = Process.Start(start);
                return;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // Fall through to the default browser.
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No browser at all; there is nothing to open the address with.
        }
    }

    /// <summary>The program of a command line such as <c>"C:\…\firefox.exe" -osint -url "%1"</c>.</summary>
    internal static string? ProgramOf(string? commandLine)
    {
        var command = commandLine?.Trim();
        if (string.IsNullOrEmpty(command))
        {
            return null;
        }

        if (command[0] == '"')
        {
            var end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }

        // Unquoted: up to and including ".exe", as the path itself may contain spaces.
        var exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? command[..(exe + 4)] : null;
    }
}

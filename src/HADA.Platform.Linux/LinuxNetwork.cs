using System.Globalization;
using System.Net.NetworkInformation;
using System.Text;
using HADA.Core.Abstractions;

namespace HADA.Platform.Linux;

/// <summary>
/// Wi-Fi and the networks the computer is connected to, as NetworkManager tells them, which GNOME and KDE both use.
/// Without it, the kernel says whether there is a Wi-Fi adapter, and <c>iw</c> which network it is on.
/// </summary>
/// <param name="run">Runs a tool; replaced in tests.</param>
/// <param name="sysNet">The kernel's folder of network interfaces; replaced in tests.</param>
public sealed class LinuxNetwork(Func<string, string[], ToolResult?>? run = null, string sysNet = "/sys/class/net")
{
    private readonly Func<string, string[], ToolResult?> _run = run ?? Tools.Run;

    public WifiStatus ReadWifi()
    {
        if (_run("nmcli", ["-t", "-f", "TYPE", "device"]) is { Succeeded: true } devices)
        {
            if (!devices.Output.Split('\n', StringSplitOptions.TrimEntries).Contains("wifi"))
            {
                return default;
            }

            return _run("nmcli", ["-t", "-f", "ACTIVE,SSID,SIGNAL", "device", "wifi", "list", "--rescan", "no"]) is { Succeeded: true } list
                ? ParseWifiList(list.Output)
                : new WifiStatus(true, null, null);
        }

        var adapters = WirelessInterfaces();
        foreach (var adapter in adapters)
        {
            if (_run("iw", ["dev", adapter, "link"]) is { Succeeded: true } link && ParseIwLink(link.Output) is { } connected)
            {
                return connected;
            }
        }

        return new WifiStatus(adapters.Count > 0, null, null);
    }

    /// <summary>
    /// The connected networks by the names NetworkManager gives its connections: a Wi-Fi network's own name, and
    /// "Wired connection 1" and so on for others. Without NetworkManager, the Wi-Fi network's name alone.
    /// </summary>
    public IReadOnlyList<string> ConnectedNetworks()
    {
        if (_run("nmcli", ["-t", "-f", "NAME,TYPE", "connection", "show", "--active"]) is { Succeeded: true } active)
        {
            return ParseActiveConnections(active.Output);
        }

        return ReadWifi() is { Ssid: { Length: > 0 } ssid } ? [ssid] : [];
    }

    /// <summary>
    /// Reads <c>nmcli -t -f ACTIVE,SSID,SIGNAL device wifi list</c>: the line of the network connected to starts
    /// with <c>yes</c>.
    /// </summary>
    public static WifiStatus ParseWifiList(string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (SplitTerse(line) is ["yes", var ssid, var signal])
            {
                return new WifiStatus(
                    true,
                    ssid.Length > 0 ? ssid : null,
                    int.TryParse(signal, NumberStyles.None, CultureInfo.InvariantCulture, out var percent) ? Math.Clamp(percent, 0, 100) : null);
            }
        }

        return new WifiStatus(true, null, null);
    }

    /// <summary>Reads <c>nmcli -t -f NAME,TYPE connection show --active</c>, leaving out the computer's own loopback.</summary>
    public static IReadOnlyList<string> ParseActiveConnections(string output)
    {
        var names = new List<string>();
        foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (SplitTerse(line) is [var name, var type] && type != "loopback" && name.Length > 0 && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>Reads <c>iw dev wlan0 link</c>: <c>SSID: Home</c> and <c>signal: -55 dBm</c>, or <c>Not connected.</c></summary>
    public static WifiStatus? ParseIwLink(string output)
    {
        string? ssid = null;
        int? signal = null;
        foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("SSID: ", StringComparison.Ordinal))
            {
                ssid = line["SSID: ".Length..];
            }
            else if (line.StartsWith("signal: ", StringComparison.Ordinal)
                && int.TryParse(line["signal: ".Length..].Split(' ')[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var dbm))
            {
                // As NetworkManager reckons quality: -100 dBm is nothing, -50 dBm and better is all.
                signal = Math.Clamp(2 * (dbm + 100), 0, 100);
            }
        }

        return ssid is null ? null : new WifiStatus(true, ssid, signal);
    }

    /// <summary>
    /// The fields of a line of <c>nmcli -t</c>, which separates them with colons and escapes a colon or a backslash
    /// within one with a backslash.
    /// </summary>
    public static IReadOnlyList<string> SplitTerse(string line)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '\\' && index + 1 < line.Length)
            {
                field.Append(line[++index]);
            }
            else if (character == ':')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(character);
            }
        }

        fields.Add(field.ToString());
        return fields;
    }

    private List<string> WirelessInterfaces() =>
        Directory.Exists(sysNet)
            ? [.. Directory.EnumerateDirectories(sysNet).Where(adapter => Directory.Exists(Path.Combine(adapter, "wireless"))).Select(Path.GetFileName).OfType<string>()]
            : [];
}

/// <summary>The networks the computer is connected to on Linux, for telling home from away.</summary>
public sealed class LinuxNetworkLocation : INetworkLocation, IDisposable
{
    private readonly LinuxNetwork _network = new();

    public LinuxNetworkLocation() => NetworkChange.NetworkAddressChanged += OnAddressChanged;

    public event EventHandler? Changed;

    public IReadOnlyList<string> ConnectedNetworks => _network.ConnectedNetworks();

    /// <summary>The names of the networks connected right now, for the window to offer.</summary>
    public static IReadOnlyList<string> Read() => new LinuxNetwork().ConnectedNetworks();

    public void Dispose() => NetworkChange.NetworkAddressChanged -= OnAddressChanged;

    private void OnAddressChanged(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);
}

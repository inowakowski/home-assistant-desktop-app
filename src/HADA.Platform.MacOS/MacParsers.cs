using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using HADA.Core.Abstractions;

namespace HADA.Platform.MacOS;

/// <summary>Who is at the Mac's own screen, as the login window tells the I/O Registry.</summary>
/// <param name="UserName">Empty while the login window is on the screen.</param>
public readonly record struct ConsoleSession(string UserName, bool IsLocked);

/// <summary>A network port of the Mac, as System Settings names it: <c>Wi-Fi</c> on <c>en0</c>, <c>Thunderbolt Ethernet</c> on <c>en5</c>.</summary>
public readonly record struct HardwarePort(string Name, string Device)
{
    public bool IsWifi => Name is "Wi-Fi" or "AirPort";
}

/// <summary>The volume settings AppleScript knows: of the output, of the input, and whether the output is muted.</summary>
public readonly record struct VolumeSettings(int? OutputVolume, int? InputVolume, bool? OutputMuted);

/// <summary>What macOS's own tools print, read. Kept apart from running them, so it is tested on every system.</summary>
public static partial class MacParsers
{
    /// <summary>
    /// Reads <c>pmset -g batt</c>: where the power comes from, and the battery's charge and state.
    /// <code>
    /// Now drawing from 'AC Power'
    ///  -InternalBattery-0 (id=4653155)	85%; charging; 1:10 remaining present: true
    /// </code>
    /// A Mac without a battery has only the first line.
    /// </summary>
    public static PowerStatus? ParsePmsetBattery(string output)
    {
        if (!output.Contains("Now drawing from", StringComparison.Ordinal))
        {
            return null;
        }

        var pluggedIn = output.Contains("'AC Power'", StringComparison.Ordinal);
        var battery = output.Split('\n').FirstOrDefault(line => line.Contains("InternalBattery", StringComparison.Ordinal));
        if (battery is null)
        {
            return new PowerStatus(HasBattery: false, BatteryPercent: null, IsCharging: false, IsPluggedIn: pluggedIn);
        }

        var percent = BatteryPercent().Match(battery) is { Success: true } match
            ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)
            : (int?)null;

        // "charging", but not "discharging" nor "not charging" (plugged in, held below full by the Mac).
        var states = battery.Split(';', StringSplitOptions.TrimEntries);
        var charging = states.Any(state => state == "charging" || state.StartsWith("finishing charge", StringComparison.Ordinal));
        return new PowerStatus(HasBattery: true, percent is null ? null : Math.Clamp(percent.Value, 0, 100), charging, pluggedIn);
    }

    /// <summary>Reads <c>ioreg -r -k AppleClamshellState -d 4</c>: <c>"AppleClamshellState" = Yes</c> while the lid is closed. A Mac without a lid has no such line.</summary>
    public static bool? ParseLidOpen(string output) =>
        ClamshellState().Match(output) is { Success: true } match ? match.Groups[1].Value == "No" : null;

    /// <summary>
    /// Reads <c>ioreg -r -d 1 -w 0 -c IOAccelerator</c>, where each graphics processor tells how busy it is among its
    /// performance statistics: <c>"Device Utilization %"=12</c>, or <c>"GPU Activity(%)"=12</c> on some older ones. The busiest counts.
    /// </summary>
    public static int? ParseGpuLoad(string output)
    {
        var loads = GpuUtilization().Matches(output).Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
        return loads.Count > 0 ? Math.Clamp(loads.Max(), 0, 100) : null;
    }

    /// <summary>
    /// Reads <c>ioreg -a -n Root -d 1</c>, the I/O Registry's root as a property list, whose <c>IOConsoleUsers</c> are
    /// the sessions the login window knows: the one on the screen, its user, and whether its screen is locked.
    /// <see langword="null"/> where the list cannot be read; nobody's session on the screen is the login window.
    /// </summary>
    public static ConsoleSession? ParseConsoleUsers(string plist)
    {
        if (ReadPropertyList(plist) is not Dictionary<string, object?> root)
        {
            return null;
        }

        if (!root.TryGetValue("IOConsoleUsers", out var users) || users is not List<object?> sessions)
        {
            return new ConsoleSession(string.Empty, IsLocked: true);
        }

        foreach (var session in sessions.OfType<Dictionary<string, object?>>())
        {
            if (session.GetValueOrDefault("kCGSSessionOnConsoleKey") is true
                && session.GetValueOrDefault("kCGSessionLoginDoneKey") is not false
                && session.GetValueOrDefault("kCGSSessionUserNameKey") is string { Length: > 0 } name)
            {
                return new ConsoleSession(name, session.GetValueOrDefault("CGSSessionScreenIsLocked") is true);
            }
        }

        return new ConsoleSession(string.Empty, IsLocked: true);
    }

    /// <summary>
    /// Reads <c>ioreg -a -p IOUSB -l -w 0</c>, the USB devices as a property list of nested entries: each device with
    /// its vendor and product id, and the names it gives itself. The root hubs are left out.
    /// </summary>
    public static IReadOnlyList<MacUsbDevice> ParseUsbDevices(string plist)
    {
        var devices = new List<MacUsbDevice>();
        void Visit(object? entry)
        {
            switch (entry)
            {
                case List<object?> list:
                    list.ForEach(Visit);
                    break;
                case Dictionary<string, object?> properties:
                    if (properties.GetValueOrDefault("idVendor") is long vendor
                        && properties.GetValueOrDefault("idProduct") is long product
                        && !(properties.GetValueOrDefault("IORegistryEntryName") is string entryName && entryName.Contains("Root Hub", StringComparison.OrdinalIgnoreCase)))
                    {
                        var matchId = string.Create(CultureInfo.InvariantCulture, $"VID_{vendor:X4}&PID_{product:X4}");
                        var serial = properties.GetValueOrDefault("USB Serial Number") as string;
                        var name = properties.GetValueOrDefault("USB Product Name") as string
                            ?? properties.GetValueOrDefault("USB Vendor Name") as string
                            ?? matchId;
                        devices.Add(new MacUsbDevice(name, matchId, string.IsNullOrEmpty(serial) ? $"USB\\{matchId}" : $"USB\\{matchId}\\{serial}"));
                    }

                    Visit(properties.GetValueOrDefault("IORegistryEntryChildren"));
                    break;
            }
        }

        Visit(ReadPropertyList(plist));
        return devices;
    }

    /// <summary>
    /// Reads <c>networksetup -listallhardwareports</c>: a block for each port, its name and its device.
    /// <code>
    /// Hardware Port: Wi-Fi
    /// Device: en0
    /// Ethernet Address: a4:83:e7:00:00:01
    /// </code>
    /// </summary>
    public static IReadOnlyList<HardwarePort> ParseHardwarePorts(string output)
    {
        var ports = new List<HardwarePort>();
        string? name = null;
        foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("Hardware Port:", StringComparison.Ordinal))
            {
                name = line["Hardware Port:".Length..].Trim();
            }
            else if (line.StartsWith("Device:", StringComparison.Ordinal) && name is not null)
            {
                ports.Add(new HardwarePort(name, line["Device:".Length..].Trim()));
                name = null;
            }
        }

        return ports;
    }

    /// <summary>
    /// The Wi-Fi network in <c>ipconfig getsummary en0</c>, a line <c>SSID : Home</c> while connected: its name, or
    /// none where macOS keeps it to itself and prints <c>&lt;redacted&gt;</c>.
    /// </summary>
    public static (bool Connected, string? Name) ParseSsid(string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && line[..colon].Trim() == "SSID")
            {
                var ssid = line[(colon + 1)..].Trim();
                return ssid.Length == 0 ? (false, null) : (true, ssid == "<redacted>" ? null : ssid);
            }
        }

        return (false, null);
    }

    /// <summary>
    /// Reads what AppleScript's <c>get volume settings</c> prints: <c>output volume:44, input volume:75, alert volume:100, output muted:false</c>.
    /// Without an output device its values are <c>missing value</c>.
    /// </summary>
    public static VolumeSettings ParseVolumeSettings(string output)
    {
        var values = output.Trim().Split(',', StringSplitOptions.TrimEntries)
            .Select(part => part.Split(':', 2, StringSplitOptions.TrimEntries))
            .Where(pair => pair.Length == 2)
            .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);

        int? Number(string key) =>
            values.TryGetValue(key, out var text) && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                ? Math.Clamp(number, 0, 100)
                : null;

        bool? muted = values.GetValueOrDefault("output muted") switch
        {
            "true" => true,
            "false" => false,
            _ => null,
        };
        return new VolumeSettings(Number("output volume"), Number("input volume"), muted);
    }

    /// <summary>
    /// An XML property list as dictionaries, lists, texts, whole numbers and truth values; <see langword="null"/>
    /// where the text is not one.
    /// </summary>
    public static object? ReadPropertyList(string plist)
    {
        XElement root;
        try
        {
            // Every property list names Apple's DTD; it is skipped, and never fetched.
            var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Ignore, XmlResolver = null };
            using var reader = System.Xml.XmlReader.Create(new StringReader(plist), settings);
            root = XDocument.Load(reader).Root!;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        return root.Name == "plist" ? root.Elements().Select(Value).FirstOrDefault() : null;
    }

    private static object? Value(XElement element) => element.Name.LocalName switch
    {
        "dict" => Dictionary(element),
        "array" => element.Elements().Select(Value).ToList(),
        "string" => element.Value,
        "integer" => long.TryParse(element.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) ? number : null,
        "real" => double.TryParse(element.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var real) ? real : null,
        "true" => true,
        "false" => false,
        _ => element.Value,
    };

    private static Dictionary<string, object?> Dictionary(XElement dict)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        string? key = null;
        foreach (var element in dict.Elements())
        {
            if (element.Name.LocalName == "key")
            {
                key = element.Value;
            }
            else if (key is not null)
            {
                values[key] = Value(element);
                key = null;
            }
        }

        return values;
    }

    [GeneratedRegex(@"(\d{1,3})%")]
    private static partial Regex BatteryPercent();

    [GeneratedRegex(@"""AppleClamshellState""\s*=\s*(Yes|No)")]
    private static partial Regex ClamshellState();

    [GeneratedRegex(@"""(?:Device Utilization %|GPU Activity\(%\))""\s*=\s*(\d+)")]
    private static partial Regex GpuUtilization();
}

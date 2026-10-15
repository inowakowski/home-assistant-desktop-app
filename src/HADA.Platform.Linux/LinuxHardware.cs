using System.Globalization;

namespace HADA.Platform.Linux;

/// <summary>How many screens are connected, and how many of them are not the laptop's own.</summary>
public readonly record struct DisplayCount(int Total, int External);

/// <summary>A screen whose backlight can be set, by the kernel's name for it.</summary>
public readonly record struct Backlight(string Name, int Level, int MaxLevel)
{
    public int Percent => MaxLevel > 0 ? (int)Math.Round(100d * Level / MaxLevel) : 0;
}

/// <summary>What the kernel says about the computer's hardware, in <c>/proc</c> and <c>/sys</c>.</summary>
/// <param name="root">Where the kernel's folders are; replaced in tests.</param>
public sealed class LinuxHardware(string root = "/")
{
    // The connectors of a laptop's own screen: embedded DisplayPort, LVDS on older ones, DSI on some tablets.
    private static readonly string[] InternalConnectors = ["eDP", "LVDS", "DSI"];

    // The kernel's advice for which backlight to use when there are several: the firmware's, the platform's, the raw one.
    private static readonly string[] BacklightTypes = ["firmware", "platform", "raw"];

    /// <summary>
    /// Whether the lid is open, as ACPI reports it in <c>/proc/acpi/button/lid/*/state</c>; <see langword="null"/>
    /// where there is no lid to tell.
    /// </summary>
    public bool? ReadLidOpen()
    {
        var folder = Path.Combine(root, "proc", "acpi", "button", "lid");
        if (!Directory.Exists(folder))
        {
            return null;
        }

        foreach (var lid in Directory.EnumerateDirectories(folder))
        {
            var state = Proc.ReadText(Path.Combine(lid, "state"));
            if (state?.Contains("open", StringComparison.Ordinal) == true)
            {
                return true;
            }

            if (state?.Contains("closed", StringComparison.Ordinal) == true)
            {
                return false;
            }
        }

        return null;
    }

    /// <summary>
    /// The connected screens, from <c>/sys/class/drm</c>: a folder for each connector of each graphics card,
    /// <c>card0-eDP-1</c>, <c>card0-HDMI-A-1</c>, with its <c>status</c> in it. <see langword="null"/> where there is no such folder.
    /// </summary>
    public DisplayCount? ReadDisplays()
    {
        var folder = Path.Combine(root, "sys", "class", "drm");
        if (!Directory.Exists(folder))
        {
            return null;
        }

        int total = 0, external = 0;
        foreach (var connector in Directory.EnumerateDirectories(folder))
        {
            var name = Path.GetFileName(connector);
            var dash = name.IndexOf('-', StringComparison.Ordinal);
            if (dash < 0 || Proc.ReadText(Path.Combine(connector, "status"))?.Trim() != "connected")
            {
                continue;
            }

            total++;
            if (!Array.Exists(InternalConnectors, kind => name[(dash + 1)..].StartsWith(kind, StringComparison.Ordinal)))
            {
                external++;
            }
        }

        return new DisplayCount(total, external);
    }

    /// <summary>The screen's backlight, from <c>/sys/class/backlight</c>; <see langword="null"/> where there is none, as on a desktop.</summary>
    public Backlight? ReadBacklight()
    {
        var folder = Path.Combine(root, "sys", "class", "backlight");
        if (!Directory.Exists(folder))
        {
            return null;
        }

        var devices = Directory.EnumerateDirectories(folder)
            .Select(device => (Device: device, Type: Proc.ReadText(Path.Combine(device, "type"))?.Trim() ?? "raw"))
            .OrderBy(device => Array.IndexOf(BacklightTypes, device.Type) is var rank && rank >= 0 ? rank : BacklightTypes.Length)
            .ThenBy(device => device.Device, StringComparer.Ordinal);
        foreach (var (device, _) in devices)
        {
            if (Number(Path.Combine(device, "brightness")) is { } level && Number(Path.Combine(device, "max_brightness")) is > 0 and var max)
            {
                return new Backlight(Path.GetFileName(device), level, max);
            }
        }

        return null;
    }

    /// <summary>
    /// How busy the graphics card is, 0-100, where the kernel tells: AMD's cards say so in
    /// <c>gpu_busy_percent</c>. <see langword="null"/> where none does.
    /// </summary>
    public int? ReadGpuBusyPercent()
    {
        var folder = Path.Combine(root, "sys", "class", "drm");
        if (!Directory.Exists(folder))
        {
            return null;
        }

        foreach (var card in Directory.EnumerateDirectories(folder, "card*").Where(card => !Path.GetFileName(card).Contains('-', StringComparison.Ordinal)))
        {
            if (Number(Path.Combine(card, "device", "gpu_busy_percent")) is { } busy)
            {
                return Math.Clamp(busy, 0, 100);
            }
        }

        return null;
    }

    private static int? Number(string path) =>
        int.TryParse(Proc.ReadText(path)?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;
}

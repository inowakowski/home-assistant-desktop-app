using System.Globalization;
using HADA.Core.Abstractions;
using HADA.Core.Unix;

namespace HADA.Platform.Linux;

/// <summary>A screen whose backlight can be set, by the kernel's name for it.</summary>
public readonly record struct Backlight(string Name, int Level, int MaxLevel)
{
    public int Percent => MaxLevel > 0 ? (int)Math.Round(100d * Level / MaxLevel) : 0;
}

/// <summary>
/// What the kernel says about the computer's hardware, in <c>/proc</c> and <c>/sys</c>, and what NVIDIA's driver says
/// about its cards.
/// </summary>
/// <param name="root">Where the kernel's folders are; replaced in tests.</param>
/// <param name="run">Runs a tool; replaced in tests.</param>
public sealed class LinuxHardware(string root = "/", Func<string, string[], ToolResult?>? run = null) : IHardwareReadings
{
    // Without nvidia-smi now, it is not there later either; asking again every ten seconds would be wasted.
    private readonly Lazy<bool> _hasNvidia = new(() => Tools.Find("nvidia-smi") is not null);
    private readonly Func<string, string[], ToolResult?> _run = run ?? Tools.Run;

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

    /// <summary>AMD's cards tell through the kernel, NVIDIA's through their driver's <c>nvidia-smi</c>; Intel's keep it to themselves.</summary>
    public bool HasGpuLoad => ReadGpuBusyPercent() is not null || _hasNvidia.Value;

    public int? ReadGpuLoadPercent() => ReadGpuBusyPercent() ?? (_hasNvidia.Value ? ReadNvidia() : null);

    /// <summary>Reads <c>nvidia-smi --query-gpu=utilization.gpu --format=csv,noheader,nounits</c>: a number per card; the busiest counts.</summary>
    public static int? ParseNvidiaSmi(string output)
    {
        var loads = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(line => int.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out var load) ? load : (int?)null)
            .OfType<int>()
            .ToList();
        return loads.Count > 0 ? Math.Clamp(loads.Max(), 0, 100) : null;
    }

    private static int? Number(string path) =>
        int.TryParse(Proc.ReadText(path)?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;

    private int? ReadNvidia() =>
        _run("nvidia-smi", ["--query-gpu=utilization.gpu", "--format=csv,noheader,nounits"]) is { Succeeded: true } result
            ? ParseNvidiaSmi(result.Output)
            : null;
}

/// <summary>
/// The screens, as the kernel lists them, and the window in front, in an X11 session. Opens the X11 display once,
/// for as long as the agent runs.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class LinuxDisplayReadings(LinuxHardware hardware) : IDisplayReadings, IFullScreen, IDisposable
{
    private readonly X11ActiveWindow? _windows = X11ActiveWindow.Open();

    /// <summary>On X11, from the window in front; Wayland does not tell, as it does not tell which window that is.</summary>
    FullScreenKind? IFullScreen.Read() => _windows is null ? null : _windows.ReadFullScreen() ? FullScreenKind.App : FullScreenKind.None;

    public DisplayCount? ReadDisplays() => hardware.ReadDisplays();

    public bool HasActiveWindow => _windows is not null;

    public ActiveWindowInfo? ReadActiveWindow() => _windows?.Read();

    public void Dispose() => _windows?.Dispose();
}

using System.Globalization;
using HADA.Core.Abstractions;
using HADA.Core.Unix;

namespace HADA.Platform.Linux;

/// <summary>
/// The screen's backlight, read from the kernel and set through systemd-logind, which lets the user at the
/// computer do it without a password.
/// </summary>
/// <param name="run">Runs a tool; replaced in tests.</param>
public sealed class LinuxBacklight(LinuxHardware hardware, Func<string, string[], ToolResult?>? run = null) : IScreenBrightness
{
    private readonly Func<string, string[], ToolResult?> _run = run ?? Tools.Run;

    public int? ReadPercent() => hardware.ReadBacklight()?.Percent;

    public string? SetPercent(int percent)
    {
        if (hardware.ReadBacklight() is not { } backlight)
        {
            return "there is no backlight to set.";
        }

        var result = _run(
            "busctl",
            ["call", "org.freedesktop.login1", "/org/freedesktop/login1/session/auto", "org.freedesktop.login1.Session", "SetBrightness",
                "ssu", "backlight", backlight.Name, LevelFor(backlight, percent).ToString(CultureInfo.InvariantCulture)]);
        return result is { Succeeded: true } ? null : Tools.Describe("busctl", result);
    }

    /// <summary>The level to set for <paramref name="percent"/> of the backlight's range; the lowest step above off keeps the screen readable.</summary>
    public static int LevelFor(Backlight backlight, double percent) =>
        Math.Clamp((int)Math.Round(backlight.MaxLevel * Math.Clamp(percent, 0, 100) / 100), 1, backlight.MaxLevel);
}

/// <summary>
/// Whether the screens are on, and turning them off and on, the way the session's desktop does it: GNOME's Mutter
/// through its power save mode, which it has on Wayland and X11 alike; KDE's through <c>kscreen-doctor</c>, which
/// switches them but does not say how they are; any other X11 desktop through <c>xset</c>.
/// </summary>
/// <param name="run">Runs a tool; replaced in tests.</param>
/// <param name="desktop">The session's <c>XDG_CURRENT_DESKTOP</c>; replaced in tests.</param>
/// <param name="isX11">Whether the session is an X11 one; replaced in tests.</param>
public sealed class LinuxDisplayPower(Func<string, string[], ToolResult?>? run = null, string? desktop = null, bool? isX11 = null) : IDisplayPower
{
    private const string Mutter = "org.gnome.Mutter.DisplayConfig";
    private const string MutterPath = "/org/gnome/Mutter/DisplayConfig";

    // Mutter's power save mode while on; 1 to 3 are standby, suspend and off, which HADA sets.
    private const int On = 0;

    private readonly Func<string, string[], ToolResult?> _run = run ?? Tools.Run;
    private readonly string _desktop = desktop ?? Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? string.Empty;
    private readonly bool _isX11 = isX11 ?? (Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") is null or "x11"
        && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")));

    private Way? _way;

    private enum Way
    {
        None,
        Mutter,
        Kde,
        Xset,
    }

    public bool CanSwitch => Chosen != Way.None;

    public bool? ReadDisplayOn() => Chosen switch
    {
        Way.Mutter => Ask("busctl", ["--user", "get-property", Mutter, MutterPath, Mutter, "PowerSaveMode"]) is { } mode ? ParseMutterPowerSaveMode(mode) : null,
        Way.Xset => Ask("xset", ["q"]) is { } state ? ParseXsetMonitor(state) : null,
        _ => null,
    };

    public string? TurnOff() => Switch(on: false);

    public string? Wake() => Switch(on: true);

    /// <summary>Reads Mutter's <c>PowerSaveMode</c> as <c>busctl</c> prints it: <c>i 0</c> while on, <c>i 3</c> while off; <c>i -1</c> where it does not know.</summary>
    public static bool? ParseMutterPowerSaveMode(string output) =>
        output.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries) is ["i", var number]
        && int.TryParse(number, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var mode) && mode >= On
            ? mode == On
            : null;

    /// <summary>Reads <c>xset q</c>, whose DPMS part ends with <c>Monitor is On</c>, <c>Off</c>, <c>in Standby</c> or <c>in Suspend</c>.</summary>
    public static bool? ParseXsetMonitor(string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("Monitor is", StringComparison.Ordinal))
            {
                return line == "Monitor is On";
            }
        }

        return null;
    }

    private Way Chosen => _way ??= Choose();

    private Way Choose()
    {
        if (Ask("busctl", ["--user", "get-property", Mutter, MutterPath, Mutter, "PowerSaveMode"]) is { } mode && ParseMutterPowerSaveMode(mode) is not null)
        {
            return Way.Mutter;
        }

        if (_desktop.Split(':').Contains("KDE", StringComparer.OrdinalIgnoreCase) && Tools.Find("kscreen-doctor") is not null)
        {
            return Way.Kde;
        }

        return _isX11 && Ask("xset", ["q"]) is { } state && ParseXsetMonitor(state) is not null ? Way.Xset : Way.None;
    }

    private string? Switch(bool on)
    {
        var (tool, arguments) = Chosen switch
        {
            Way.Mutter => ("busctl", new[] { "--user", "set-property", Mutter, MutterPath, Mutter, "PowerSaveMode", "i", on ? "0" : "3" }),
            Way.Kde => ("kscreen-doctor", ["--dpms", on ? "on" : "off"]),
            Way.Xset => ("xset", ["dpms", "force", on ? "on" : "off"]),
            _ => (string.Empty, []),
        };
        if (tool.Length == 0)
        {
            return "the desktop does not let its screens be switched off.";
        }

        var result = _run(tool, arguments);
        return result is { Succeeded: true } ? null : Tools.Describe(tool, result);
    }

    private string? Ask(string tool, string[] arguments) =>
        _run(tool, arguments) is { Succeeded: true } result ? result.Output : null;
}

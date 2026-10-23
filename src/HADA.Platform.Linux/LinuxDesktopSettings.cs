using HADA.Core.Abstractions;
using HADA.Core.Unix;

namespace HADA.Platform.Linux;

/// <summary>Which desktop the session runs, from <c>XDG_CURRENT_DESKTOP</c>, a list of names such as <c>ubuntu:GNOME</c> or <c>KDE</c>.</summary>
internal static class LinuxDesktop
{
    public static string Current => Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? string.Empty;

    public static bool Is(string desktop, string name) =>
        desktop.Split(':', StringSplitOptions.RemoveEmptyEntries).Contains(name, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The dark look as the desktop keeps it: GNOME's colour scheme, which its Settings switch between "Default" and
/// "Dark", and KDE's colour scheme, Breeze Dark or Breeze Light. Other desktops are left out.
/// </summary>
/// <param name="run">Runs a tool; replaced in tests.</param>
/// <param name="desktop">The session's <c>XDG_CURRENT_DESKTOP</c>; replaced in tests.</param>
public sealed class LinuxDarkMode(Func<string, string[], ToolResult?>? run = null, string? desktop = null) : IDarkMode
{
    private static readonly string[] GnomeScheme = ["org.gnome.desktop.interface", "color-scheme"];

    private readonly Func<string, string[], ToolResult?> _run = run ?? Tools.Run;
    private readonly string _desktop = desktop ?? LinuxDesktop.Current;
    private Way? _way;

    private enum Way
    {
        None,
        Gnome,
        Kde,
    }

    public bool CanSet => Chosen != Way.None;

    public bool? Read() => Chosen switch
    {
        Way.Gnome => _run("gsettings", ["get", .. GnomeScheme]) is { Succeeded: true } scheme ? ParseGnomeScheme(scheme.Output) : null,
        Way.Kde => KdeScheme() is { } name ? name.Contains("Dark", StringComparison.OrdinalIgnoreCase) : null,
        _ => null,
    };

    public string? Set(bool dark)
    {
        var (tool, arguments) = Chosen switch
        {
            Way.Gnome => ("gsettings", new[] { "set", GnomeScheme[0], GnomeScheme[1], dark ? "prefer-dark" : "default" }),
            Way.Kde => ("plasma-apply-colorscheme", new[] { dark ? "BreezeDark" : "BreezeLight" }),
            _ => (string.Empty, Array.Empty<string>()),
        };
        if (tool.Length == 0)
        {
            return "the desktop does not let its look be switched.";
        }

        var result = _run(tool, arguments);
        return result is { Succeeded: true } ? null : Tools.Describe(tool, result);
    }

    /// <summary>Reads <c>gsettings get org.gnome.desktop.interface color-scheme</c>: <c>'prefer-dark'</c>, <c>'default'</c> or <c>'prefer-light'</c>.</summary>
    public static bool? ParseGnomeScheme(string output) => output.Trim().Trim('\'') switch
    {
        "prefer-dark" => true,
        "default" or "prefer-light" => false,
        _ => null,
    };

    private Way Chosen => _way ??= Choose();

    private Way Choose()
    {
        if (LinuxDesktop.Is(_desktop, "GNOME") && _run("gsettings", ["get", .. GnomeScheme]) is { Succeeded: true } scheme
            && ParseGnomeScheme(scheme.Output) is not null)
        {
            return Way.Gnome;
        }

        return LinuxDesktop.Is(_desktop, "KDE") && Tools.Find("plasma-apply-colorscheme") is not null ? Way.Kde : Way.None;
    }

    private string? KdeScheme()
    {
        var reader = Tools.Find("kreadconfig6") is not null ? "kreadconfig6" : "kreadconfig5";
        return _run(reader, ["--file", "kdeglobals", "--group", "General", "--key", "ColorScheme"]) is { Succeeded: true } result
            ? result.Output.Trim()
            : null;
    }
}

/// <summary>
/// Signs out the way the desktop's own menu does: GNOME's session manager, without asking again, or KDE's shutdown
/// service. Apps with unsaved work can still hold it up. Other desktops are left out.
/// </summary>
/// <param name="run">Runs a tool; replaced in tests.</param>
/// <param name="desktop">The session's <c>XDG_CURRENT_DESKTOP</c>; replaced in tests.</param>
public sealed class LinuxSignOut(Func<string, string[], ToolResult?>? run = null, string? desktop = null) : ISignOut
{
    private readonly Func<string, string[], ToolResult?> _run = run ?? Tools.Run;
    private readonly string _desktop = desktop ?? LinuxDesktop.Current;

    public bool CanSignOut => IsGnome || IsKde;

    private bool IsGnome => LinuxDesktop.Is(_desktop, "GNOME");

    private bool IsKde => LinuxDesktop.Is(_desktop, "KDE");

    public string? SignOut()
    {
        if (IsGnome)
        {
            var result = _run("gnome-session-quit", ["--logout", "--no-prompt"]);
            return result is { Succeeded: true } ? null : Tools.Describe("gnome-session-quit", result);
        }

        if (IsKde)
        {
            // Plasma 6's shutdown service, then Plasma 5's session manager.
            var plasma6 = _run("dbus-send", ["--session", "--print-reply", "--dest=org.kde.Shutdown", "/Shutdown", "org.kde.Shutdown.logout"]);
            if (plasma6 is { Succeeded: true })
            {
                return null;
            }

            var plasma5 = _run(
                "dbus-send",
                ["--session", "--print-reply", "--dest=org.kde.ksmserver", "/KSMServer", "org.kde.KSMServerInterface.logout", "int32:0", "int32:0", "int32:0"]);
            return plasma5 is { Succeeded: true } ? null : Tools.Describe("dbus-send", plasma5);
        }

        return "the desktop cannot be told to sign out.";
    }
}

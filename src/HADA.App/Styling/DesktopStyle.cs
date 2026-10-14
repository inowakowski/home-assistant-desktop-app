namespace HADA.App.Styling;

/// <summary>The look of the window. Each system gets its own, so that HADA looks like the apps around it.</summary>
public enum DesktopStyle
{
    /// <summary>Windows 11: Mica behind the window, a layer for the content, the accent marking the page.</summary>
    Windows,

    /// <summary>macOS: a translucent sidebar with coloured icons, the page title in the toolbar.</summary>
    MacOS,

    /// <summary>GNOME: libadwaita's header bars and boxed lists, drawn by HADA itself.</summary>
    Gnome,

    /// <summary>KDE Plasma: Breeze, under the title bar KWin draws.</summary>
    Kde,
}

public static class DesktopStyles
{
    public const string Argument = "--style";

    /// <summary>The style asked for with <c>--style windows|macos|gnome|kde</c>, for trying one out; else the system's own.</summary>
    public static DesktopStyle Choose(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == Argument && Enum.TryParse<DesktopStyle>(args[i + 1], ignoreCase: true, out var chosen))
            {
                return chosen;
            }
        }

        return OfThisSystem();
    }

    /// <summary>The style of the system this runs on. On Linux, KDE Plasma gets Breeze and every other desktop GNOME's look.</summary>
    public static DesktopStyle OfThisSystem()
    {
        if (OperatingSystem.IsWindows())
        {
            return DesktopStyle.Windows;
        }

        if (OperatingSystem.IsMacOS())
        {
            return DesktopStyle.MacOS;
        }

        var desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? string.Empty;
        return desktop.Contains("KDE", StringComparison.OrdinalIgnoreCase) ? DesktopStyle.Kde : DesktopStyle.Gnome;
    }
}

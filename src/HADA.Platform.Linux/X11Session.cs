using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace HADA.Platform.Linux;

/// <summary>The X11 display of the user's session, where the session is an X11 one.</summary>
[SupportedOSPlatform("linux")]
internal static partial class X11Session
{
    public const string Library = "libX11.so.6";

    // An X error, such as asking about a window that closed a moment ago, ends the process unless handled.
    private static readonly XErrorHandler IgnoreErrors = static (_, _) => 0;

    private delegate int XErrorHandler(IntPtr display, IntPtr errorEvent);

    /// <summary>
    /// Whether the session is an X11 one. Wayland keeps the windows and the keyboard to the desktop itself; a
    /// session that does not say what it is counts as X11 where it has a display.
    /// </summary>
    public static bool IsX11 =>
        Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") is null or "x11"
        && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));

    /// <summary>A connection to the session's display; <see cref="IntPtr.Zero"/> where there is none to open.</summary>
    public static IntPtr OpenDisplay()
    {
        if (!IsX11)
        {
            return IntPtr.Zero;
        }

        try
        {
            var display = XOpenDisplay(IntPtr.Zero);
            if (display != IntPtr.Zero)
            {
                _ = XSetErrorHandler(IgnoreErrors);
            }

            return display;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return IntPtr.Zero;
        }
    }

    [DllImport(Library)]
    public static extern int XCloseDisplay(IntPtr display);

    [DllImport(Library)]
    public static extern int XFlush(IntPtr display);

    [DllImport(Library)]
    private static extern IntPtr XOpenDisplay(IntPtr name);

    [DllImport(Library)]
    private static extern IntPtr XSetErrorHandler(XErrorHandler handler);
}

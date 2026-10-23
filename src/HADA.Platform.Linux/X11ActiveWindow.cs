using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using HADA.Core.Abstractions;

namespace HADA.Platform.Linux;

/// <summary>
/// The window in front, as an X11 session's window manager tells every client through the root window's
/// <c>_NET_ACTIVE_WINDOW</c>. Wayland keeps that to the desktop itself, so there is nothing to read there.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed partial class X11ActiveWindow : IDisposable
{
    private const string Library = X11Session.Library;
    private const ulong AnyPropertyType = 0;
    private const int Success = 0;

    private readonly IntPtr _display;
    private readonly ulong _root;
    private readonly ulong _activeWindow;
    private readonly ulong _windowName;
    private readonly ulong _utf8;
    private readonly ulong _pid;
    private readonly ulong _state;
    private readonly ulong _fullScreen;

    // One display, asked by the sensors of the window in front and of the full screen, each on a thread of its own.
    private readonly Lock _lock = new();

    private X11ActiveWindow(IntPtr display)
    {
        _display = display;
        _root = XDefaultRootWindow(display);
        _activeWindow = XInternAtom(display, "_NET_ACTIVE_WINDOW", false);
        _windowName = XInternAtom(display, "_NET_WM_NAME", false);
        _utf8 = XInternAtom(display, "UTF8_STRING", false);
        _pid = XInternAtom(display, "_NET_WM_PID", false);
        _state = XInternAtom(display, "_NET_WM_STATE", false);
        _fullScreen = XInternAtom(display, "_NET_WM_STATE_FULLSCREEN", false);
    }

    /// <summary>The X11 session's display, where this session is one; <see langword="null"/> on Wayland or without a display.</summary>
    public static X11ActiveWindow? Open() =>
        X11Session.OpenDisplay() is var display && display != IntPtr.Zero ? new X11ActiveWindow(display) : null;

    /// <summary>The window in front; <see langword="null"/> while there is none, as on an empty desktop.</summary>
    public ActiveWindowInfo? Read()
    {
        lock (_lock)
        {
            return ActiveWindow() is { } window ? Describe(window) : null;
        }
    }

    /// <summary>
    /// Whether the window in front fills its screen, as its <c>_NET_WM_STATE</c> says: a film, a game or a slide
    /// show made full screen. <see langword="false"/> while there is no window in front.
    /// </summary>
    public bool ReadFullScreen()
    {
        lock (_lock)
        {
            if (ActiveWindow() is not { } window || ReadProperty(window, _state) is not { } atoms)
            {
                return false;
            }

            // Atoms of format 32, a C long each.
            for (var offset = 0; offset + 8 <= atoms.Length; offset += 8)
            {
                if ((ulong)BitConverter.ToInt64(atoms, offset) == _fullScreen)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public void Dispose() => _ = X11Session.XCloseDisplay(_display);

    private ulong? ActiveWindow()
    {
        if (ReadProperty(_root, _activeWindow) is not { Length: >= 8 } active)
        {
            return null;
        }

        // Format 32 comes as a C long, 64 bits wide here.
        var window = (ulong)BitConverter.ToInt64(active, 0);
        return window == 0 ? null : window;
    }

    private ActiveWindowInfo Describe(ulong window)
    {
        var title = ReadProperty(window, _windowName) is { } name ? Encoding.UTF8.GetString(name) : string.Empty;
        string? processName = null;
        if (ReadProperty(window, _pid) is { Length: >= 8 } pid)
        {
            processName = Proc.ReadText($"/proc/{BitConverter.ToInt64(pid, 0)}/comm")?.Trim();
        }

        return new ActiveWindowInfo(title, processName);
    }

    private byte[]? ReadProperty(ulong window, ulong property)
    {
        if (XGetWindowProperty(_display, window, property, 0, 1024, false, AnyPropertyType, out _, out var format, out var count, out _, out var data) != Success
            || data == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            // Items of format 32 take a long each; of format 8, a byte.
            var size = format == 32 ? (int)count * IntPtr.Size : format == 16 ? (int)count * 2 : (int)count;
            var bytes = new byte[size];
            Marshal.Copy(data, bytes, 0, size);
            return bytes;
        }
        finally
        {
            _ = XFree(data);
        }
    }

    [DllImport(Library)]
    private static extern ulong XDefaultRootWindow(IntPtr display);

    [DllImport(Library, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern ulong XInternAtom(IntPtr display, string name, [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);

    [DllImport(Library)]
    private static extern int XGetWindowProperty(
        IntPtr display,
        ulong window,
        ulong property,
        long offset,
        long length,
        [MarshalAs(UnmanagedType.Bool)] bool delete,
        ulong requestedType,
        out ulong actualType,
        out int actualFormat,
        out ulong itemCount,
        out ulong bytesAfter,
        out IntPtr data);

    [DllImport(Library)]
    private static extern int XFree(IntPtr data);
}

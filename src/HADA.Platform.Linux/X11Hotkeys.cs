using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using HADA.Core.Input;

namespace HADA.Platform.Linux;

/// <summary>
/// Keyboard shortcuts that work whichever window has the focus, in an X11 session: each grabbed on the screen's
/// root window, with and without Caps Lock and Num Lock, which would otherwise make a shortcut fail. Wayland lets no
/// app listen for keys meant for others, so there are none there.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed partial class X11Hotkeys : IDisposable
{
    private const string Library = X11Session.Library;

    // Modifier masks of X11: Shift, Lock, Control, Mod1 (Alt), Mod2 (Num Lock), Mod4 (Super).
    private const uint ShiftMask = 1;
    private const uint LockMask = 2;
    private const uint ControlMask = 4;
    private const uint AltMask = 8;
    private const uint NumLockMask = 16;
    private const uint SuperMask = 64;

    private const int KeyPress = 2;
    private const int GrabModeAsync = 1;

    // An XEvent is a union of 24 longs; a key event's state and keycode are at these offsets on 64-bit systems.
    private const int EventSize = 192;
    private const int StateOffset = 80;
    private const int KeyCodeOffset = 84;

    private static readonly uint[] LockVariants = [0, LockMask, NumLockMask, LockMask | NumLockMask];
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    private readonly Lock _lock = new();
    private readonly IntPtr _display;
    private readonly ulong _root;
    private readonly Dictionary<(uint KeyCode, uint Modifiers), Action> _actions = [];
    private readonly CancellationTokenSource _stopping = new();
    private Thread? _listener;

    private X11Hotkeys(IntPtr display)
    {
        _display = display;
        _root = XDefaultRootWindow(display);
    }

    /// <summary>A connection of its own to the session's display; <see langword="null"/> on Wayland or without a display.</summary>
    public static X11Hotkeys? Open() =>
        X11Session.OpenDisplay() is var display && display != IntPtr.Zero ? new X11Hotkeys(display) : null;

    /// <summary>
    /// Replaces every shortcut set so far. Returns those that could not be had, as X11 has no such key, the
    /// keyboard layout lacks it or the X server keeps it for itself. One another app holds already is not told
    /// apart: it just does nothing.
    /// </summary>
    public IReadOnlyList<string> Set(IEnumerable<(string Shortcut, Action Pressed)> shortcuts)
    {
        var refused = new List<string>();
        lock (_lock)
        {
            Clear();
            foreach (var (shortcut, pressed) in shortcuts)
            {
                if (!KeyCombination.TryParse(shortcut, out var keys) || keys.VirtualKey == 0 || X11Keys.SwitchesConsoles(keys)
                    || X11Keys.KeysymOf(keys.VirtualKey) is not { } keysym || XKeysymToKeycode(_display, keysym) is var keyCode && keyCode == 0)
                {
                    refused.Add(shortcut);
                    continue;
                }

                var modifiers = MaskOf(keys.Modifiers);
                foreach (var variant in LockVariants)
                {
                    _ = XGrabKey(_display, keyCode, modifiers | variant, _root, ownerEvents: false, GrabModeAsync, GrabModeAsync);
                }

                _actions[(keyCode, modifiers)] = pressed;
            }

            // Waited for, so that the shortcuts work as soon as this returns.
            _ = X11Session.XSync(_display, discard: false);
        }

        if (_listener is null && _actions.Count > 0)
        {
            _listener = new Thread(Listen) { IsBackground = true, Name = "HADA X11 shortcuts" };
            _listener.Start();
        }

        return refused;
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _listener?.Join(TimeSpan.FromSeconds(2));
        lock (_lock)
        {
            Clear();
            _ = X11Session.XCloseDisplay(_display);
        }

        _stopping.Dispose();
    }

    /// <summary>X11's modifier mask for those of a combination; Win is Super.</summary>
    public static uint MaskOf(KeyModifiers modifiers) =>
        ((modifiers & KeyModifiers.Shift) != 0 ? ShiftMask : 0)
        | ((modifiers & KeyModifiers.Ctrl) != 0 ? ControlMask : 0)
        | ((modifiers & KeyModifiers.Alt) != 0 ? AltMask : 0)
        | ((modifiers & KeyModifiers.Win) != 0 ? SuperMask : 0);

    /// <summary>The modifiers that make the shortcut, without Caps Lock and Num Lock, which do not change it.</summary>
    public static uint WithoutLocks(uint state) => state & ~(LockMask | NumLockMask) & (ShiftMask | ControlMask | AltMask | SuperMask);

    private void Clear()
    {
        foreach (var (keyCode, modifiers) in _actions.Keys)
        {
            foreach (var variant in LockVariants)
            {
                _ = XUngrabKey(_display, keyCode, modifiers | variant, _root);
            }
        }

        _actions.Clear();
    }

    private void Listen()
    {
        var buffer = Marshal.AllocHGlobal(EventSize);
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                var pressed = new List<Action>();
                lock (_lock)
                {
                    while (XPending(_display) > 0)
                    {
                        _ = XNextEvent(_display, buffer);
                        if (Marshal.ReadInt32(buffer) == KeyPress
                            && _actions.TryGetValue(((uint)Marshal.ReadInt32(buffer, KeyCodeOffset), WithoutLocks((uint)Marshal.ReadInt32(buffer, StateOffset))), out var action))
                        {
                            pressed.Add(action);
                        }
                    }
                }

                // Outside the lock: what a shortcut does may set the shortcuts again.
                pressed.ForEach(action => action());
                _stopping.Token.WaitHandle.WaitOne(PollInterval);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport(Library)]
    private static extern ulong XDefaultRootWindow(IntPtr display);

    [DllImport(Library)]
    private static extern byte XKeysymToKeycode(IntPtr display, ulong keysym);

    [DllImport(Library)]
    private static extern int XGrabKey(
        IntPtr display, uint keyCode, uint modifiers, ulong grabWindow, [MarshalAs(UnmanagedType.Bool)] bool ownerEvents, int pointerMode, int keyboardMode);

    [DllImport(Library)]
    private static extern int XUngrabKey(IntPtr display, uint keyCode, uint modifiers, ulong grabWindow);

    [DllImport(Library)]
    private static extern int XPending(IntPtr display);

    [DllImport(Library)]
    private static extern int XNextEvent(IntPtr display, IntPtr eventBuffer);
}

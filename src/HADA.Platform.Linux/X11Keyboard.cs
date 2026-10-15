using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using HADA.Core.Abstractions;
using HADA.Core.Input;

namespace HADA.Platform.Linux;

/// <summary>
/// Presses keys on an X11 desktop through the XTEST extension, which X servers have for this very purpose.
/// Wayland lets no app type into another, so in a Wayland session nothing is pressed and the log says why.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed partial class X11Keyboard : IKeyboard, IDisposable
{
    private const string XTest = "libXtst.so.6";

    private readonly Lock _lock = new();
    private IntPtr _display;

    public string? Press(KeyCombination combination)
    {
        if (!X11Session.IsX11)
        {
            return "keys can only be pressed in an X11 session; Wayland does not let apps type into others.";
        }

        var keysyms = X11Keys.KeysymsOf(combination);
        if (keysyms is null)
        {
            return "X11 has no such key.";
        }

        lock (_lock)
        {
            if (_display == IntPtr.Zero && (_display = X11Session.OpenDisplay()) == IntPtr.Zero)
            {
                return "the X11 display cannot be opened.";
            }

            var keycodes = new List<uint>(keysyms.Count);
            foreach (var keysym in keysyms)
            {
                var keycode = XKeysymToKeycode(_display, keysym);
                if (keycode == 0)
                {
                    return $"the keyboard layout has no key for X11 key 0x{keysym:X}.";
                }

                keycodes.Add(keycode);
            }

            try
            {
                // Modifiers first, then the key; let go in reverse, as fingers do.
                foreach (var keycode in keycodes)
                {
                    _ = XTestFakeKeyEvent(_display, keycode, isPress: true, delay: 0);
                }

                for (var i = keycodes.Count - 1; i >= 0; i--)
                {
                    _ = XTestFakeKeyEvent(_display, keycodes[i], isPress: false, delay: 0);
                }

                _ = X11Session.XFlush(_display);
                return null;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return "libXtst is not installed.";
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_display != IntPtr.Zero)
            {
                _ = X11Session.XCloseDisplay(_display);
                _display = IntPtr.Zero;
            }
        }
    }

    [DllImport(X11Session.Library)]
    private static extern byte XKeysymToKeycode(IntPtr display, ulong keysym);

    [DllImport(XTest)]
    private static extern int XTestFakeKeyEvent(IntPtr display, uint keycode, [MarshalAs(UnmanagedType.Bool)] bool isPress, ulong delay);
}

/// <summary>The X11 keys, keysyms, of the keys <see cref="KeyCombination"/> names by their Windows codes.</summary>
public static class X11Keys
{
    /// <summary>The X11 keys of a combination, modifiers first; <see langword="null"/> where X11 has no such key.</summary>
    public static IReadOnlyList<ulong>? KeysymsOf(KeyCombination combination)
    {
        var keysyms = new List<ulong>(5);
        foreach (var (modifier, keysym) in new[]
        {
            (KeyModifiers.Ctrl, 0xFFE3UL), // Control_L
            (KeyModifiers.Alt, 0xFFE9UL), // Alt_L
            (KeyModifiers.Shift, 0xFFE1UL), // Shift_L
            (KeyModifiers.Win, 0xFFEBUL), // Super_L
        })
        {
            if ((combination.Modifiers & modifier) != 0)
            {
                keysyms.Add(keysym);
            }
        }

        if (combination.VirtualKey != 0)
        {
            if (KeysymOf(combination.VirtualKey) is not { } key)
            {
                return null;
            }

            keysyms.Add(key);
        }

        return keysyms.Count > 0 ? keysyms : null;
    }

    /// <summary>The X11 key, a keysym, of a Windows virtual-key code: those <see cref="KeyCombination"/> reads.</summary>
    public static ulong? KeysymOf(ushort virtualKey) => virtualKey switch
    {
        // Letters by their lowercase keysym, which is the key itself; digits as they are.
        >= 'A' and <= 'Z' => (ulong)char.ToLowerInvariant((char)virtualKey),
        >= '0' and <= '9' => virtualKey,
        >= 0x70 and <= 0x87 => 0xFFBEUL + (ulong)(virtualKey - 0x70), // F1 to F24
        0x08 => 0xFF08, // BackSpace
        0x09 => 0xFF09, // Tab
        0x0D => 0xFF0D, // Return
        0x13 => 0xFF13, // Pause
        0x1B => 0xFF1B, // Escape
        0x20 => 0x20, // space
        0x21 => 0xFF55, // Prior
        0x22 => 0xFF56, // Next
        0x23 => 0xFF57, // End
        0x24 => 0xFF50, // Home
        0x25 => 0xFF51, // Left
        0x26 => 0xFF52, // Up
        0x27 => 0xFF53, // Right
        0x28 => 0xFF54, // Down
        0x2C => 0xFF61, // Print
        0x2D => 0xFF63, // Insert
        0x2E => 0xFFFF, // Delete
        0xAD => 0x1008FF12, // XF86AudioMute
        0xAE => 0x1008FF11, // XF86AudioLowerVolume
        0xAF => 0x1008FF13, // XF86AudioRaiseVolume
        0xB0 => 0x1008FF17, // XF86AudioNext
        0xB1 => 0x1008FF16, // XF86AudioPrev
        0xB2 => 0x1008FF15, // XF86AudioStop
        0xB3 => 0x1008FF14, // XF86AudioPlay
        _ => null,
    };
}

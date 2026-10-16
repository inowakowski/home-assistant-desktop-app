using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Input;

namespace HADA.Platform.MacOS;

/// <summary>The Mac's keys for those <see cref="KeyCombination"/> names by their Windows codes.</summary>
public static class MacKeys
{
    // Event flags of the modifiers: Control, Option, Shift, Command.
    public const ulong ControlFlag = 0x40000;
    public const ulong OptionFlag = 0x80000;
    public const ulong ShiftFlag = 0x20000;
    public const ulong CommandFlag = 0x100000;

    // The keys of the ANSI keyboard, kVK_ANSI_A and on, in the order of the alphabet and the digits.
    private static readonly byte[] Letters =
        [0x00, 0x0B, 0x08, 0x02, 0x0E, 0x03, 0x05, 0x04, 0x22, 0x26, 0x28, 0x25, 0x2E, 0x2D, 0x1F, 0x23, 0x0C, 0x0F, 0x01, 0x11, 0x20, 0x09, 0x0D, 0x07, 0x10, 0x06];

    private static readonly byte[] Digits = [0x1D, 0x12, 0x13, 0x14, 0x15, 0x17, 0x16, 0x1A, 0x1C, 0x19];

    // F1 to F20; a Mac's keyboard has no F21 and on.
    private static readonly byte[] FunctionKeys =
        [0x7A, 0x78, 0x63, 0x76, 0x60, 0x61, 0x62, 0x64, 0x65, 0x6D, 0x67, 0x6F, 0x69, 0x6B, 0x71, 0x6A, 0x40, 0x4F, 0x50, 0x5A];

    /// <summary>The modifiers' keys and flags, in the order they are pressed: Control, Option, Shift, Command (Win).</summary>
    public static IEnumerable<(ushort KeyCode, ulong Flag)> ModifiersOf(KeyModifiers modifiers)
    {
        if ((modifiers & KeyModifiers.Ctrl) != 0)
        {
            yield return (0x3B, ControlFlag);
        }

        if ((modifiers & KeyModifiers.Alt) != 0)
        {
            yield return (0x3A, OptionFlag);
        }

        if ((modifiers & KeyModifiers.Shift) != 0)
        {
            yield return (0x38, ShiftFlag);
        }

        if ((modifiers & KeyModifiers.Win) != 0)
        {
            yield return (0x37, CommandFlag);
        }
    }

    /// <summary>The Mac's virtual key code of a Windows virtual key; <see langword="null"/> for the media keys and where a Mac has no such key.</summary>
    public static ushort? KeyCodeOf(ushort virtualKey) => virtualKey switch
    {
        >= 'A' and <= 'Z' => Letters[virtualKey - 'A'],
        >= '0' and <= '9' => Digits[virtualKey - '0'],
        >= 0x70 and <= 0x83 => FunctionKeys[virtualKey - 0x70],
        0x08 => 0x33, // Delete, the Mac's backspace
        0x09 => 0x30, // Tab
        0x0D => 0x24, // Return
        0x1B => 0x35, // Escape
        0x20 => 0x31, // Space
        0x21 => 0x74, // Page Up
        0x22 => 0x79, // Page Down
        0x23 => 0x77, // End
        0x24 => 0x73, // Home
        0x25 => 0x7B, // Left
        0x26 => 0x7E, // Up
        0x27 => 0x7C, // Right
        0x28 => 0x7D, // Down
        0x2D => 0x72, // Help, where Insert is on other keyboards
        0x2E => 0x75, // Forward Delete
        _ => null,
    };

    /// <summary>
    /// The media key of a Windows virtual key, as the Mac's keyboard sends it (NX_KEYTYPE_*): volume up, down and mute,
    /// play, next and previous. <see langword="null"/> for the others; a Mac's keyboard has no stop key.
    /// </summary>
    public static int? MediaKeyOf(ushort virtualKey) => virtualKey switch
    {
        0xAF => 0, // NX_KEYTYPE_SOUND_UP
        0xAE => 1, // NX_KEYTYPE_SOUND_DOWN
        0xAD => 7, // NX_KEYTYPE_MUTE
        0xB3 => 16, // NX_KEYTYPE_PLAY
        0xB0 => 17, // NX_KEYTYPE_NEXT
        0xB1 => 18, // NX_KEYTYPE_PREVIOUS
        _ => null,
    };
}

/// <summary>
/// Presses keys on the Mac as if typed, through Core Graphics' events; the media keys as the keyboard's own. macOS
/// lets an app do that only once it is allowed under Privacy &amp; Security, Accessibility; until then nothing is
/// pressed, and the log says why.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed partial class MacKeyboard : IKeyboard
{
    private const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";

    // kCGHIDEventTap: where the keyboard's own events come in.
    private const uint HidEventTap = 0;

    // NSEventTypeSystemDefined, and its subtype for the keyboard's special keys.
    private const ulong SystemDefined = 14;
    private const short SpecialKeys = 8;

    public static string NotAllowed => "macOS does not let HADA press keys yet: allow HADA in System Settings, Privacy & Security, Accessibility.";

    public string? Press(KeyCombination combination)
    {
        if (!IsAllowed())
        {
            return NotAllowed;
        }

        if (combination.Modifiers == KeyModifiers.None && MacKeys.MediaKeyOf(combination.VirtualKey) is { } media)
        {
            return PressMediaKey(media) ? null : "the media key could not be sent.";
        }

        ushort? key = combination.VirtualKey == 0 ? null : MacKeys.KeyCodeOf(combination.VirtualKey);
        if (combination.VirtualKey != 0 && key is null)
        {
            return "a Mac has no such key.";
        }

        // Modifiers first, each held while the next goes down; the key; then all let go in reverse.
        var modifiers = MacKeys.ModifiersOf(combination.Modifiers).ToList();
        ulong flags = 0;
        foreach (var (code, flag) in modifiers)
        {
            flags |= flag;
            Post(code, keyDown: true, flags);
        }

        if (key is { } pressed)
        {
            Post(pressed, keyDown: true, flags);
            Post(pressed, keyDown: false, flags);
        }

        for (var i = modifiers.Count - 1; i >= 0; i--)
        {
            flags &= ~modifiers[i].Flag;
            Post(modifiers[i].KeyCode, keyDown: false, flags);
        }

        return null;
    }

    /// <summary>Whether macOS lets this app send keys, as allowed under Accessibility.</summary>
    public static bool IsAllowed()
    {
        try
        {
            return AXIsProcessTrusted() != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Presses and lets go of one of the keyboard's media keys (NX_KEYTYPE_*), as a system-defined event.</summary>
    public static bool PressMediaKey(int key)
    {
        _ = NativeLibrary.TryLoad(AppKit, out _);
        var eventClass = objc_getClass("NSEvent");
        if (eventClass == IntPtr.Zero)
        {
            return false;
        }

        var pool = objc_autoreleasePoolPush();
        try
        {
            foreach (var down in new[] { true, false })
            {
                // data1: the key in the upper half, then 0xA when it goes down and 0xB when it comes up.
                var state = down ? 0xA : 0xB;
                var nsEvent = objc_msgSend_otherEvent(
                    eventClass,
                    sel_registerName("otherEventWithType:location:modifierFlags:timestamp:windowNumber:context:subtype:data1:data2:"),
                    SystemDefined,
                    default,
                    (ulong)(state << 8),
                    0,
                    0,
                    IntPtr.Zero,
                    SpecialKeys,
                    (key << 16) | (state << 8),
                    -1);
                var cgEvent = nsEvent == IntPtr.Zero ? IntPtr.Zero : objc_msgSend(nsEvent, sel_registerName("CGEvent"));
                if (cgEvent == IntPtr.Zero)
                {
                    return false;
                }

                CGEventPost(HidEventTap, cgEvent);
            }

            return true;
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    private static void Post(ushort keyCode, bool keyDown, ulong flags)
    {
        var keyEvent = CGEventCreateKeyboardEvent(IntPtr.Zero, keyCode, keyDown);
        if (keyEvent == IntPtr.Zero)
        {
            return;
        }

        try
        {
            CGEventSetFlags(keyEvent, flags);
            CGEventPost(HidEventTap, keyEvent);
        }
        finally
        {
            CFRelease(keyEvent);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CGPoint(double X, double Y);

    [DllImport(ApplicationServices)]
    private static extern byte AXIsProcessTrusted();

    [DllImport(CoreGraphics)]
    private static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort virtualKey, [MarshalAs(UnmanagedType.U1)] bool keyDown);

    [DllImport(CoreGraphics)]
    private static extern void CGEventSetFlags(IntPtr keyEvent, ulong flags);

    [DllImport(CoreGraphics)]
    private static extern void CGEventPost(uint tap, IntPtr keyEvent);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr value);

    [DllImport(ObjC, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(ObjC, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(ObjC)]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_otherEvent(
        IntPtr receiver, IntPtr selector, ulong type, CGPoint location, ulong modifierFlags, double timestamp, long windowNumber, IntPtr context,
        short subtype, long data1, long data2);

    [DllImport(ObjC)]
    private static extern IntPtr objc_autoreleasePoolPush();

    [DllImport(ObjC)]
    private static extern void objc_autoreleasePoolPop(IntPtr pool);
}

/// <summary>
/// The media buttons on a Mac: the keyboard's play, next and previous keys, which go to whatever plays. What plays
/// macOS keeps to itself, so there is no playback sensor and no media player here, and a Mac's keyboard has no stop key.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacMediaKeys : IMediaSessions
{
    public Task<MediaPlayback> ReadAsync(CancellationToken cancellationToken) =>
        Task.FromException<MediaPlayback>(new NotSupportedException("macOS does not tell apps what plays."));

    public bool Supports(string action) => action is MediaCommand.PlayPause or MediaCommand.Next or MediaCommand.Previous;

    public Task<bool> ControlAsync(string action, CancellationToken cancellationToken)
    {
        var key = action switch
        {
            MediaCommand.PlayPause => 16,
            MediaCommand.Next => 17,
            MediaCommand.Previous => 18,
            _ => -1,
        };
        return Task.FromResult(key >= 0 && MacKeyboard.IsAllowed() && MacKeyboard.PressMediaKey(key));
    }
}

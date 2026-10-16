using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using HADA.Core.Input;

namespace HADA.Platform.MacOS;

/// <summary>
/// Keyboard shortcuts that work whichever app is in front, through Carbon's hot keys, which macOS still keeps for
/// this and lets any app use without asking. Belongs to the main thread, which must create it, set it and dispose
/// of it: the presses arrive there.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed partial class MacHotkeys : IDisposable
{
    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";

    // kEventClassKeyboard and kEventHotKeyPressed; kEventParamDirectObject of typeEventHotKeyID.
    private const uint KeyboardClass = 0x6B657962; // 'keyb'
    private const uint HotKeyPressed = 5;
    private const uint DirectObject = 0x2D2D2D2D; // '----'
    private const uint HotKeyIdType = 0x686B6964; // 'hkid'
    private const uint Signature = 0x48414441; // 'HADA'

    // Carbon's modifier bits: Command, Shift, Option, Control.
    private const uint CommandKey = 0x100;
    private const uint ShiftKey = 0x200;
    private const uint OptionKey = 0x800;
    private const uint ControlKey = 0x1000;

    private readonly Dictionary<uint, Action> _actions = [];
    private readonly List<IntPtr> _registered = [];
    private readonly EventHandler _handler;
    private IntPtr _handlerRef;
    private uint _lastId;

    public MacHotkeys()
    {
        // Kept in a field: Carbon holds on to the pointer, the collector must not take the delegate away.
        _handler = OnHotKey;
        var types = new[] { new EventTypeSpec(KeyboardClass, HotKeyPressed) };
        _ = InstallEventHandler(GetApplicationEventTarget(), Marshal.GetFunctionPointerForDelegate(_handler), (nuint)types.Length, types, IntPtr.Zero, out _handlerRef);
    }

    private delegate int EventHandler(IntPtr callRef, IntPtr eventRef, IntPtr userData);

    /// <summary>
    /// Replaces every shortcut set so far. Returns those that could not be had, usually as another app holds them,
    /// or as a Mac's keyboard has no such key.
    /// </summary>
    public IReadOnlyList<string> Set(IEnumerable<(string Shortcut, Action Pressed)> shortcuts)
    {
        Clear();
        var refused = new List<string>();
        foreach (var (shortcut, pressed) in shortcuts)
        {
            if (!KeyCombination.TryParse(shortcut, out var keys) || keys.VirtualKey == 0 || MacKeys.KeyCodeOf(keys.VirtualKey) is not { } code)
            {
                refused.Add(shortcut);
                continue;
            }

            var id = ++_lastId;
            if (RegisterEventHotKey(code, ModifiersOf(keys.Modifiers), new EventHotKeyId(Signature, id), GetApplicationEventTarget(), 0, out var hotKey) == 0)
            {
                _registered.Add(hotKey);
                _actions[id] = pressed;
            }
            else
            {
                refused.Add(shortcut);
            }
        }

        return refused;
    }

    public void Dispose()
    {
        Clear();
        if (_handlerRef != IntPtr.Zero)
        {
            _ = RemoveEventHandler(_handlerRef);
            _handlerRef = IntPtr.Zero;
        }
    }

    /// <summary>Carbon's modifier bits for those of a combination.</summary>
    public static uint ModifiersOf(KeyModifiers modifiers) =>
        ((modifiers & KeyModifiers.Ctrl) != 0 ? ControlKey : 0)
        | ((modifiers & KeyModifiers.Alt) != 0 ? OptionKey : 0)
        | ((modifiers & KeyModifiers.Shift) != 0 ? ShiftKey : 0)
        | ((modifiers & KeyModifiers.Win) != 0 ? CommandKey : 0);

    private void Clear()
    {
        foreach (var hotKey in _registered)
        {
            _ = UnregisterEventHotKey(hotKey);
        }

        _registered.Clear();
        _actions.Clear();
    }

    private int OnHotKey(IntPtr callRef, IntPtr eventRef, IntPtr userData)
    {
        if (GetEventParameter(eventRef, DirectObject, HotKeyIdType, IntPtr.Zero, (nuint)Marshal.SizeOf<EventHotKeyId>(), IntPtr.Zero, out var hotKeyId) == 0
            && hotKeyId.Signature == Signature
            && _actions.TryGetValue(hotKeyId.Id, out var pressed))
        {
            pressed();
        }

        return 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct EventTypeSpec(uint EventClass, uint EventKind);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct EventHotKeyId(uint Signature, uint Id);

    [DllImport(Carbon)]
    private static extern IntPtr GetApplicationEventTarget();

    [DllImport(Carbon)]
    private static extern int InstallEventHandler(
        IntPtr target, IntPtr handler, nuint typeCount, [In] EventTypeSpec[] types, IntPtr userData, out IntPtr handlerRef);

    [DllImport(Carbon)]
    private static extern int RemoveEventHandler(IntPtr handlerRef);

    [DllImport(Carbon)]
    private static extern int RegisterEventHotKey(uint keyCode, uint modifiers, EventHotKeyId hotKeyId, IntPtr target, uint options, out IntPtr hotKey);

    [DllImport(Carbon)]
    private static extern int UnregisterEventHotKey(IntPtr hotKey);

    [DllImport(Carbon)]
    private static extern int GetEventParameter(
        IntPtr eventRef, uint name, uint desiredType, IntPtr actualType, nuint bufferSize, IntPtr actualSize, out EventHotKeyId data);
}

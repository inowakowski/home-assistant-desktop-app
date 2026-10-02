using System.Runtime.InteropServices;
using HADA.Platform.Windows.Interop;

namespace HADA.Platform.Windows.Actions;

/// <summary>Multimedia keys, as on a keyboard that has them.</summary>
public enum MediaKey : ushort
{
    NextTrack = 0xB0,
    PreviousTrack = 0xB1,
    Stop = 0xB2,
    PlayPause = 0xB3,
}

/// <summary>Injects keyboard and mouse input into the session of the calling process. Does not work from a service.</summary>
public static class InputSimulator
{
    private const uint KeyExtended = 0x0001;
    private const uint KeyUp = 0x0002;
    private const uint MouseMove = 0x0001;

    /// <summary>Presses and releases a multimedia key; whichever app handles such keys reacts.</summary>
    public static bool Press(MediaKey key)
    {
        ReadOnlySpan<Input> inputs =
        [
            Key((ushort)key, KeyExtended),
            Key((ushort)key, KeyExtended | KeyUp),
        ];
        return Send(inputs);
    }

    /// <summary>
    /// Moves the mouse pointer by <paramref name="pixels"/> and back, which Windows takes for someone using the
    /// computer: a screen that is off turns on, and the idle timers start over. Zero pixels checks only that
    /// input can be injected.
    /// </summary>
    public static bool NudgeMouse(int pixels = 1)
    {
        ReadOnlySpan<Input> inputs = [Move(pixels), Move(-pixels)];
        return Send(inputs);
    }

    private static Input Key(ushort virtualKey, uint flags) => new()
    {
        Type = Input.Keyboard,
        Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = virtualKey, Flags = flags } },
    };

    private static Input Move(int pixels) => new()
    {
        Type = Input.Mouse,
        Data = new InputUnion { Mouse = new MouseInput { X = pixels, Flags = MouseMove } },
    };

    private static unsafe bool Send(ReadOnlySpan<Input> inputs)
    {
        fixed (Input* first = inputs)
        {
            return NativeMethods.SendInput((uint)inputs.Length, first, Marshal.SizeOf<Input>()) == inputs.Length;
        }
    }
}

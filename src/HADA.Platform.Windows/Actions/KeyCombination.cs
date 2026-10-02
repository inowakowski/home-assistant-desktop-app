using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace HADA.Platform.Windows.Actions;

[Flags]
public enum KeyModifiers
{
    None = 0,
    Alt = 1,
    Ctrl = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>
/// A key with modifiers, as written in settings: <c>Ctrl+Alt+L</c>, <c>Win+Shift+S</c>, <c>F11</c>, <c>MediaNext</c>.
/// </summary>
/// <param name="VirtualKey">The Windows virtual-key code, or 0 for modifiers alone, e.g. <c>Win</c>.</param>
public readonly record struct KeyCombination(KeyModifiers Modifiers, ushort VirtualKey)
{
    private static readonly FrozenDictionary<string, ushort> NamedKeys = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
    {
        ["Backspace"] = 0x08,
        ["Tab"] = 0x09,
        ["Enter"] = 0x0D,
        ["Pause"] = 0x13,
        ["Esc"] = 0x1B,
        ["Escape"] = 0x1B,
        ["Space"] = 0x20,
        ["PageUp"] = 0x21,
        ["PageDown"] = 0x22,
        ["End"] = 0x23,
        ["Home"] = 0x24,
        ["Left"] = 0x25,
        ["Up"] = 0x26,
        ["Right"] = 0x27,
        ["Down"] = 0x28,
        ["PrintScreen"] = 0x2C,
        ["Insert"] = 0x2D,
        ["Delete"] = 0x2E,
        ["VolumeMute"] = 0xAD,
        ["VolumeDown"] = 0xAE,
        ["VolumeUp"] = 0xAF,
        ["MediaNext"] = 0xB0,
        ["MediaPrevious"] = 0xB1,
        ["MediaStop"] = 0xB2,
        ["MediaPlayPause"] = 0xB3,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, KeyModifiers> NamedModifiers = new Dictionary<string, KeyModifiers>(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = KeyModifiers.Ctrl,
        ["Control"] = KeyModifiers.Ctrl,
        ["Alt"] = KeyModifiers.Alt,
        ["Shift"] = KeyModifiers.Shift,
        ["Win"] = KeyModifiers.Win,
        ["Windows"] = KeyModifiers.Win,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the key is one of those Windows marks as "extended": the navigation block, the Windows keys and the
    /// multimedia keys. Injected input has to say so, or some apps take the key for its numeric-pad twin.
    /// </summary>
    public static bool IsExtended(ushort virtualKey) =>
        virtualKey is (>= 0x21 and <= 0x28) or 0x2C or 0x2D or 0x2E or 0x5B or 0x5C or (>= 0xAD and <= 0xB3);

    /// <summary>
    /// Parses parts joined by <c>+</c>: any of Ctrl, Alt, Shift and Win, then at most one key: a letter, a digit,
    /// F1 to F24, or one of the named keys such as Enter, Space, Delete, Left, PrintScreen, VolumeUp, MediaNext.
    /// </summary>
    public static bool TryParse([NotNullWhen(true)] string? text, out KeyCombination combination)
    {
        combination = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var modifiers = KeyModifiers.None;
        ushort key = 0;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries))
        {
            if (NamedModifiers.TryGetValue(part, out var modifier))
            {
                // "Ctrl+Ctrl" is a slip of the hand, not a combination.
                if ((modifiers & modifier) != 0)
                {
                    return false;
                }

                modifiers |= modifier;
            }
            else if (key == 0 && TryParseKey(part, out key))
            {
            }
            else
            {
                return false;
            }
        }

        combination = new KeyCombination(modifiers, key);
        return true;
    }

    /// <summary>The combination in the form <see cref="TryParse"/> reads, e.g. <c>Ctrl+Alt+L</c>.</summary>
    public override string ToString()
    {
        var parts = new List<string>(5);
        foreach (var (modifier, name) in new[] { (KeyModifiers.Ctrl, "Ctrl"), (KeyModifiers.Alt, "Alt"), (KeyModifiers.Shift, "Shift"), (KeyModifiers.Win, "Win") })
        {
            if ((Modifiers & modifier) != 0)
            {
                parts.Add(name);
            }
        }

        if (VirtualKey != 0)
        {
            parts.Add(KeyName(VirtualKey));
        }

        return string.Join('+', parts);
    }

    private static bool TryParseKey(string part, out ushort key)
    {
        key = 0;
        if (part.Length == 1 && char.IsAsciiLetterOrDigit(part[0]))
        {
            // Letters and digits have their uppercase character code as their virtual-key code.
            key = char.ToUpperInvariant(part[0]);
            return true;
        }

        if (part.Length is 2 or 3
            && part[0] is 'F' or 'f'
            && int.TryParse(part.AsSpan(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
            && number is >= 1 and <= 24)
        {
            key = (ushort)(0x70 + number - 1);
            return true;
        }

        return NamedKeys.TryGetValue(part, out key);
    }

    private static string KeyName(ushort key)
    {
        if (key is (>= '0' and <= '9') or (>= 'A' and <= 'Z'))
        {
            return ((char)key).ToString();
        }

        if (key is >= 0x70 and <= 0x87)
        {
            return "F" + (key - 0x70 + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        // "Esc" and "Escape" name the same key; the first name found is as good as the other.
        return NamedKeys.First(pair => pair.Value == key).Key;
    }
}

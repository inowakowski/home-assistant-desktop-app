using System.Globalization;
using System.Text;

namespace HADA.Ipc;

/// <summary>Where a custom sensor's value comes from.</summary>
public enum CustomSensorType
{
    /// <summary>A fixed text typed in the settings window, e.g. the room this computer stands in.</summary>
    Text,

    /// <summary>On while a process with the given name is running. Shows up as a binary sensor.</summary>
    ProcessRunning,

    /// <summary>The output of a PowerShell command, run by the service at a fixed interval.</summary>
    PowerShell,

    /// <summary>
    /// On while a device whose instance id contains the given text is connected, e.g. <c>VID_0BDA&amp;PID_8153</c>
    /// for a USB-C dock's network adapter. Shows up as a binary sensor.
    /// </summary>
    DeviceConnected,

    /// <summary>
    /// A button in Home Assistant that runs a PowerShell command when pressed. The service runs it, so it has the
    /// service's rights and no desktop.
    /// </summary>
    CommandButton,

    /// <summary>
    /// A button in Home Assistant that starts a program, opens a document or opens an address when pressed, on
    /// the desktop of the signed-in user.
    /// </summary>
    LaunchButton,

    /// <summary>
    /// A button in Home Assistant that presses a key combination, such as <c>Ctrl+Shift+M</c>, on the desktop of the
    /// signed-in user.
    /// </summary>
    KeysButton,

    /// <summary>
    /// The other direction: an entry in the tray icon's menu, optionally with a keyboard shortcut, that Home
    /// Assistant can start an automation from. Shows up as a trigger of the device.
    /// </summary>
    QuickAction,
}

/// <summary>
/// A sensor or button defined by the user in the settings window (or under <c>CustomSensors:Items</c> in appsettings.json).
/// </summary>
public sealed record CustomSensorDefinition
{
    public const int MinIntervalSeconds = 2;
    public const int MaxIntervalSeconds = 86400;
    public const int DefaultIntervalSeconds = 30;

    /// <summary>Entity id: lowercase letters, digits and underscores. Derived from <see cref="Name"/> when empty.</summary>
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public CustomSensorType Type { get; init; }

    /// <summary>Whether sensors of this type are on or off, as opposed to having a text or a number as their value.</summary>
    public bool IsBinary => Type is CustomSensorType.ProcessRunning or CustomSensorType.DeviceConnected;

    /// <summary>Whether this is something Home Assistant presses, as opposed to a sensor that reports a value.</summary>
    public bool IsButton => Type is CustomSensorType.CommandButton or CustomSensorType.LaunchButton or CustomSensorType.KeysButton;

    /// <summary>Whether this is something the user does on the computer, for Home Assistant to react to.</summary>
    public bool IsTrigger => Type == CustomSensorType.QuickAction;

    /// <summary>
    /// The text, the process name, the PowerShell command, the device id, the program to start or the key
    /// combination, depending on <see cref="Type"/>. For a quick action it is its keyboard shortcut, and may be empty.
    /// </summary>
    public string Value { get; init; } = string.Empty;

    /// <summary>Unit of measurement. Set it only for numeric values; Home Assistant then treats the sensor as a measurement.</summary>
    public string Unit { get; init; } = string.Empty;

    /// <summary>How often a process is looked for or a command is run. Ignored for <see cref="CustomSensorType.Text"/>.</summary>
    public int IntervalSeconds { get; init; } = DefaultIntervalSeconds;

    /// <summary>Trims every field and fills in an empty id from the name.</summary>
    public CustomSensorDefinition Normalize()
    {
        var name = Name.Trim();
        var id = Id.Trim();
        return this with
        {
            Id = id.Length > 0 ? id : ToId(name),
            Name = name,
            Value = Value.Trim(),
            Unit = IsBinary || IsButton || IsTrigger ? string.Empty : Unit.Trim(),
        };
    }

    /// <summary>Turns a display name into an entity id, e.g. <c>Gra włączona</c> becomes <c>gra_wlaczona</c>.</summary>
    public static string ToId(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            // The Polish ł has no decomposed form, so strip its stroke by hand.
            var lower = char.ToLowerInvariant(c) == 'ł' ? 'l' : char.ToLowerInvariant(c);
            if (char.IsAsciiLetterOrDigit(lower))
            {
                builder.Append(lower);
            }
            else if (builder.Length > 0 && builder[^1] != '_')
            {
                builder.Append('_');
            }
        }

        return builder.ToString().TrimEnd('_');
    }
}

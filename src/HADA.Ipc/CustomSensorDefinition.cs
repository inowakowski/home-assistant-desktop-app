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
}

/// <summary>A sensor defined by the user in the settings window (or under <c>CustomSensors:Items</c> in appsettings.json).</summary>
public sealed record CustomSensorDefinition
{
    public const int MinIntervalSeconds = 2;
    public const int MaxIntervalSeconds = 86400;
    public const int DefaultIntervalSeconds = 30;

    /// <summary>Entity id: lowercase letters, digits and underscores. Derived from <see cref="Name"/> when empty.</summary>
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public CustomSensorType Type { get; init; }

    /// <summary>The text, the process name or the PowerShell command, depending on <see cref="Type"/>.</summary>
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
            Unit = Type == CustomSensorType.ProcessRunning ? string.Empty : Unit.Trim(),
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

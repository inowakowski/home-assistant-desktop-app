using System.Globalization;

namespace HADA.Core.Entities;

/// <summary>What Home Assistant may send to an entity, the same for every engine.</summary>
public static class CommandValue
{
    /// <summary>Longer notification messages are cut off.</summary>
    public const int MaxMessageLength = 4096;

    /// <summary>
    /// Checks <paramref name="raw"/> against what the entity accepts and brings it into the form action handlers
    /// expect: <c>on</c> or <c>off</c> for a switch, an invariant-culture number within the entity's range for a
    /// number, the text for a notification, and nothing for a button.
    /// </summary>
    public static bool TryNormalize(EntityDescriptor entity, string? raw, out string? value)
    {
        ArgumentNullException.ThrowIfNull(entity);

        value = null;
        raw ??= string.Empty;
        switch (entity.Kind)
        {
            case EntityKind.Button:
                return true;
            case EntityKind.Switch:
                value = raw.Trim().ToLowerInvariant();
                return value is BinaryState.On or BinaryState.Off;
            case EntityKind.Number:
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                    || !double.IsFinite(number)
                    || number < (entity.Min ?? double.MinValue)
                    || number > (entity.Max ?? double.MaxValue))
                {
                    return false;
                }

                value = number.ToString(CultureInfo.InvariantCulture);
                return true;
            case EntityKind.Notify:
                value = raw.Length > MaxMessageLength ? raw[..MaxMessageLength] : raw;
                return !string.IsNullOrWhiteSpace(raw);
            default:
                return false;
        }
    }
}

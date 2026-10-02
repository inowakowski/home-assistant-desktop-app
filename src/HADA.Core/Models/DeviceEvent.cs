namespace HADA.Core.Models;

/// <summary>
/// Something that happened on the computer and that Home Assistant may want to react to: a quick action was
/// chosen, a button of a notification was pressed. Unlike a <see cref="TelemetryEvent"/> it is not a state; it
/// happens, and is gone. Whoever produces one publishes it to the <see cref="Abstractions.IEventBus"/>;
/// communication engines pass it on.
/// </summary>
public sealed record DeviceEvent
{
    /// <summary>A quick action was chosen from the tray menu or by its keyboard shortcut; <see cref="Value"/> is its entity id.</summary>
    public const string QuickAction = "quick_action";

    /// <summary>A button of a notification was pressed; <see cref="Value"/> is the action the notification gave that button.</summary>
    public const string NotificationAction = "notification_action";

    private const int MaxValueLength = 64;

    /// <summary>What kind of thing happened: lowercase letters, digits and underscores, as it becomes part of an MQTT topic.</summary>
    public required string Name { get; init; }

    /// <summary>Which one: an id made of letters, digits, <c>_</c>, <c>-</c> and <c>.</c>, at most 64 characters.</summary>
    public required string Value { get; init; }

    /// <summary>Process that produced the event (e.g. the tray app), if known.</summary>
    public string? Source { get; init; }

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Whether name and value are of the shape described above. Events arrive from the tray app, which the service
    /// does not trust, and end up in topics and payloads.
    /// </summary>
    public bool IsWellFormed =>
        Name is QuickAction or NotificationAction
        && Value.Length is > 0 and <= MaxValueLength
        && !Value.AsSpan().ContainsAnyExcept(IdCharacters);

    private static System.Buffers.SearchValues<char> IdCharacters { get; } =
        System.Buffers.SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-.");
}

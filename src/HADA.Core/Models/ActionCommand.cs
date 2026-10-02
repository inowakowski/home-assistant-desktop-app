using System.Collections.Frozen;
using System.Text.Json;

namespace HADA.Core.Models;

/// <summary>
/// A command received from Home Assistant to be executed locally.
/// Communication engines publish these to the <see cref="Abstractions.IEventBus"/>; action handlers consume them.
/// </summary>
public sealed record ActionCommand
{
    /// <summary>Id of the entity the command is for, e.g. <c>lock_screen</c> or <c>volume_level</c>.</summary>
    public required string ActionId { get; init; }

    /// <summary>
    /// What the entity is set to or sent: <c>on</c> or <c>off</c> for a switch, the number for a number, the
    /// message for a notification. Empty for a button press.
    /// </summary>
    public string? Value { get; init; }

    /// <summary>Further arguments, e.g. the <c>title</c> of a notification.</summary>
    public IReadOnlyDictionary<string, object?> Parameters { get; init; } = FrozenDictionary<string, object?>.Empty;

    /// <summary>Name of the engine that received the command (e.g. MQTT or WebSocket), if known.</summary>
    public string? Origin { get; init; }

    /// <summary>Correlates a result back to the originating request, when the engine supports it.</summary>
    public string? CorrelationId { get; init; }

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    /// A text parameter, or <see langword="null"/> when there is none. A command that crossed the pipe to the tray
    /// app has its parameters as JSON values, which this reads as well.
    /// </summary>
    public string? GetParameter(string name) =>
        !Parameters.TryGetValue(name, out var value) ? null
        : value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
            _ => null,
        };
}

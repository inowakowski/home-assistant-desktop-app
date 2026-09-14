using System.Collections.Frozen;

namespace HADA.Core.Models;

/// <summary>
/// A command received from Home Assistant to be executed locally.
/// Communication engines publish these to the <see cref="Abstractions.IEventBus"/>; action handlers consume them.
/// </summary>
public sealed record ActionCommand
{
    /// <summary>Stable action identifier, e.g. <c>power.shutdown</c> or <c>audio.set_volume</c>.</summary>
    public required string ActionId { get; init; }

    /// <summary>Action arguments, e.g. <c>{ "level": 40 }</c>.</summary>
    public IReadOnlyDictionary<string, object?> Parameters { get; init; } = FrozenDictionary<string, object?>.Empty;

    /// <summary>Name of the engine that received the command (e.g. MQTT or WebSocket), if known.</summary>
    public string? Origin { get; init; }

    /// <summary>Correlates a result back to the originating request, when the engine supports it.</summary>
    public string? CorrelationId { get; init; }

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    public Guid Id { get; init; } = Guid.NewGuid();
}

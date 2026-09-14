using System.Collections.Frozen;

namespace HADA.Core.Models;

/// <summary>
/// A state reading produced by a sensor, destined for Home Assistant.
/// Sensors publish these to the <see cref="Abstractions.IEventBus"/>; communication engines forward them.
/// </summary>
public sealed record TelemetryEvent
{
    /// <summary>Stable sensor identifier, e.g. <c>active_window</c> or <c>cpu_load</c>.</summary>
    public required string SensorId { get; init; }

    /// <summary>Sensor state. Home Assistant states are strings.</summary>
    public required string State { get; init; }

    /// <summary>Optional extra attributes reported alongside the state.</summary>
    public IReadOnlyDictionary<string, object?> Attributes { get; init; } = FrozenDictionary<string, object?>.Empty;

    /// <summary>Process that produced the reading (e.g. Service or Tray), if known.</summary>
    public string? Source { get; init; }

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    public Guid Id { get; init; } = Guid.NewGuid();
}

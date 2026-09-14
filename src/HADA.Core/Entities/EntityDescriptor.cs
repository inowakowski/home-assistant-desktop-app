namespace HADA.Core.Entities;

public enum EntityKind
{
    /// <summary>Reports a state via <see cref="Models.TelemetryEvent"/>.</summary>
    Sensor,

    /// <summary>Triggers an <see cref="Models.ActionCommand"/> when pressed in Home Assistant.</summary>
    Button,
}

/// <summary>
/// Transport-agnostic description of something exposed to Home Assistant.
/// Engines translate it into their own format, e.g. an MQTT discovery config.
/// </summary>
public sealed record EntityDescriptor
{
    /// <summary>
    /// Unique within this device; lowercase letters, digits and underscores only.
    /// Matches <see cref="Models.TelemetryEvent.SensorId"/> or <see cref="Models.ActionCommand.ActionId"/>.
    /// </summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required EntityKind Kind { get; init; }

    /// <summary>Material Design icon, e.g. <c>mdi:cpu-64-bit</c>.</summary>
    public string? Icon { get; init; }

    /// <summary>Home Assistant device class, e.g. <c>temperature</c>.</summary>
    public string? DeviceClass { get; init; }

    public string? UnitOfMeasurement { get; init; }

    /// <summary>Home Assistant state class, e.g. <c>measurement</c>, which enables long-term statistics.</summary>
    public string? StateClass { get; init; }
}

/// <summary>Published on the event bus whenever an entity is added to the registry.</summary>
public sealed record EntityRegistered(EntityDescriptor Entity);

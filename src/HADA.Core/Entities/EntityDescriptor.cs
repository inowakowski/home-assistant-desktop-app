namespace HADA.Core.Entities;

public enum EntityKind
{
    /// <summary>Reports a state via <see cref="Models.TelemetryEvent"/>.</summary>
    Sensor,

    /// <summary>Triggers an <see cref="Models.ActionCommand"/> when pressed in Home Assistant.</summary>
    Button,

    /// <summary>
    /// Reports <see cref="BinaryState.On"/> or <see cref="BinaryState.Off"/> via <see cref="Models.TelemetryEvent"/>.
    /// </summary>
    BinarySensor,
}

/// <summary>The two states of an <see cref="EntityKind.BinarySensor"/>, as Home Assistant spells them.</summary>
public static class BinaryState
{
    public const string On = "on";
    public const string Off = "off";

    public static string From(bool value) => value ? On : Off;
}

public static class EntityKindExtensions
{
    /// <summary>Whether entities of this kind publish readings, as opposed to receiving commands.</summary>
    public static bool ReportsState(this EntityKind kind) => kind is EntityKind.Sensor or EntityKind.BinarySensor;
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

/// <summary>A change to the entity registry, published on the event bus in the order the changes were made.</summary>
public abstract record EntityRegistryChange(EntityDescriptor Entity);

/// <summary>Published on the event bus whenever an entity is added to the registry or replaced.</summary>
public sealed record EntityRegistered(EntityDescriptor Entity) : EntityRegistryChange(Entity);

/// <summary>Published on the event bus when an entity is removed; engines then remove it from Home Assistant.</summary>
public sealed record EntityUnregistered(EntityDescriptor Entity) : EntityRegistryChange(Entity);

/// <summary>
/// Published on the event bus when an entity's source goes away or comes back, e.g. the tray app exits or
/// reconnects. Engines then show the entity as unavailable in Home Assistant instead of leaving its last value.
/// </summary>
public sealed record EntityAvailabilityChanged(EntityDescriptor Entity, bool IsAvailable) : EntityRegistryChange(Entity);

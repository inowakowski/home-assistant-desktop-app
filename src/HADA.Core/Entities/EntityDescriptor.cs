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

    /// <summary>
    /// Reports <see cref="BinaryState.On"/> or <see cref="BinaryState.Off"/> and can be turned on and off from
    /// Home Assistant: the <see cref="Models.ActionCommand.Value"/> of its commands is the wanted state.
    /// </summary>
    Switch,

    /// <summary>
    /// Reports a number and can be set from Home Assistant, within <see cref="EntityDescriptor.Min"/> and
    /// <see cref="EntityDescriptor.Max"/>: the <see cref="Models.ActionCommand.Value"/> of its commands is the wanted number.
    /// </summary>
    Number,

    /// <summary>Receives messages from Home Assistant; <see cref="Models.ActionCommand.Value"/> is the message text.</summary>
    Notify,

    /// <summary>
    /// Something the user does on the computer that Home Assistant can start an automation from, such as a quick
    /// action. It has no state and takes no commands; a <see cref="Models.DeviceEvent"/> carrying its id says it happened.
    /// </summary>
    Trigger,
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
    public static bool ReportsState(this EntityKind kind) =>
        kind is EntityKind.Sensor or EntityKind.BinarySensor or EntityKind.Switch or EntityKind.Number;

    /// <summary>Whether Home Assistant can send commands to entities of this kind.</summary>
    public static bool AcceptsCommands(this EntityKind kind) =>
        kind is EntityKind.Button or EntityKind.Switch or EntityKind.Number or EntityKind.Notify;

    /// <summary>Whether the state of entities of this kind is <see cref="BinaryState.On"/> or <see cref="BinaryState.Off"/>.</summary>
    public static bool IsBinary(this EntityKind kind) => kind is EntityKind.BinarySensor or EntityKind.Switch;
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

    /// <summary>Smallest value of an <see cref="EntityKind.Number"/>.</summary>
    public double? Min { get; init; }

    /// <summary>Largest value of an <see cref="EntityKind.Number"/>.</summary>
    public double? Max { get; init; }

    /// <summary>Step between the values of an <see cref="EntityKind.Number"/>.</summary>
    public double? Step { get; init; }

    /// <summary>
    /// False for entities that are exposed to Home Assistant only after the user switched them on, such as the
    /// buttons that shut the computer down.
    /// </summary>
    public bool EnabledByDefault { get; init; } = true;
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

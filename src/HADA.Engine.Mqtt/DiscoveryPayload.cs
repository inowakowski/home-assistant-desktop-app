namespace HADA.Engine.Mqtt;

/// <summary>Home Assistant MQTT discovery config; serialized with snake_case names and nulls omitted.</summary>
internal sealed record DiscoveryPayload(
    string Name,
    string UniqueId,
    DiscoveryAvailability[] Availability,
    string AvailabilityMode,
    string? StateTopic,
    string? JsonAttributesTopic,
    string? CommandTopic,
    string? PayloadPress,
    string? PayloadOn,
    string? PayloadOff,
    string? Icon,
    string? DeviceClass,
    string? UnitOfMeasurement,
    string? StateClass,
    double? Min,
    double? Max,
    double? Step,
    DiscoveryDevice Device);

/// <summary>
/// Discovery config of a device trigger: Home Assistant offers it as a trigger of the device, and fires it when
/// <paramref name="Payload"/> arrives on <paramref name="Topic"/>.
/// </summary>
/// <param name="Type">How Home Assistant words it; <c>button_short_press</c> reads as "… pressed".</param>
/// <param name="Subtype">What was pressed, in the user's words.</param>
internal sealed record TriggerDiscoveryPayload(
    string AutomationType,
    string Topic,
    string Type,
    string Subtype,
    string Payload,
    DiscoveryDevice Device);

/// <summary>A topic carrying <c>online</c> or <c>offline</c>.</summary>
internal sealed record DiscoveryAvailability(string Topic);

internal sealed record DiscoveryDevice(
    string[] Identifiers,
    string Name,
    string Manufacturer,
    string Model,
    string? SwVersion);

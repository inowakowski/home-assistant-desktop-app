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
    DiscoveryDevice Device);

/// <summary>A topic carrying <c>online</c> or <c>offline</c>.</summary>
internal sealed record DiscoveryAvailability(string Topic);

internal sealed record DiscoveryDevice(
    string[] Identifiers,
    string Name,
    string Manufacturer,
    string Model,
    string? SwVersion);

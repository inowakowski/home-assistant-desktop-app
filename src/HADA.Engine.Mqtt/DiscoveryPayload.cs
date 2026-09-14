namespace HADA.Engine.Mqtt;

/// <summary>Home Assistant MQTT discovery config; serialized with snake_case names and nulls omitted.</summary>
internal sealed record DiscoveryPayload(
    string Name,
    string UniqueId,
    string AvailabilityTopic,
    string? StateTopic,
    string? JsonAttributesTopic,
    string? CommandTopic,
    string? PayloadPress,
    string? Icon,
    string? DeviceClass,
    string? UnitOfMeasurement,
    string? StateClass,
    DiscoveryDevice Device);

internal sealed record DiscoveryDevice(
    string[] Identifiers,
    string Name,
    string Manufacturer,
    string Model,
    string? SwVersion);

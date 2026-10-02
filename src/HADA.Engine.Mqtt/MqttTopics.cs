using System.Diagnostics.CodeAnalysis;
using HADA.Core.Entities;

namespace HADA.Engine.Mqtt;

/// <summary>
/// Topic layout: <c>{base}/{device}/availability</c>, <c>{base}/{device}/{entity}/state|set</c>,
/// <c>{base}/{device}/event/{name}</c>.
/// </summary>
internal sealed class MqttTopics
{
    private const string CommandSuffix = "/set";

    private readonly string _discoveryPrefix;
    private readonly string _devicePrefix;

    public MqttTopics(string discoveryPrefix, string baseTopic, string deviceId)
    {
        _discoveryPrefix = discoveryPrefix;
        _devicePrefix = $"{baseTopic}/{deviceId}/";
        DeviceId = deviceId;
        Availability = _devicePrefix + "availability";
        CommandFilter = _devicePrefix + "+" + CommandSuffix;
        HomeAssistantStatus = $"{discoveryPrefix}/status";
    }

    public string DeviceId { get; }

    public string Availability { get; }

    public string CommandFilter { get; }

    /// <summary>Home Assistant publishes <c>online</c> here after it (re)starts.</summary>
    public string HomeAssistantStatus { get; }

    public string State(string entityId) => _devicePrefix + entityId + "/state";

    public string Attributes(string entityId) => _devicePrefix + entityId + "/attributes";

    public string Command(string entityId) => _devicePrefix + entityId + CommandSuffix;

    /// <summary>Whether the entity's own source is there, e.g. the tray app for session sensors.</summary>
    public string EntityAvailability(string entityId) => _devicePrefix + entityId + "/availability";

    /// <summary>Where a kind of <see cref="Core.Models.DeviceEvent"/> is published; the payload says which one happened.</summary>
    public string Event(string name) => _devicePrefix + "event/" + name;

    public string Discovery(EntityDescriptor entity) =>
        $"{_discoveryPrefix}/{Component(entity.Kind)}/{DeviceId}/{entity.Id}/config";

    public bool TryGetCommandEntityId(string topic, [NotNullWhen(true)] out string? entityId)
    {
        if (topic.Length > _devicePrefix.Length + CommandSuffix.Length
            && topic.StartsWith(_devicePrefix, StringComparison.Ordinal)
            && topic.EndsWith(CommandSuffix, StringComparison.Ordinal))
        {
            entityId = topic[_devicePrefix.Length..^CommandSuffix.Length];
            return !entityId.Contains('/');
        }

        entityId = null;
        return false;
    }

    /// <summary>Makes a value safe for a topic segment, e.g. <c>DESKTOP-01</c> becomes <c>desktop_01</c>.</summary>
    public static string ToTopicSegment(string value) =>
        string.Create(value.Length, value, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var c = char.ToLowerInvariant(source[i]);
                span[i] = char.IsAsciiLetterOrDigit(c) ? c : '_';
            }
        });

    private static string Component(EntityKind kind) => kind switch
    {
        EntityKind.Sensor => "sensor",
        EntityKind.Button => "button",
        EntityKind.BinarySensor => "binary_sensor",
        EntityKind.Switch => "switch",
        EntityKind.Number => "number",
        EntityKind.Notify => "notify",
        EntityKind.Trigger => "device_automation",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}

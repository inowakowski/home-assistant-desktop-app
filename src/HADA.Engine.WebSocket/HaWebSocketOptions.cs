using HADA.Core.Entities;

namespace HADA.Engine.WebSocket;

public sealed class HaWebSocketOptions
{
    public const string SectionName = "HomeAssistant";

    /// <summary>
    /// Tells this Home Assistant apart from the others the computer is connected to: lowercase letters and digits,
    /// given once and never changed. The one of the <c>HomeAssistant</c> section has <see cref="DefaultId"/>.
    /// </summary>
    public string? Id { get; set; }

    /// <summary>What the user calls this Home Assistant, e.g. the place it is in. Shown in the window and in the log.</summary>
    public string? Name { get; set; }

    public const string DefaultId = "default";

    /// <summary>Home Assistant base URL, e.g. <c>http://homeassistant.local:8123</c>. The engine stays idle while this is empty.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Long-lived access token (Home Assistant profile, Security tab). It must belong to an administrator,
    /// because non-admin users cannot subscribe to custom events. Keep it out of appsettings.json:
    /// use <c>dotnet user-secrets</c> during development or the <c>HomeAssistant__AccessToken</c> environment variable.
    /// </summary>
    public string? AccessToken { get; set; }

    /// <summary>Used in entity ids (<c>sensor.{DeviceId}_{entity}</c>) and to target commands. Defaults to the machine name.</summary>
    public string? DeviceId { get; set; }

    /// <summary>Prefix for entity friendly names. Defaults to the machine name.</summary>
    public string? DeviceName { get; set; }

    /// <summary>
    /// Registers the computer with Home Assistant's <c>mobile_app</c> integration, as a phone's companion app
    /// does. Home Assistant then has the action <c>notify.mobile_app_{device name}</c>, whose notifications
    /// arrive over this connection. Works with the token of any user, not only an administrator's.
    /// </summary>
    public bool Notifications { get; set; }

    /// <summary>How sensors reach this Home Assistant, if at all.</summary>
    public HomeAssistantSensorMode SensorMode { get; set; } = HomeAssistantSensorMode.States;

    /// <summary>
    /// Event type carrying commands, fired from Home Assistant with data
    /// <c>{ "device_id": "...", "action": "lock_screen" }</c>.
    /// </summary>
    public string CommandEventType { get; set; } = "hada_command";

    /// <summary>
    /// Event type fired in Home Assistant when something happens on the computer, with data
    /// <c>{ "device_id": "...", "name": "quick_action", "value": "..." }</c>.
    /// </summary>
    public string DeviceEventType { get; set; } = "hada_event";

    /// <summary>A ping is sent at this interval; the connection is dropped if nothing arrives for two intervals.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How often the HADA integration is looked for again while Home Assistant does not have it.</summary>
    public TimeSpan IntegrationRetryInterval { get; set; } = TimeSpan.FromMinutes(1);

    public TimeSpan MinReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan MaxReconnectDelay { get; set; } = TimeSpan.FromMinutes(1);
}

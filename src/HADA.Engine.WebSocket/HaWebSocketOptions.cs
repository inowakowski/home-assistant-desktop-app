namespace HADA.Engine.WebSocket;

public sealed class HaWebSocketOptions
{
    public const string SectionName = "HomeAssistant";

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

    public TimeSpan MinReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan MaxReconnectDelay { get; set; } = TimeSpan.FromMinutes(1);
}

namespace HADA.Engine.Mqtt;

public sealed class MqttOptions
{
    public const string SectionName = "Mqtt";

    /// <summary>Broker host name or IP address. The engine stays idle while this is empty.</summary>
    public string? Host { get; set; }

    public int Port { get; set; } = 1883;

    public bool UseTls { get; set; }

    public string? Username { get; set; }

    /// <summary>
    /// Keep this out of appsettings.json: use <c>dotnet user-secrets</c> during development
    /// or the <c>Mqtt__Password</c> environment variable.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>Identifies this computer in topics and Home Assistant unique ids. Defaults to the machine name.</summary>
    public string? DeviceId { get; set; }

    /// <summary>Device name shown in Home Assistant. Defaults to the machine name.</summary>
    public string? DeviceName { get; set; }

    public string DiscoveryPrefix { get; set; } = "homeassistant";

    public string BaseTopic { get; set; } = "hada";

    public TimeSpan MinReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan MaxReconnectDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// A connection that lasted at least this long counts as working, and the next reconnect starts from
    /// <see cref="MinReconnectDelay"/> again. Shorter ones make the delay grow.
    /// </summary>
    public TimeSpan StableConnectionTime { get; set; } = TimeSpan.FromSeconds(30);
}

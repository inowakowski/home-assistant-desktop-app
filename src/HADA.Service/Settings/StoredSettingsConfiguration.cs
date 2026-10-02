using System.Globalization;

namespace HADA.Service.Settings;

/// <summary>Adds saved settings to configuration. Added after appsettings.json, so saved values win.</summary>
public sealed class StoredSettingsConfigurationSource(SettingsStore store) : IConfigurationSource
{
    public StoredSettingsConfigurationProvider Provider { get; } = new(store);

    public IConfigurationProvider Build(IConfigurationBuilder builder) => Provider;
}

public sealed class StoredSettingsConfigurationProvider(SettingsStore store) : ConfigurationProvider
{
    public override void Load() => Data = ToConfiguration(store.Load());

    /// <summary>Re-reads the file and notifies options monitors, which makes the affected engines restart.</summary>
    public void Reload()
    {
        Load();
        OnReload();
    }

    public static Dictionary<string, string?> ToConfiguration(StoredSettings settings)
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        if (settings.Mqtt is { } mqtt)
        {
            data["Mqtt:Host"] = mqtt.Host;
            data["Mqtt:Port"] = mqtt.Port.ToString(CultureInfo.InvariantCulture);
            data["Mqtt:UseTls"] = mqtt.UseTls ? "true" : "false";
            data["Mqtt:Username"] = mqtt.Username;
            data["Mqtt:DeviceId"] = mqtt.DeviceId;
            data["Mqtt:DeviceName"] = mqtt.DeviceName;
            data["Mqtt:DiscoveryPrefix"] = mqtt.DiscoveryPrefix;
            data["Mqtt:BaseTopic"] = mqtt.BaseTopic;

            // A saved section owns its secret too, so clearing it in the window also overrides appsettings.json.
            data["Mqtt:Password"] = SettingsStore.TryUnprotect(settings.MqttPassword) ?? string.Empty;
        }

        if (settings.HomeAssistant is { } homeAssistant)
        {
            data["HomeAssistant:BaseUrl"] = homeAssistant.BaseUrl;
            data["HomeAssistant:DeviceId"] = homeAssistant.DeviceId;
            data["HomeAssistant:DeviceName"] = homeAssistant.DeviceName;
            data["HomeAssistant:CommandEventType"] = homeAssistant.CommandEventType;
            data["HomeAssistant:AccessToken"] = SettingsStore.TryUnprotect(settings.AccessToken) ?? string.Empty;
        }

        if (settings.DisabledEntities is { } disabled)
        {
            for (var i = 0; i < disabled.Count; i++)
            {
                data[$"{EntityOptions.SectionName}:Disabled:{i}"] = disabled[i];
            }
        }

        if (settings.EnabledEntities is { } enabled)
        {
            for (var i = 0; i < enabled.Count; i++)
            {
                data[$"{EntityOptions.SectionName}:Enabled:{i}"] = enabled[i];
            }
        }

        if (settings.CustomSensors is { } customSensors)
        {
            for (var i = 0; i < customSensors.Count; i++)
            {
                var sensor = customSensors[i];
                var prefix = $"{CustomSensorOptions.SectionName}:Items:{i}:";
                data[prefix + nameof(sensor.Id)] = sensor.Id;
                data[prefix + nameof(sensor.Name)] = sensor.Name;
                data[prefix + nameof(sensor.Type)] = sensor.Type.ToString();
                data[prefix + nameof(sensor.Value)] = sensor.Value;
                data[prefix + nameof(sensor.Unit)] = sensor.Unit;
                data[prefix + nameof(sensor.IntervalSeconds)] = sensor.IntervalSeconds.ToString(CultureInfo.InvariantCulture);
            }
        }

        return data;
    }
}

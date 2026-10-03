using System.Globalization;
using HADA.Ipc;

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

        if (settings.GetMqttServers() is { } servers)
        {
            // Saved servers replace whatever appsettings.json lists, down to there being none.
            data[$"{MqttServersOptions.SectionName}:{nameof(MqttServersOptions.Count)}"] = servers.Count.ToString(CultureInfo.InvariantCulture);
            for (var i = 0; i < servers.Count; i++)
            {
                var mqtt = servers[i].Settings;
                var prefix = $"{MqttServersOptions.SectionName}:{nameof(MqttServersOptions.Items)}:{i}:";
                data[prefix + nameof(mqtt.Id)] = mqtt.Id;
                data[prefix + nameof(mqtt.Name)] = mqtt.Name;
                data[prefix + nameof(mqtt.Host)] = mqtt.Host;
                data[prefix + nameof(mqtt.Port)] = mqtt.Port.ToString(CultureInfo.InvariantCulture);
                data[prefix + nameof(mqtt.UseTls)] = mqtt.UseTls ? "true" : "false";
                data[prefix + nameof(mqtt.Username)] = mqtt.Username;
                data[prefix + nameof(mqtt.DeviceId)] = mqtt.DeviceId;
                data[prefix + nameof(mqtt.DeviceName)] = mqtt.DeviceName;
                data[prefix + nameof(mqtt.DiscoveryPrefix)] = mqtt.DiscoveryPrefix;
                data[prefix + nameof(mqtt.BaseTopic)] = mqtt.BaseTopic;

                // A saved server owns its secret too, so clearing it in the window also overrides appsettings.json.
                data[prefix + "Password"] = SettingsStore.TryUnprotect(servers[i].Password) ?? string.Empty;
            }
        }

        if (settings.GetHomeAssistantServers() is { } homeAssistants)
        {
            data[$"{HomeAssistantServersOptions.SectionName}:{nameof(HomeAssistantServersOptions.Count)}"] =
                homeAssistants.Count.ToString(CultureInfo.InvariantCulture);
            for (var i = 0; i < homeAssistants.Count; i++)
            {
                var homeAssistant = homeAssistants[i].Settings;
                var prefix = $"{HomeAssistantServersOptions.SectionName}:{nameof(HomeAssistantServersOptions.Items)}:{i}:";
                data[prefix + nameof(homeAssistant.Id)] = homeAssistant.Id;
                data[prefix + nameof(homeAssistant.Name)] = homeAssistant.Name;
                data[prefix + nameof(homeAssistant.BaseUrl)] = homeAssistant.BaseUrl;
                data[prefix + nameof(homeAssistant.DeviceId)] = homeAssistant.DeviceId;
                data[prefix + nameof(homeAssistant.DeviceName)] = homeAssistant.DeviceName;
                data[prefix + nameof(homeAssistant.CommandEventType)] = homeAssistant.CommandEventType;
                data[prefix + nameof(homeAssistant.Notifications)] = homeAssistant.Notifications ? "true" : "false";
                data[prefix + nameof(homeAssistant.SensorMode)] = homeAssistant.SensorMode.ToString();
                data[prefix + "AccessToken"] = SettingsStore.TryUnprotect(homeAssistants[i].AccessToken) ?? string.Empty;
            }
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

        if (settings.Updates is { } updates)
        {
            data[$"{UpdateOptions.SectionName}:{nameof(UpdateOptions.CheckAutomatically)}"] = updates.CheckAutomatically ? "true" : "false";
            data[$"{UpdateOptions.SectionName}:{nameof(UpdateOptions.IncludePrereleases)}"] = updates.IncludePrereleases ? "true" : "false";
        }

        return data;
    }
}

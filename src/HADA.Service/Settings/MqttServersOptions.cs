using HADA.Engine.Mqtt;

namespace HADA.Service.Settings;

/// <summary>
/// The MQTT servers the computer is connected to at the same time, each of them one Home Assistant. The window
/// saves them here; a single server can also be given the old way, in the <c>Mqtt</c> section.
/// </summary>
public sealed class MqttServersOptions
{
    public const string SectionName = "MqttServers";

    /// <summary>
    /// How many of <see cref="Items"/> the settings saved from the window consist of; unset while none were saved.
    /// Configuration merges lists item by item, so without this a server listed in appsettings.json behind the
    /// saved ones would come back after it was removed in the window.
    /// </summary>
    public int? Count { get; set; }

    public List<MqttOptions> Items { get; set; } = [];

    /// <summary>
    /// The servers in effect: the saved ones, else the ones listed in this section, else the one of the
    /// <c>Mqtt</c> section. Every one has an id, and no two the same.
    /// </summary>
    public static IReadOnlyList<MqttOptions> Resolve(MqttServersOptions servers, MqttOptions mqttSection)
    {
        IEnumerable<MqttOptions> listed = servers.Count is { } saved ? servers.Items.Take(saved) : servers.Items;
        var resolved = new List<MqttOptions>();
        foreach (var server in listed)
        {
            // Servers written into appsettings.json by hand need not be given ids; their place in the list will do.
            if (string.IsNullOrWhiteSpace(server.Id))
            {
                server.Id = $"server{resolved.Count + 1}";
            }

            if (resolved.TrueForAll(other => other.Id != server.Id))
            {
                resolved.Add(server);
            }
        }

        if (resolved.Count > 0 || servers.Count is not null)
        {
            return resolved;
        }

        mqttSection.Id ??= MqttOptions.DefaultId;
        return [mqttSection];
    }
}

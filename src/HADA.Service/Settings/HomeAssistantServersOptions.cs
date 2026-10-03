using HADA.Engine.WebSocket;

namespace HADA.Service.Settings;

/// <summary>
/// The Home Assistants the computer is connected to directly at the same time. The window saves them here; a
/// single one can also be given the old way, in the <c>HomeAssistant</c> section.
/// </summary>
public sealed class HomeAssistantServersOptions
{
    public const string SectionName = "HomeAssistantServers";

    /// <summary>
    /// How many of <see cref="Items"/> the settings saved from the window consist of; unset while none were saved.
    /// See <see cref="MqttServersOptions.Count"/>.
    /// </summary>
    public int? Count { get; set; }

    public List<HaWebSocketOptions> Items { get; set; } = [];

    /// <summary>
    /// The servers in effect: the saved ones, else the ones listed in this section, else the one of the
    /// <c>HomeAssistant</c> section. Every one has an id, and no two the same.
    /// </summary>
    public static IReadOnlyList<HaWebSocketOptions> Resolve(HomeAssistantServersOptions servers, HaWebSocketOptions homeAssistantSection)
    {
        IEnumerable<HaWebSocketOptions> listed = servers.Count is { } saved ? servers.Items.Take(saved) : servers.Items;
        var resolved = new List<HaWebSocketOptions>();
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

        homeAssistantSection.Id ??= HaWebSocketOptions.DefaultId;
        return [homeAssistantSection];
    }
}

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace HADA.Core;

/// <summary>
/// A Home Assistant the computer is connected to directly, as the settings window last saw it, with what the
/// user chose for its dashboard. Kept among the user's preferences, so that the tray icon's menu can offer the
/// dashboards without asking the service which servers there are.
/// </summary>
/// <param name="Id">The id of the server's settings.</param>
/// <param name="Name">What the user calls the server; empty when it has no name.</param>
/// <param name="Url">The address HADA connects to the server at.</param>
public sealed record ServerDashboard(string Id, string Name, string Url)
{
    /// <summary>Whether the dashboard is offered in the tray icon's menu.</summary>
    public bool Show { get; init; } = true;

    /// <summary>
    /// An address shown instead of <see cref="ServerDashboard.Url"/>, a particular dashboard for instance; empty
    /// for the server's own address.
    /// </summary>
    public string OwnUrl { get; init; } = string.Empty;
}

/// <summary>Something the dashboard window can show, and the tray icon's menu offers.</summary>
/// <param name="Id">Tells the dashboards apart: <see cref="Dashboards.OwnId"/>, or a server's id after <c>ha:</c>.</param>
/// <param name="Name">The server's name, or the host of the address when there is none.</param>
public sealed record Dashboard(string Id, string Name, Uri Address);

/// <summary>
/// Which dashboards there are: one for each Home Assistant connected to directly, unless the user switched it
/// off, and one for an address of the user's own, as for a Home Assistant reached through MQTT.
/// </summary>
public static class Dashboards
{
    /// <summary>The id of the dashboard that shows the address the user typed.</summary>
    public const string OwnId = "own";

    private const string ServerPrefix = "ha:";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Only <c>http</c> and <c>https</c> addresses are shown.</summary>
    public static bool TryGetAddress(string? text, [NotNullWhen(true)] out Uri? address) =>
        Uri.TryCreate(text?.Trim(), UriKind.Absolute, out address)
        && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps);

    /// <summary>Reads the list back from the preference it is kept in; empty for one that is missing or damaged.</summary>
    public static IReadOnlyList<ServerDashboard> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return [.. (JsonSerializer.Deserialize<List<ServerDashboard>>(json, JsonOptions) ?? [])
                .Where(server => server is { Id.Length: > 0, Name: not null, Url: not null, OwnUrl: not null })];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string Serialize(IReadOnlyList<ServerDashboard> servers) => JsonSerializer.Serialize(servers, JsonOptions);

    /// <summary>
    /// Brings the list up to date with the servers there are now: a new one is added, and shown; names and
    /// addresses follow the servers'; one that is gone is dropped. What the user chose for a server stays.
    /// </summary>
    /// <param name="ownUrl">
    /// The address the user typed before there was a dashboard per server. When it is one at a server that is
    /// new to the list, it becomes that server's own address, rather than a second entry for the same Home Assistant.
    /// </param>
    public static IReadOnlyList<ServerDashboard> Sync(
        IReadOnlyList<ServerDashboard> saved, IEnumerable<(string Id, string Name, string Url)> servers, ref string ownUrl)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(servers);

        var synced = new List<ServerDashboard>();
        foreach (var (id, name, url) in servers)
        {
            if (id.Length == 0 || !TryGetAddress(url, out var address) || synced.Exists(server => server.Id == id))
            {
                continue;
            }

            if (saved.FirstOrDefault(server => server.Id == id) is { } known)
            {
                synced.Add(known with { Name = name.Trim(), Url = address.AbsoluteUri });
                continue;
            }

            var added = new ServerDashboard(id, name.Trim(), address.AbsoluteUri);
            if (TryGetAddress(ownUrl, out var own)
                && Uri.Compare(own, address, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0)
            {
                added = added with { OwnUrl = own.AbsoluteUri == address.AbsoluteUri ? string.Empty : own.AbsoluteUri };
                ownUrl = string.Empty;
            }

            synced.Add(added);
        }

        return synced;
    }

    /// <summary>
    /// The dashboards to offer, in the order of the servers, the user's own address last. An own address that
    /// one of the servers already shows is not offered twice.
    /// </summary>
    public static IReadOnlyList<Dashboard> List(IReadOnlyList<ServerDashboard> servers, string? ownUrl)
    {
        ArgumentNullException.ThrowIfNull(servers);

        var dashboards = new List<Dashboard>();
        foreach (var server in servers)
        {
            if (server.Show && TryGetAddress(server.OwnUrl.Length > 0 ? server.OwnUrl : server.Url, out var address))
            {
                dashboards.Add(new Dashboard(ServerPrefix + server.Id, server.Name.Length > 0 ? server.Name : address.Host, address));
            }
        }

        if (TryGetAddress(ownUrl, out var own) && !dashboards.Exists(dashboard => dashboard.Address == own))
        {
            dashboards.Add(new Dashboard(OwnId, own.Host, own));
        }

        return dashboards;
    }
}

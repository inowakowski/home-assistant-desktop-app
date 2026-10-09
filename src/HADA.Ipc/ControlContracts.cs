using HADA.Core.Abstractions;
using HADA.Core.Entities;
using Microsoft.Extensions.Logging;

namespace HADA.Ipc;

/// <summary>What a client connection is used for.</summary>
public enum IpcClientRole
{
    /// <summary>Streams entities and readings from the user session (the tray's sensors).</summary>
    Sensors,

    /// <summary>Reads status, settings and logs. Saving settings and testing connections also require an elevated administrator.</summary>
    Control,
}

/// <param name="Update">A newer version of HADA, when the service found one.</param>
/// <param name="LastUpdateCheck">How the last look for a newer version went; <see langword="null"/> before the first one.</param>
public sealed record ServiceStatus(
    string Version,
    DateTimeOffset StartedAt,
    IReadOnlyList<EngineStatus> Engines,
    IReadOnlyList<string> SensorClients,
    IReadOnlyList<EntityStatus> Entities,
    UpdateInfo? Update = null,
    UpdateCheckResult? LastUpdateCheck = null);

/// <param name="Version">E.g. <c>0.4.1</c>.</param>
/// <param name="Url">The page to download it from.</param>
public sealed record UpdateInfo(string Version, string Url);

public enum UpdateCheckOutcome
{
    /// <summary>This is the newest version published.</summary>
    UpToDate,

    UpdateAvailable,

    /// <summary>GitHub shows no releases without signing in, as for a private repository; there is nothing to compare with.</summary>
    ReleasesHidden,

    /// <summary>GitHub could not be reached, or did not answer properly.</summary>
    Failed,
}

/// <param name="LatestVersion">The newest version published, when one was found.</param>
/// <param name="Url">Its release page.</param>
/// <param name="Message">Why the check failed.</param>
public sealed record UpdateCheckResult(
    UpdateCheckOutcome Outcome,
    DateTimeOffset CheckedAt,
    string? LatestVersion = null,
    string? Url = null,
    string? Message = null);

/// <param name="CheckAutomatically">Whether the service asks GitHub for a newer version once a day.</param>
/// <param name="IncludePrereleases">Whether versions marked as pre-releases count as newer versions.</param>
public sealed record UpdateSettings(bool CheckAutomatically = true, bool IncludePrereleases = false);

/// <param name="Name"><c>mqtt</c> or <c>websocket</c>.</param>
/// <param name="ServerId">Which of the servers of that kind this is: the id of its settings.</param>
/// <param name="ServerName">What the user calls that server; empty when it has no name.</param>
/// <param name="Issue">
/// What keeps a connected engine from doing all it is set up to do, as a short code such as
/// <c>integration_missing</c>; <see langword="null"/> when nothing does.
/// </param>
public sealed record EngineStatus(
    string Name,
    bool IsConfigured,
    EngineConnectionState State,
    string? ServerId = null,
    string? ServerName = null,
    string? Issue = null);

/// <param name="Source"><c>service</c>, <c>custom</c> for a custom sensor, or the name of the tray client that registered the entity.</param>
/// <param name="IsAvailable">False while the entity's source is away, e.g. a tray sensor after the tray app exited.</param>
public sealed record EntityStatus(
    EntityDescriptor Entity,
    bool IsEnabled,
    string Source,
    string? State,
    DateTimeOffset? UpdatedAt,
    bool IsAvailable = true);

/// <summary>
/// One MQTT server, which is one Home Assistant. A computer can be connected to several at once, e.g. one per
/// place it is used in; each then shows it as a device of its own and can send it commands.
/// </summary>
public sealed record MqttSettings(
    string Host,
    int Port,
    bool UseTls,
    string Username,
    string DeviceId,
    string DeviceName,
    string DiscoveryPrefix,
    string BaseTopic)
{
    /// <summary>
    /// Tells the servers apart, whatever else about them is changed: a saved password is kept under it. Lowercase
    /// letters and digits, made up by the window when a server is added.
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>What the user calls the server, e.g. "Flat". Needed once there is more than one.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>A new id for a server added in the window.</summary>
    public static string NewId() => Guid.NewGuid().ToString("N")[..8];
}

/// <summary>A server as shown to clients: its password is never sent back, only whether one is saved.</summary>
public sealed record MqttServerSnapshot(MqttSettings Settings, bool HasPassword);

public sealed record MqttServerUpdate(MqttSettings Settings, SecretUpdate Password);

/// <summary>
/// One Home Assistant the computer is connected to directly, through its WebSocket API. As with MQTT servers,
/// there can be several at once.
/// </summary>
public sealed record HomeAssistantSettings(
    string BaseUrl,
    string DeviceId,
    string DeviceName,
    string CommandEventType)
{
    /// <summary>
    /// Tells the servers apart, whatever else about them is changed: a saved access token is kept under it.
    /// Lowercase letters and digits, made up by the window when a server is added.
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>What the user calls the server, e.g. "Flat". Needed once there is more than one.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Whether the computer registers with this Home Assistant's <c>mobile_app</c> integration, which gives it the
    /// action <c>notify.mobile_app_{device name}</c>. Off for a server saved before there was such a thing.
    /// </summary>
    public bool Notifications { get; init; }

    /// <summary>How sensors reach this Home Assistant. As states, for a server saved before there was a choice.</summary>
    public HomeAssistantSensorMode SensorMode { get; init; } = HomeAssistantSensorMode.States;
}

/// <summary>A server as shown to clients: its access token is never sent back, only whether one is saved.</summary>
public sealed record HomeAssistantServerSnapshot(HomeAssistantSettings Settings, bool HasAccessToken);

public sealed record HomeAssistantServerUpdate(HomeAssistantSettings Settings, SecretUpdate AccessToken);

/// <summary>Effective settings as shown to clients. Secrets are never sent back, only whether one is set.</summary>
/// <param name="EnabledEntities">The off-by-default entities that were switched on.</param>
public sealed record SettingsSnapshot(
    IReadOnlyList<MqttServerSnapshot> MqttServers,
    IReadOnlyList<HomeAssistantServerSnapshot> HomeAssistantServers,
    IReadOnlyList<string> DisabledEntities,
    IReadOnlyList<CustomSensorDefinition> CustomSensors,
    IReadOnlyList<string> EnabledEntities,
    UpdateSettings Updates);

public enum SecretChange
{
    Keep,
    Replace,
    Clear,
}

public sealed record SecretUpdate(SecretChange Change, string? Value = null)
{
    public static SecretUpdate Unchanged { get; } = new(SecretChange.Keep);

    // Never let a secret end up in logs or exception messages through the record's generated ToString.
    public override string ToString() => $"SecretUpdate {{ Change = {Change} }}";
}

/// <param name="MqttServers">Every server there should be; one that is left out is forgotten, with its password.</param>
/// <param name="HomeAssistantServers">Likewise: one that is left out is forgotten, with its access token.</param>
public sealed record SettingsUpdate(
    IReadOnlyList<MqttServerUpdate> MqttServers,
    IReadOnlyList<HomeAssistantServerUpdate> HomeAssistantServers,
    IReadOnlyList<string> DisabledEntities,
    IReadOnlyList<CustomSensorDefinition> CustomSensors,
    IReadOnlyList<string> EnabledEntities,
    UpdateSettings Updates);

public enum ConnectionTarget
{
    Mqtt,
    HomeAssistant,
}

public sealed record OperationResult(bool Success, string? Message = null);

public sealed record LogEntry(
    long Sequence,
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Category,
    string Message,
    string? Exception);

/// <summary>Implemented by the service to answer control requests. The IPC server checks authorization first.</summary>
public interface IServiceControl
{
    Task<ServiceStatus> GetStatusAsync(CancellationToken cancellationToken);

    Task<SettingsSnapshot> GetSettingsAsync(CancellationToken cancellationToken);

    Task<OperationResult> SaveSettingsAsync(SettingsUpdate settings, CancellationToken cancellationToken);

    /// <summary>Tries <paramref name="settings"/> without saving; secrets marked Keep use the stored values.</summary>
    /// <param name="serverId">Which server of <paramref name="settings"/> to try; the first one of that kind when <see langword="null"/>.</param>
    Task<OperationResult> TestConnectionAsync(
        ConnectionTarget target, SettingsUpdate settings, string? serverId, CancellationToken cancellationToken);

    IReadOnlyList<LogEntry> GetLogs(long afterSequence, int maxCount);

    /// <summary>Looks for a newer version now. Open to every signed-in user, so implementations must not let it be used to flood anyone.</summary>
    Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken);
}

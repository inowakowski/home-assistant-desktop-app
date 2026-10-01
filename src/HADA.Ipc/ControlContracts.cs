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

public sealed record ServiceStatus(
    string Version,
    DateTimeOffset StartedAt,
    IReadOnlyList<EngineStatus> Engines,
    IReadOnlyList<string> SensorClients,
    IReadOnlyList<EntityStatus> Entities);

public sealed record EngineStatus(string Name, bool IsConfigured, EngineConnectionState State);

/// <param name="Source"><c>service</c>, or the name of the tray client that registered the entity.</param>
public sealed record EntityStatus(
    EntityDescriptor Entity,
    bool IsEnabled,
    string Source,
    string? State,
    DateTimeOffset? UpdatedAt);

public sealed record MqttSettings(
    string Host,
    int Port,
    bool UseTls,
    string Username,
    string DeviceId,
    string DeviceName,
    string DiscoveryPrefix,
    string BaseTopic);

public sealed record HomeAssistantSettings(
    string BaseUrl,
    string DeviceId,
    string DeviceName,
    string CommandEventType);

/// <summary>Effective settings as shown to clients. Secrets are never sent back, only whether one is set.</summary>
public sealed record SettingsSnapshot(
    MqttSettings Mqtt,
    bool HasMqttPassword,
    HomeAssistantSettings HomeAssistant,
    bool HasAccessToken,
    IReadOnlyList<string> DisabledEntities);

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

public sealed record SettingsUpdate(
    MqttSettings Mqtt,
    SecretUpdate MqttPassword,
    HomeAssistantSettings HomeAssistant,
    SecretUpdate AccessToken,
    IReadOnlyList<string> DisabledEntities);

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
    Task<OperationResult> TestConnectionAsync(ConnectionTarget target, SettingsUpdate settings, CancellationToken cancellationToken);

    IReadOnlyList<LogEntry> GetLogs(long afterSequence, int maxCount);
}

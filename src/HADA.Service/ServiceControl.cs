using System.Diagnostics;
using System.Reflection;
using HADA.Core.Abstractions;
using HADA.Core.Models;
using HADA.Engine.Mqtt;
using HADA.Engine.WebSocket;
using HADA.Ipc;
using HADA.Service.CustomSensors;
using HADA.Service.Logging;
using HADA.Service.Settings;
using HADA.Service.Updates;
using Microsoft.Extensions.Options;

namespace HADA.Service;

/// <summary>Answers the settings window's requests. The IPC server checks authorization before calls reach this class.</summary>
public sealed partial class ServiceControl(
    IEntityRegistry registry,
    TelemetryCache telemetry,
    EngineSupervisor engines,
    SettingsStore store,
    StoredSettingsConfigurationProvider storedSettings,
    IOptionsMonitor<MqttOptions> mqttOptions,
    IOptionsMonitor<HaWebSocketOptions> homeAssistantOptions,
    IOptionsMonitor<EntityOptions> entityOptions,
    IOptionsMonitor<CustomSensorOptions> customSensorOptions,
    LogBuffer logs,
    ILogger<ServiceControl> logger,
    UpdateChecker? updates = null) : IServiceControl
{
    private const int MaxLogEntriesPerRequest = 50;
    private const int MaxTextLength = 256;
    private const int MaxSecretLength = 4096;
    private const int MaxDisabledEntities = 256;

    private static readonly string Version =
        typeof(ServiceControl).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    private static readonly DateTimeOffset StartedAt = GetProcessStartTime();

    public Task<ServiceStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var filter = entityOptions.CurrentValue.ToFilter();
        var custom = CurrentCustomSensors().Select(sensor => sensor.Id).ToHashSet(StringComparer.Ordinal);
        var entities = registry.Entities
            .OrderBy(entity => entity.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(entity =>
            {
                telemetry.TryGet(entity.Id, out var reading);
                var source = custom.Contains(entity.Id) ? CustomSensorHost.Source : reading?.Source ?? "service";
                return new EntityStatus(
                    entity, filter.IsEnabled(entity), source, reading?.State, reading?.Timestamp, registry.IsAvailable(entity.Id));
            })
            .ToArray();

        // The IPC server fills in the connected tray clients and which entities they own.
        return Task.FromResult(new ServiceStatus(Version, StartedAt, engines.GetStatus(), [], entities, updates?.Available));
    }

    public Task<SettingsSnapshot> GetSettingsAsync(CancellationToken cancellationToken)
    {
        var mqtt = mqttOptions.CurrentValue;
        var homeAssistant = homeAssistantOptions.CurrentValue;
        return Task.FromResult(new SettingsSnapshot(
            new MqttSettings(
                mqtt.Host ?? string.Empty,
                mqtt.Port,
                mqtt.UseTls,
                mqtt.Username ?? string.Empty,
                mqtt.DeviceId ?? string.Empty,
                mqtt.DeviceName ?? string.Empty,
                mqtt.DiscoveryPrefix,
                mqtt.BaseTopic),
            !string.IsNullOrEmpty(mqtt.Password),
            new HomeAssistantSettings(
                homeAssistant.BaseUrl ?? string.Empty,
                homeAssistant.DeviceId ?? string.Empty,
                homeAssistant.DeviceName ?? string.Empty,
                homeAssistant.CommandEventType),
            !string.IsNullOrEmpty(homeAssistant.AccessToken),
            [.. entityOptions.CurrentValue.Disabled],
            CurrentCustomSensors(),
            [.. entityOptions.CurrentValue.Enabled]));
    }

    public Task<OperationResult> SaveSettingsAsync(SettingsUpdate settings, CancellationToken cancellationToken)
    {
        if (Validate(settings) is { } error)
        {
            return Task.FromResult(new OperationResult(false, error));
        }

        var stored = new StoredSettings
        {
            Mqtt = Normalize(settings.Mqtt),
            MqttPassword = ProtectOrNull(ResolveSecret(settings.MqttPassword, mqttOptions.CurrentValue.Password)),
            HomeAssistant = Normalize(settings.HomeAssistant),
            AccessToken = ProtectOrNull(ResolveSecret(settings.AccessToken, homeAssistantOptions.CurrentValue.AccessToken)),
            DisabledEntities = [.. settings.DisabledEntities.Distinct(StringComparer.Ordinal)],
            EnabledEntities = [.. settings.EnabledEntities.Distinct(StringComparer.Ordinal)],
            CustomSensors = [.. settings.CustomSensors.Select(sensor => sensor.Normalize())],
        };

        try
        {
            store.Save(stored);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogSaveFailed(logger, ex, store.FilePath);
            return Task.FromResult(new OperationResult(false, $"Could not write {store.FilePath}: {ex.Message}"));
        }

        // Engines pick the new values up through their options monitors and restart if needed.
        storedSettings.Reload();
        LogSettingsSaved(logger, store.FilePath);
        return Task.FromResult(new OperationResult(true));
    }

    public async Task<OperationResult> TestConnectionAsync(
        ConnectionTarget target, SettingsUpdate settings, CancellationToken cancellationToken)
    {
        if (Validate(settings, target) is { } error)
        {
            return new OperationResult(false, error);
        }

        var result = target switch
        {
            ConnectionTarget.Mqtt => await MqttEngine.TestConnectionAsync(
                ToOptions(Normalize(settings.Mqtt), ResolveSecret(settings.MqttPassword, mqttOptions.CurrentValue.Password)),
                cancellationToken).ConfigureAwait(false),
            ConnectionTarget.HomeAssistant => await HaWebSocketEngine.TestConnectionAsync(
                ToOptions(Normalize(settings.HomeAssistant), ResolveSecret(settings.AccessToken, homeAssistantOptions.CurrentValue.AccessToken)),
                cancellationToken).ConfigureAwait(false),
            _ => new ConnectionTestResult(false, $"Unknown connection target '{target}'."),
        };

        LogConnectionTested(logger, target, result.Success);
        return new OperationResult(result.Success, result.Message);
    }

    public IReadOnlyList<LogEntry> GetLogs(long afterSequence, int maxCount) =>
        logs.GetAfter(afterSequence, Math.Clamp(maxCount, 1, MaxLogEntriesPerRequest));

    /// <summary>Server-side checks; the window validates too, but must not be trusted to.</summary>
    /// <param name="target">
    /// The connection being tested, so a test is not refused over settings it does not use;
    /// <see langword="null"/> to check everything before saving.
    /// </param>
    public static string? Validate(SettingsUpdate settings, ConnectionTarget? target = null)
    {
        if (target is null or ConnectionTarget.Mqtt && ValidateMqtt(settings) is { } mqttError)
        {
            return mqttError;
        }

        if (target is null or ConnectionTarget.HomeAssistant && ValidateHomeAssistant(settings) is { } homeAssistantError)
        {
            return homeAssistantError;
        }

        if (target is not null)
        {
            return null;
        }

        if (settings.DisabledEntities.Count > MaxDisabledEntities || settings.EnabledEntities.Count > MaxDisabledEntities)
        {
            return "Too many entities are switched on or off.";
        }

        return CustomSensorRules.Validate([.. settings.CustomSensors.Select(sensor => sensor.Normalize())]);
    }

    private static string? ValidateMqtt(SettingsUpdate settings)
    {
        // Checked as it will be used: a pasted "mqtt://broker:1883" is split into host and port first.
        var mqtt = MqttAddress.Apply(settings.Mqtt);

        string?[] texts = [settings.Mqtt.Host, mqtt.Username, mqtt.DeviceId, mqtt.DeviceName, mqtt.DiscoveryPrefix, mqtt.BaseTopic];
        if (texts.Any(text => text is null || text.Length > MaxTextLength))
        {
            return $"Text settings must be at most {MaxTextLength} characters long.";
        }

        if ((settings.MqttPassword.Value?.Length ?? 0) > MaxSecretLength)
        {
            return $"Secrets must be at most {MaxSecretLength} characters long.";
        }

        if (mqtt.Port is < 1 or > 65535)
        {
            return "The MQTT port must be between 1 and 65535.";
        }

        if (mqtt.Host.Length > 0 && Uri.CheckHostName(mqtt.Host) == UriHostNameType.Unknown)
        {
            return $"'{mqtt.Host}' is not a valid host name or IP address.";
        }

        return IsTopicPrefix(mqtt.DiscoveryPrefix) && IsTopicPrefix(mqtt.BaseTopic)
            ? null
            : "MQTT topic prefixes must not be empty or contain spaces, '+' or '#'.";
    }

    private static string? ValidateHomeAssistant(SettingsUpdate settings)
    {
        var homeAssistant = settings.HomeAssistant;

        string?[] texts = [homeAssistant.BaseUrl, homeAssistant.DeviceId, homeAssistant.DeviceName, homeAssistant.CommandEventType];
        if (texts.Any(text => text is null || text.Length > MaxTextLength))
        {
            return $"Text settings must be at most {MaxTextLength} characters long.";
        }

        if ((settings.AccessToken.Value?.Length ?? 0) > MaxSecretLength)
        {
            return $"Secrets must be at most {MaxSecretLength} characters long.";
        }

        if (homeAssistant.BaseUrl.Trim() is { Length: > 0 } baseUrl
            && !(Uri.TryCreate(baseUrl, UriKind.Absolute, out var url) && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps)))
        {
            return "The Home Assistant URL must be an absolute http:// or https:// address.";
        }

        return string.IsNullOrWhiteSpace(homeAssistant.CommandEventType) || homeAssistant.CommandEventType.Trim().Any(char.IsWhiteSpace)
            ? "The command event type must not be empty or contain spaces."
            : null;
    }

    private CustomSensorDefinition[] CurrentCustomSensors() =>
        [.. customSensorOptions.CurrentValue.Items.Select(sensor => sensor.Normalize())];

    private static bool IsTopicPrefix(string value) =>
        value.Trim().Trim('/').Length > 0 && !value.Trim().Any(c => char.IsWhiteSpace(c) || c is '+' or '#');

    private static MqttSettings Normalize(MqttSettings settings) => MqttAddress.Apply(settings) with
    {
        Username = settings.Username.Trim(),
        DeviceId = settings.DeviceId.Trim(),
        DeviceName = settings.DeviceName.Trim(),
        DiscoveryPrefix = settings.DiscoveryPrefix.Trim().Trim('/'),
        BaseTopic = settings.BaseTopic.Trim().Trim('/'),
    };

    private static HomeAssistantSettings Normalize(HomeAssistantSettings settings) => settings with
    {
        BaseUrl = settings.BaseUrl.Trim(),
        DeviceId = settings.DeviceId.Trim(),
        DeviceName = settings.DeviceName.Trim(),
        CommandEventType = settings.CommandEventType.Trim(),
    };

    private static MqttOptions ToOptions(MqttSettings settings, string? password) => new()
    {
        Host = settings.Host,
        Port = settings.Port,
        UseTls = settings.UseTls,
        Username = settings.Username,
        Password = password,
        DeviceId = settings.DeviceId,
        DeviceName = settings.DeviceName,
        DiscoveryPrefix = settings.DiscoveryPrefix,
        BaseTopic = settings.BaseTopic,
    };

    private static HaWebSocketOptions ToOptions(HomeAssistantSettings settings, string? accessToken) => new()
    {
        BaseUrl = settings.BaseUrl,
        AccessToken = accessToken,
        DeviceId = settings.DeviceId,
        DeviceName = settings.DeviceName,
        CommandEventType = settings.CommandEventType,
    };

    private static string? ResolveSecret(SecretUpdate update, string? current) => update.Change switch
    {
        SecretChange.Replace => update.Value ?? string.Empty,
        SecretChange.Clear => null,
        _ => current,
    };

    private static string? ProtectOrNull(string? secret) =>
        string.IsNullOrEmpty(secret) ? null : SettingsStore.Protect(secret);

    private static DateTimeOffset GetProcessStartTime()
    {
        using var process = Process.GetCurrentProcess();
        return new DateTimeOffset(process.StartTime);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Settings saved to {Path}.")]
    private static partial void LogSettingsSaved(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Saving settings to {Path} failed.")]
    private static partial void LogSaveFailed(ILogger logger, Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connection test for {Target} finished; success: {Success}.")]
    private static partial void LogConnectionTested(ILogger logger, ConnectionTarget target, bool success);
}

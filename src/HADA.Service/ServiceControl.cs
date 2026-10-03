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
    IOptionsMonitor<MqttServersOptions> mqttServers,
    IOptionsMonitor<HaWebSocketOptions> homeAssistantOptions,
    IOptionsMonitor<HomeAssistantServersOptions> homeAssistantServers,
    IOptionsMonitor<EntityOptions> entityOptions,
    IOptionsMonitor<CustomSensorOptions> customSensorOptions,
    IOptionsMonitor<UpdateOptions> updateOptions,
    LogBuffer logs,
    ILogger<ServiceControl> logger,
    UpdateChecker? updates = null,
    MobileAppRegistrationStore? registrations = null) : IServiceControl
{
    private const int MaxLogEntriesPerRequest = 50;
    private const int MaxTextLength = 256;
    private const int MaxSecretLength = 4096;
    private const int MaxDisabledEntities = 256;
    private const int MaxServerNameLength = 64;

    /// <summary>More Home Assistants than anyone has; a limit so that a request cannot make the service open hundreds of connections.</summary>
    public const int MaxMqttServers = 8;

    /// <summary>As <see cref="MaxMqttServers"/>, for the Home Assistants connected to directly.</summary>
    public const int MaxHomeAssistantServers = 8;

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
        return Task.FromResult(
            new ServiceStatus(Version, StartedAt, engines.GetStatus(), [], entities, updates?.Available, updates?.LastCheck));
    }

    public Task<SettingsSnapshot> GetSettingsAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(new SettingsSnapshot(
            [.. CurrentMqttServers().Select(mqtt => new MqttServerSnapshot(ToSettings(mqtt), !string.IsNullOrEmpty(mqtt.Password)))],
            [
                .. CurrentHomeAssistantServers().Select(homeAssistant =>
                    new HomeAssistantServerSnapshot(ToSettings(homeAssistant), !string.IsNullOrEmpty(homeAssistant.AccessToken))),
            ],
            [.. entityOptions.CurrentValue.Disabled],
            CurrentCustomSensors(),
            [.. entityOptions.CurrentValue.Enabled],
            new UpdateSettings(updateOptions.CurrentValue.CheckAutomatically, updateOptions.CurrentValue.IncludePrereleases)));
    }

    public Task<OperationResult> SaveSettingsAsync(SettingsUpdate settings, CancellationToken cancellationToken)
    {
        if (Validate(settings) is { } error)
        {
            return Task.FromResult(new OperationResult(false, error));
        }

        // A server keeps its password for as long as it keeps its id, whatever else about it changes.
        var passwords = CurrentMqttServers().ToDictionary(server => server.Id!, server => server.Password, StringComparer.Ordinal);
        var tokens = CurrentHomeAssistantServers().ToDictionary(server => server.Id!, server => server.AccessToken, StringComparer.Ordinal);
        var stored = new StoredSettings
        {
            MqttServers =
            [
                .. settings.MqttServers.Select(server => new StoredMqttServer(
                    Normalize(server.Settings),
                    ProtectOrNull(ResolveSecret(server.Password, passwords.GetValueOrDefault(server.Settings.Id))))),
            ],
            HomeAssistantServers =
            [
                .. settings.HomeAssistantServers.Select(server => new StoredHomeAssistantServer(
                    Normalize(server.Settings),
                    ProtectOrNull(ResolveSecret(server.AccessToken, tokens.GetValueOrDefault(server.Settings.Id))))),
            ],
            DisabledEntities = [.. settings.DisabledEntities.Distinct(StringComparer.Ordinal)],
            EnabledEntities = [.. settings.EnabledEntities.Distinct(StringComparer.Ordinal)],
            CustomSensors = [.. settings.CustomSensors.Select(sensor => sensor.Normalize())],
            Updates = settings.Updates,
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

        // A Home Assistant that was removed is forgotten with what it gave this computer, as with its access token.
        registrations?.KeepOnly([.. stored.HomeAssistantServers.Select(server => server.Settings.Id)]);

        // Engines pick the new values up through their options monitors and restart if needed.
        storedSettings.Reload();
        LogSettingsSaved(logger, store.FilePath);
        return Task.FromResult(new OperationResult(true));
    }

    public async Task<OperationResult> TestConnectionAsync(
        ConnectionTarget target, SettingsUpdate settings, string? serverId, CancellationToken cancellationToken)
    {
        if (Validate(settings, target, serverId) is { } error)
        {
            return new OperationResult(false, error);
        }

        var result = target switch
        {
            ConnectionTarget.Mqtt => await TestMqttAsync(FindServer(settings, serverId), cancellationToken).ConfigureAwait(false),
            ConnectionTarget.HomeAssistant =>
                await TestHomeAssistantAsync(FindHomeAssistant(settings, serverId), cancellationToken).ConfigureAwait(false),
            _ => new ConnectionTestResult(false, $"Unknown connection target '{target}'."),
        };

        LogConnectionTested(logger, target, result.Success);
        return new OperationResult(result.Success, result.Message);
    }

    public IReadOnlyList<LogEntry> GetLogs(long afterSequence, int maxCount) =>
        logs.GetAfter(afterSequence, Math.Clamp(maxCount, 1, MaxLogEntriesPerRequest));

    public Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken) =>
        updates?.CheckAsync(cancellationToken)
        ?? Task.FromResult(new UpdateCheckResult(UpdateCheckOutcome.Failed, DateTimeOffset.UtcNow, Message: "This service cannot check for updates."));

    /// <summary>Server-side checks; the window validates too, but must not be trusted to.</summary>
    /// <param name="target">
    /// The connection being tested, so a test is not refused over settings it does not use;
    /// <see langword="null"/> to check everything before saving.
    /// </param>
    /// <param name="serverId">The server being tested; the first one of that kind when <see langword="null"/>.</param>
    public static string? Validate(SettingsUpdate settings, ConnectionTarget? target = null, string? serverId = null)
    {
        if (settings.MqttServers is null || settings.MqttServers.Any(server => server?.Settings is null || server.Password is null))
        {
            return "The MQTT servers are missing.";
        }

        if (settings.HomeAssistantServers is null
            || settings.HomeAssistantServers.Any(server => server?.Settings is null || server.AccessToken is null))
        {
            return "The Home Assistant servers are missing.";
        }

        var mqttError = target switch
        {
            null => ValidateMqttServers(settings.MqttServers),
            ConnectionTarget.Mqtt => FindServer(settings, serverId) is { } server ? ValidateMqtt(server) : "There is no such MQTT server.",
            _ => null,
        };
        if (mqttError is not null)
        {
            return mqttError;
        }

        var homeAssistantError = target switch
        {
            null => ValidateHomeAssistantServers(settings.HomeAssistantServers),
            ConnectionTarget.HomeAssistant => FindHomeAssistant(settings, serverId) is { } server
                ? ValidateHomeAssistant(server)
                : "There is no such Home Assistant server.",
            _ => null,
        };
        if (homeAssistantError is not null)
        {
            return homeAssistantError;
        }

        if (target is not null)
        {
            return null;
        }

        if (settings.Updates is null)
        {
            return "The update settings are missing.";
        }

        if (settings.DisabledEntities.Count > MaxDisabledEntities || settings.EnabledEntities.Count > MaxDisabledEntities)
        {
            return "Too many entities are switched on or off.";
        }

        return CustomSensorRules.Validate([.. settings.CustomSensors.Select(sensor => sensor.Normalize())]);
    }

    /// <summary>Each server by itself, and then what they must not have in common.</summary>
    private static string? ValidateMqttServers(IReadOnlyList<MqttServerUpdate> servers)
    {
        if (servers.Count > MaxMqttServers)
        {
            return $"There can be at most {MaxMqttServers} MQTT servers.";
        }

        if (servers.Select(ValidateMqtt).FirstOrDefault(error => error is not null) is { } serverError)
        {
            return serverError;
        }

        var settings = servers.Select(server => Normalize(server.Settings)).ToArray();
        if (settings.Select(server => server.Id).Distinct(StringComparer.Ordinal).Count() != settings.Length)
        {
            return "Two MQTT servers have the same id.";
        }

        if (settings.Length > 1 && settings.Any(server => server.Name.Length == 0))
        {
            return "Give each MQTT server a name, to tell them apart.";
        }

        if (settings.Select(server => server.Name).Distinct(StringComparer.CurrentCultureIgnoreCase).Count() != settings.Length)
        {
            return "Two MQTT servers have the same name.";
        }

        // The same computer twice on one broker: the two connections would throw each other off in turns.
        var twice = settings
            .Where(server => server.Host.Length > 0)
            .GroupBy(
                server => (server.Host, server.Port, DeviceId: server.DeviceId.Length > 0 ? server.DeviceId : Environment.MachineName),
                ServerKeyComparer.Instance)
            .FirstOrDefault(group => group.Count() > 1);
        return twice is null
            ? null
            : $"'{twice.First().Name}' and '{twice.Last().Name}' are the same broker with the same Device ID. Remove one, or give them different Device IDs.";
    }

    private static string? ValidateMqtt(MqttServerUpdate server)
    {
        // Checked as it will be used: a pasted "mqtt://broker:1883" is split into host and port first.
        var mqtt = MqttAddress.Apply(server.Settings);

        string?[] texts = [server.Settings.Host, mqtt.Username, mqtt.DeviceId, mqtt.DeviceName, mqtt.DiscoveryPrefix, mqtt.BaseTopic, mqtt.Name];
        if (texts.Any(text => text is null || text.Length > MaxTextLength))
        {
            return $"Text settings must be at most {MaxTextLength} characters long.";
        }

        if (mqtt.Name.Trim().Length > MaxServerNameLength)
        {
            return $"The name of an MQTT server must be at most {MaxServerNameLength} characters long.";
        }

        if (mqtt.Id is not { Length: > 0 and <= 32 } || mqtt.Id.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c)))
        {
            return "The id of an MQTT server must consist of lowercase letters and digits.";
        }

        if ((server.Password.Value?.Length ?? 0) > MaxSecretLength)
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

    /// <summary>Each Home Assistant by itself, and then what they must not have in common.</summary>
    private static string? ValidateHomeAssistantServers(IReadOnlyList<HomeAssistantServerUpdate> servers)
    {
        if (servers.Count > MaxHomeAssistantServers)
        {
            return $"There can be at most {MaxHomeAssistantServers} Home Assistant servers.";
        }

        if (servers.Select(ValidateHomeAssistant).FirstOrDefault(error => error is not null) is { } serverError)
        {
            return serverError;
        }

        var settings = servers.Select(server => Normalize(server.Settings)).ToArray();
        if (settings.Select(server => server.Id).Distinct(StringComparer.Ordinal).Count() != settings.Length)
        {
            return "Two Home Assistant servers have the same id.";
        }

        if (settings.Length > 1 && settings.Any(server => server.Name.Length == 0))
        {
            return "Give each Home Assistant server a name, to tell them apart.";
        }

        if (settings.Select(server => server.Name).Distinct(StringComparer.CurrentCultureIgnoreCase).Count() != settings.Length)
        {
            return "Two Home Assistant servers have the same name.";
        }

        // The same computer twice in one Home Assistant: the two connections would write the same entities.
        var twice = settings
            .Where(server => server.BaseUrl.Length > 0)
            .GroupBy(
                server => (server.BaseUrl.TrimEnd('/'), DeviceId: server.DeviceId.Length > 0 ? server.DeviceId : Environment.MachineName),
                HomeAssistantKeyComparer.Instance)
            .FirstOrDefault(group => group.Count() > 1);
        return twice is null
            ? null
            : $"'{twice.First().Name}' and '{twice.Last().Name}' are the same Home Assistant with the same Device ID. Remove one, or give them different Device IDs.";
    }

    private static string? ValidateHomeAssistant(HomeAssistantServerUpdate server)
    {
        var homeAssistant = server.Settings;

        string?[] texts =
        [
            homeAssistant.BaseUrl, homeAssistant.DeviceId, homeAssistant.DeviceName, homeAssistant.CommandEventType, homeAssistant.Name,
        ];
        if (texts.Any(text => text is null || text.Length > MaxTextLength))
        {
            return $"Text settings must be at most {MaxTextLength} characters long.";
        }

        if (homeAssistant.Name.Trim().Length > MaxServerNameLength)
        {
            return $"The name of a Home Assistant server must be at most {MaxServerNameLength} characters long.";
        }

        if (homeAssistant.Id is not { Length: > 0 and <= 32 } || homeAssistant.Id.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c)))
        {
            return "The id of a Home Assistant server must consist of lowercase letters and digits.";
        }

        if ((server.AccessToken.Value?.Length ?? 0) > MaxSecretLength)
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

    private IReadOnlyList<MqttOptions> CurrentMqttServers() =>
        MqttServersOptions.Resolve(mqttServers.CurrentValue, mqttOptions.CurrentValue);

    private static MqttServerUpdate? FindServer(SettingsUpdate settings, string? serverId) =>
        serverId is null
            ? (settings.MqttServers.Count > 0 ? settings.MqttServers[0] : null)
            : settings.MqttServers.FirstOrDefault(server => server.Settings.Id == serverId);

    private IReadOnlyList<HaWebSocketOptions> CurrentHomeAssistantServers() =>
        HomeAssistantServersOptions.Resolve(homeAssistantServers.CurrentValue, homeAssistantOptions.CurrentValue);

    private static HomeAssistantServerUpdate? FindHomeAssistant(SettingsUpdate settings, string? serverId) =>
        serverId is null
            ? (settings.HomeAssistantServers.Count > 0 ? settings.HomeAssistantServers[0] : null)
            : settings.HomeAssistantServers.FirstOrDefault(server => server.Settings.Id == serverId);

    private Task<ConnectionTestResult> TestHomeAssistantAsync(HomeAssistantServerUpdate? server, CancellationToken cancellationToken)
    {
        if (server is null)
        {
            return Task.FromResult(new ConnectionTestResult(false, "There is no such Home Assistant server."));
        }

        var saved = CurrentHomeAssistantServers().FirstOrDefault(current => current.Id == server.Settings.Id);
        return HaWebSocketEngine.TestConnectionAsync(
            ToOptions(Normalize(server.Settings), ResolveSecret(server.AccessToken, saved?.AccessToken)), cancellationToken);
    }

    private static HomeAssistantSettings ToSettings(HaWebSocketOptions homeAssistant) => new(
        homeAssistant.BaseUrl ?? string.Empty,
        homeAssistant.DeviceId ?? string.Empty,
        homeAssistant.DeviceName ?? string.Empty,
        homeAssistant.CommandEventType)
    {
        Id = homeAssistant.Id ?? string.Empty,
        Name = homeAssistant.Name ?? string.Empty,
        Notifications = homeAssistant.Notifications,
        SensorMode = homeAssistant.SensorMode,
    };

    private Task<ConnectionTestResult> TestMqttAsync(MqttServerUpdate? server, CancellationToken cancellationToken)
    {
        if (server is null)
        {
            return Task.FromResult(new ConnectionTestResult(false, "There is no such MQTT server."));
        }

        var saved = CurrentMqttServers().FirstOrDefault(current => current.Id == server.Settings.Id);
        return MqttEngine.TestConnectionAsync(
            ToOptions(Normalize(server.Settings), ResolveSecret(server.Password, saved?.Password)), cancellationToken);
    }

    private static MqttSettings ToSettings(MqttOptions mqtt) => new(
        mqtt.Host ?? string.Empty,
        mqtt.Port,
        mqtt.UseTls,
        mqtt.Username ?? string.Empty,
        mqtt.DeviceId ?? string.Empty,
        mqtt.DeviceName ?? string.Empty,
        mqtt.DiscoveryPrefix,
        mqtt.BaseTopic)
    {
        Id = mqtt.Id ?? string.Empty,
        Name = mqtt.Name ?? string.Empty,
    };

    private static MqttSettings Normalize(MqttSettings settings) => MqttAddress.Apply(settings) with
    {
        Name = settings.Name.Trim(),
        Username = settings.Username.Trim(),
        DeviceId = settings.DeviceId.Trim(),
        DeviceName = settings.DeviceName.Trim(),
        DiscoveryPrefix = settings.DiscoveryPrefix.Trim().Trim('/'),
        BaseTopic = settings.BaseTopic.Trim().Trim('/'),
    };

    private static HomeAssistantSettings Normalize(HomeAssistantSettings settings) => settings with
    {
        Name = settings.Name.Trim(),
        BaseUrl = settings.BaseUrl.Trim(),
        DeviceId = settings.DeviceId.Trim(),
        DeviceName = settings.DeviceName.Trim(),
        CommandEventType = settings.CommandEventType.Trim(),
    };

    private static MqttOptions ToOptions(MqttSettings settings, string? password) => new()
    {
        Id = settings.Id,
        Name = settings.Name,
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
        Id = settings.Id,
        Name = settings.Name,
        Notifications = settings.Notifications,
        SensorMode = settings.SensorMode,
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

    /// <summary>Two entries are the same connection when broker, port and device id agree, however they are capitalised.</summary>
    private sealed class ServerKeyComparer : IEqualityComparer<(string Host, int Port, string DeviceId)>
    {
        public static ServerKeyComparer Instance { get; } = new();

        public bool Equals((string Host, int Port, string DeviceId) x, (string Host, int Port, string DeviceId) y) =>
            x.Port == y.Port
            && string.Equals(x.Host, y.Host, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.DeviceId, y.DeviceId, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Host, int Port, string DeviceId) key) =>
            HashCode.Combine(key.Port, key.Host.ToUpperInvariant(), key.DeviceId.ToUpperInvariant());
    }

    /// <summary>Two entries are the same connection when address and device id agree, however they are capitalised.</summary>
    private sealed class HomeAssistantKeyComparer : IEqualityComparer<(string BaseUrl, string DeviceId)>
    {
        public static HomeAssistantKeyComparer Instance { get; } = new();

        public bool Equals((string BaseUrl, string DeviceId) x, (string BaseUrl, string DeviceId) y) =>
            string.Equals(x.BaseUrl, y.BaseUrl, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.DeviceId, y.DeviceId, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string BaseUrl, string DeviceId) key) =>
            HashCode.Combine(key.BaseUrl.ToUpperInvariant(), key.DeviceId.ToUpperInvariant());
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Settings saved to {Path}.")]
    private static partial void LogSettingsSaved(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Saving settings to {Path} failed.")]
    private static partial void LogSaveFailed(ILogger logger, Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connection test for {Target} finished; success: {Success}.")]
    private static partial void LogConnectionTested(ILogger logger, ConnectionTarget target, bool success);
}

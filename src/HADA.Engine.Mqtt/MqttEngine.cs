using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;

namespace HADA.Engine.Mqtt;

/// <summary>
/// Engine A: MQTT with Home Assistant discovery. Availability uses a retained topic backed by a last will,
/// so Home Assistant marks every entity unavailable when the connection drops.
/// Disabled entities are removed from Home Assistant by clearing their retained discovery config.
/// Commands arrive on each entity's <c>set</c> topic: <c>PRESS</c> for a button, <c>on</c>/<c>off</c> for a switch,
/// the value for a number and the message for a notification.
/// </summary>
public sealed partial class MqttEngine : ICommunicationEngine
{
    private const string Online = "online";
    private const string Offline = "offline";
    private const string PressPayload = "PRESS";
    private const int MaxStateLength = 255;
    private const int ShortLivedConnectionsBeforeWarning = 3;

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IEventBus _bus;
    private readonly IEntityRegistry _registry;
    private readonly IEntityFilter _filter;
    private readonly MqttOptions _options;
    private readonly ILogger _logger;
    private readonly MqttTopics _topics;
    private readonly DiscoveryDevice _device;
    private readonly IMqttClient _client;
    private readonly ConcurrentDictionary<string, TelemetryEvent> _lastReadings = new(StringComparer.Ordinal);

    // Discovery topic and id of entities unregistered while disconnected; cleared on the next connect.
    private readonly ConcurrentDictionary<string, string> _pendingRemovals = new(StringComparer.Ordinal);

    private CancellationTokenSource? _stopping;
    private CancellationToken _stoppingToken;
    private Task _running = Task.CompletedTask;
    private TaskCompletionSource _disconnected = NewSignal();
    private volatile EngineConnectionState _state;

    public MqttEngine(
        IEventBus bus,
        IEntityRegistry registry,
        IOptions<MqttOptions> options,
        ILogger<MqttEngine> logger,
        IEntityFilter? filter = null)
    {
        _bus = bus;
        _registry = registry;
        _filter = filter ?? EntityFilter.AllEnabled;
        _options = options.Value;
        _logger = logger;

        var deviceId = MqttTopics.ToTopicSegment(NullIfEmpty(_options.DeviceId) ?? Environment.MachineName);
        _topics = new MqttTopics(_options.DiscoveryPrefix, _options.BaseTopic, deviceId);
        _device = new DiscoveryDevice(
            Identifiers: [$"hada_{deviceId}"],
            Name: NullIfEmpty(_options.DeviceName) ?? Environment.MachineName,
            Manufacturer: "HADA",
            Model: "Windows",
            SwVersion: typeof(MqttEngine).Assembly.GetName().Version?.ToString());

        _client = new MqttClientFactory().CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
        _client.DisconnectedAsync += _ =>
        {
            _disconnected.TrySetResult();
            return Task.CompletedTask;
        };
    }

    public string Name => "mqtt";

    public EngineConnectionState State => _state;

    /// <summary>
    /// Connects to the broker with <paramref name="options"/> and disconnects again without publishing anything.
    /// A unique client id keeps a running engine's session from being taken over.
    /// </summary>
    public static async Task<ConnectionTestResult> TestConnectionAsync(MqttOptions options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Host))
        {
            return new ConnectionTestResult(false, "No MQTT broker host is configured.");
        }

        using var client = new MqttClientFactory().CreateMqttClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TestTimeout);
        try
        {
            await client.ConnectAsync(CreateClientOptions(options, $"hada-test-{Guid.NewGuid():N}").Build(), timeout.Token)
                .ConfigureAwait(false);
            await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), CancellationToken.None)
                .ConfigureAwait(false);
            return new ConnectionTestResult(true, $"Connected to MQTT broker {options.Host}:{options.Port}.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ConnectionTestResult(
                false, $"MQTT broker {options.Host}:{options.Port} did not respond within {TestTimeout.TotalSeconds:0} s.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ConnectionTestResult(false, Describe(ex));
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Host))
        {
            LogNotConfigured(_logger);
            return Task.CompletedTask;
        }

        if (_stopping is not null)
        {
            throw new InvalidOperationException("The MQTT engine is already running.");
        }

        _stopping = new CancellationTokenSource();
        _stoppingToken = _stopping.Token;
        var token = _stoppingToken;

        // Subscribe before connecting so readings and registrations made in the meantime are not missed.
        var readings = _bus.Subscribe<TelemetryEvent>(
            new EventSubscriptionOptions { Capacity = 256, Backpressure = BackpressureMode.DropOldest });
        var registryChanges = _bus.Subscribe<EntityRegistryChange>();

        // Not started with the caller's token: the loops end through StopAsync, not when starting is cancelled.
        _running = Task.WhenAll(
            Task.Run(() => MaintainConnectionAsync(token), CancellationToken.None),
            Task.Run(() => ForwardTelemetryAsync(readings, token), CancellationToken.None),
            Task.Run(() => FollowRegistryAsync(registryChanges, token), CancellationToken.None));

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stopping is null)
        {
            return;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        await _running.WaitAsync(cancellationToken).ConfigureAwait(false);
        _stopping.Dispose();
        _stopping = null;

        if (_client.IsConnected)
        {
            try
            {
                // A clean disconnect suppresses the last will, so report offline explicitly first.
                await PublishAsync(_topics.Availability, Offline, retain: true, cancellationToken).ConfigureAwait(false);
                await _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogDisconnectFailed(_logger, ex);
            }
        }

        _state = EngineConnectionState.Disconnected;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _client.Dispose();
    }

    private async Task MaintainConnectionAsync(CancellationToken cancellationToken)
    {
        var retryDelay = _options.MinReconnectDelay;
        var shortLivedConnections = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _state = EngineConnectionState.Connecting;
                _disconnected = NewSignal();
                await ConnectAsync(cancellationToken).ConfigureAwait(false);
                var connectedAt = Stopwatch.GetTimestamp();

                await _disconnected.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                _state = EngineConnectionState.Disconnected;

                // A broker lets one client per client id in and drops the previous one, so two computers (or two
                // copies of the service) sharing a Device ID throw each other out the moment they connect. Only a
                // connection that lasted counts as recovered; otherwise the delay keeps growing, instead of both
                // sides reconnecting every other second forever.
                if (Stopwatch.GetElapsedTime(connectedAt) >= _options.StableConnectionTime)
                {
                    retryDelay = _options.MinReconnectDelay;
                    shortLivedConnections = 0;
                }
                else if (++shortLivedConnections == ShortLivedConnectionsBeforeWarning)
                {
                    LogConnectionKeepsDropping(_logger, _topics.DeviceId);
                }

                LogDisconnected(_logger, retryDelay);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _state = EngineConnectionState.Faulted;
                LogConnectFailed(_logger, Describe(ex), retryDelay);
                await _client.TryDisconnectAsync(MqttClientDisconnectOptionsReason.NormalDisconnection, "reconnecting")
                    .ConfigureAwait(false);
            }

            try
            {
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            retryDelay = TimeSpan.FromTicks(Math.Min(retryDelay.Ticks * 2, _options.MaxReconnectDelay.Ticks));
        }
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await _client.ConnectAsync(BuildClientOptions(), cancellationToken).ConfigureAwait(false);
        await _client.SubscribeAsync(_topics.HomeAssistantStatus, MqttQualityOfServiceLevel.AtLeastOnce, cancellationToken)
            .ConfigureAwait(false);
        await _client.SubscribeAsync(_topics.CommandFilter, MqttQualityOfServiceLevel.AtLeastOnce, cancellationToken)
            .ConfigureAwait(false);
        await PublishAsync(_topics.Availability, Online, retain: true, cancellationToken).ConfigureAwait(false);

        // Mark connected before announcing so readings arriving meanwhile are published, not just cached.
        _state = EngineConnectionState.Connected;
        LogConnected(_logger, _options.Host!, _options.Port, _topics.DeviceId);
        await AnnounceAllAsync(cancellationToken).ConfigureAwait(false);
    }

    private MqttClientOptions BuildClientOptions() =>
        CreateClientOptions(_options, $"hada-{_topics.DeviceId}")
            .WithWillTopic(_topics.Availability)
            .WithWillPayload(Offline)
            .WithWillRetain(true)
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

    private static MqttClientOptionsBuilder CreateClientOptions(MqttOptions options, string clientId)
    {
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(options.Host, options.Port, System.Net.Sockets.AddressFamily.Unspecified)
            .WithClientId(clientId)
            .WithCleanSession(true)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30));

        if (NullIfEmpty(options.Username) is { } username)
        {
            builder.WithCredentials(username, options.Password);
        }

        if (options.UseTls)
        {
            builder.WithTlsOptions(tls => tls.UseTls(true));
        }

        return builder;
    }

    private Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        var message = e.ApplicationMessage;

        // Retained messages are replays, not live events: never re-run a command or re-announce because of one.
        if (message.Retain)
        {
            return Task.CompletedTask;
        }

        var payload = message.ConvertPayloadToString();
        if (message.Topic == _topics.HomeAssistantStatus)
        {
            if (payload == Online)
            {
                LogHomeAssistantOnline(_logger);

                // Publishing from inside the receive handler can stall the client, so hand the work off.
                var token = _stoppingToken;
                _ = Task.Run(() => AnnounceAllAsync(token), token);
            }

            return Task.CompletedTask;
        }

        return _topics.TryGetCommandEntityId(message.Topic, out var entityId)
            ? DispatchCommandAsync(entityId, payload)
            : Task.CompletedTask;
    }

    private async Task DispatchCommandAsync(string entityId, string payload)
    {
        if (!TryGetExposed(entityId, out var entity)
            || !entity.Kind.AcceptsCommands()
            || (entity.Kind == EntityKind.Button && payload != PressPayload)
            || !CommandValue.TryNormalize(entity, SplitNotification(entity, payload, out var title), out var value))
        {
            // Not the payload: a notification's text is nobody's business but the user's.
            LogIgnoredCommand(_logger, entityId);
            return;
        }

        var command = new ActionCommand { ActionId = entity.Id, Value = value, Origin = Name };
        try
        {
            await _bus.PublishAsync(
                    title is null ? command : command with { Parameters = new Dictionary<string, object?> { ["title"] = title } },
                    _stoppingToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Engine is stopping.
        }
    }

    /// <summary>
    /// Home Assistant's notify entity sends the message as plain text. To send a title as well, publish JSON
    /// instead: <c>{"title": "…", "message": "…"}</c>.
    /// </summary>
    private static string? SplitNotification(EntityDescriptor entity, string? payload, out string? title)
    {
        title = null;
        if (entity.Kind != EntityKind.Notify || payload is null || !payload.AsSpan().TrimStart().StartsWith("{"))
        {
            return payload;
        }

        try
        {
            using var json = JsonDocument.Parse(payload);
            if (json.RootElement.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            {
                if (json.RootElement.TryGetProperty("title", out var titleProperty) && titleProperty.ValueKind == JsonValueKind.String)
                {
                    title = titleProperty.GetString();
                }

                return message.GetString();
            }
        }
        catch (JsonException)
        {
            // Not JSON after all, just a message that starts with a brace.
        }

        return payload;
    }

    private async Task ForwardTelemetryAsync(IEventSubscription<TelemetryEvent> readings, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var reading in readings.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!TryGetExposed(reading.SensorId, out var entity) || !entity.Kind.ReportsState())
                {
                    continue;
                }

                // Cached so the latest reading can be re-sent after a reconnect or a Home Assistant restart.
                _lastReadings[entity.Id] = reading;
                if (_state == EngineConnectionState.Connected)
                {
                    await TryPublishReadingAsync(reading, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await readings.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task FollowRegistryAsync(IEventSubscription<EntityRegistryChange> changes, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var change in changes.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var connected = _state == EngineConnectionState.Connected;
                switch (change)
                {
                    case EntityUnregistered removed:
                        _pendingRemovals[_topics.Discovery(removed.Entity)] = removed.Entity.Id;
                        if (connected)
                        {
                            await ClearPendingRemovalsAsync(cancellationToken).ConfigureAwait(false);
                        }

                        break;
                    case EntityRegistered registered when _registry.TryGet(registered.Entity.Id, out var entity):
                        _pendingRemovals.TryRemove(_topics.Discovery(entity), out _);
                        if (connected)
                        {
                            await TryAnnounceEntityAsync(entity, cancellationToken).ConfigureAwait(false);
                        }

                        break;
                    case EntityAvailabilityChanged availability when connected && _filter.IsEnabled(availability.Entity):
                        await TryPublishEntityAvailabilityAsync(availability.Entity.Id, cancellationToken).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await changes.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ClearPendingRemovalsAsync(CancellationToken cancellationToken)
    {
        foreach (var (topic, entityId) in _pendingRemovals)
        {
            if (await TryRemoveEntityAsync(topic, entityId, cancellationToken).ConfigureAwait(false))
            {
                _pendingRemovals.TryRemove(topic, out _);
            }
        }
    }

    private async Task AnnounceAllAsync(CancellationToken cancellationToken)
    {
        await ClearPendingRemovalsAsync(cancellationToken).ConfigureAwait(false);

        var entities = _registry.Entities;
        foreach (var entity in entities)
        {
            await TryAnnounceEntityAsync(entity, cancellationToken).ConfigureAwait(false);
        }

        var enabled = entities.Count(_filter.IsEnabled);
        LogAnnounced(_logger, enabled, _options.DiscoveryPrefix, _topics.DeviceId, entities.Count - enabled);

        foreach (var reading in _lastReadings.Values)
        {
            await TryPublishReadingAsync(reading, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TryAnnounceEntityAsync(EntityDescriptor entity, CancellationToken cancellationToken)
    {
        if (!_filter.IsEnabled(entity))
        {
            await TryRemoveEntityAsync(_topics.Discovery(entity), entity.Id, cancellationToken).ConfigureAwait(false);
            return;
        }

        var isSensor = entity.Kind.ReportsState();
        var isButton = entity.Kind == EntityKind.Button;
        var isBinary = entity.Kind.IsBinary();
        var isNumber = entity.Kind == EntityKind.Number;
        var payload = new DiscoveryPayload(
            Name: entity.Name,
            UniqueId: $"{_device.Identifiers[0]}_{entity.Id}",
            // The device is connected, and the entity's own source (e.g. the tray app) is there.
            Availability: [new(_topics.Availability), new(_topics.EntityAvailability(entity.Id))],
            AvailabilityMode: "all",
            StateTopic: isSensor ? _topics.State(entity.Id) : null,
            JsonAttributesTopic: isSensor ? _topics.Attributes(entity.Id) : null,
            CommandTopic: entity.Kind.AcceptsCommands() ? _topics.Command(entity.Id) : null,
            PayloadPress: isButton ? PressPayload : null,
            PayloadOn: isBinary ? BinaryState.On : null,
            PayloadOff: isBinary ? BinaryState.Off : null,
            Icon: entity.Icon,
            DeviceClass: entity.DeviceClass,
            UnitOfMeasurement: entity.UnitOfMeasurement,
            StateClass: entity.StateClass,
            Min: isNumber ? entity.Min : null,
            Max: isNumber ? entity.Max : null,
            Step: isNumber ? entity.Step : null,
            Device: _device);

        await TryPublishAsync(_topics.Discovery(entity), JsonSerializer.Serialize(payload, JsonOptions), retain: true, cancellationToken)
            .ConfigureAwait(false);
        await TryPublishEntityAvailabilityAsync(entity.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Retained, so the entity stays unavailable across reconnects for as long as its source is away.</summary>
    private Task TryPublishEntityAvailabilityAsync(string entityId, CancellationToken cancellationToken) =>
        TryPublishAsync(
            _topics.EntityAvailability(entityId), _registry.IsAvailable(entityId) ? Online : Offline, retain: true, cancellationToken);

    /// <remarks>
    /// Retained, because Home Assistant subscribes to an entity's topics only after it has processed the discovery
    /// config: a state sent right behind the config would be missed, and a sensor that reports only changes would
    /// stay "unknown" until its next change. Stale values are no concern, as the availability topic covers those.
    /// </remarks>
    private async Task TryPublishReadingAsync(TelemetryEvent reading, CancellationToken cancellationToken)
    {
        if (reading.Attributes.Count > 0)
        {
            await TryPublishAsync(
                    _topics.Attributes(reading.SensorId), JsonSerializer.Serialize(reading.Attributes), retain: true, cancellationToken)
                .ConfigureAwait(false);
        }

        var state = reading.State.Length > MaxStateLength ? reading.State[..MaxStateLength] : reading.State;
        await TryPublishAsync(_topics.State(reading.SensorId), state, retain: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes an entity from Home Assistant, along with the retained readings it left on the broker.</summary>
    private async Task<bool> TryRemoveEntityAsync(string discoveryTopic, string entityId, CancellationToken cancellationToken)
    {
        // An empty retained payload deletes the retained message; an empty config makes Home Assistant drop the entity.
        var removed = await TryPublishAsync(discoveryTopic, string.Empty, retain: true, cancellationToken).ConfigureAwait(false);

        // An entity that was replaced under the same id, e.g. a custom sensor whose type changed, keeps its topics:
        // its first new reading may already be there.
        if (!TryGetExposed(entityId, out _))
        {
            _lastReadings.TryRemove(entityId, out _);
            await TryPublishAsync(_topics.State(entityId), string.Empty, retain: true, cancellationToken).ConfigureAwait(false);
            await TryPublishAsync(_topics.Attributes(entityId), string.Empty, retain: true, cancellationToken).ConfigureAwait(false);
            await TryPublishAsync(_topics.EntityAvailability(entityId), string.Empty, retain: true, cancellationToken).ConfigureAwait(false);
        }

        return removed;
    }

    private async Task<bool> TryPublishAsync(string topic, string payload, bool retain, CancellationToken cancellationToken)
    {
        try
        {
            await PublishAsync(topic, payload, retain, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The connection loop owns recovery; cached readings are re-sent after reconnecting.
            LogPublishFailed(_logger, ex, topic);
            return false;
        }
    }

    private Task PublishAsync(string topic, string payload, bool retain, CancellationToken cancellationToken) =>
        _client.PublishAsync(
            new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithRetainFlag(retain)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build(),
            cancellationToken);

    private bool TryGetExposed(string entityId, [MaybeNullWhen(false)] out EntityDescriptor entity) =>
        _registry.TryGet(entityId, out entity) && _filter.IsEnabled(entity);

    private static string Describe(Exception exception) =>
        exception.InnerException is null ? exception.Message : $"{exception.Message} ({exception.GetBaseException().Message})";

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    [LoggerMessage(Level = LogLevel.Warning, Message = "MQTT engine is idle: Mqtt:Host is not configured.")]
    private static partial void LogNotConfigured(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected to MQTT broker {Host}:{Port} as device '{DeviceId}'.")]
    private static partial void LogConnected(ILogger logger, string host, int port, string deviceId);

    // What Home Assistant should now show; the first thing to compare when entities are missing there.
    [LoggerMessage(Level = LogLevel.Information, Message = "Announced {Count} entities to Home Assistant on '{DiscoveryPrefix}/<type>/{DeviceId}/<entity>/config'; {Disabled} disabled ones were removed.")]
    private static partial void LogAnnounced(ILogger logger, int count, string discoveryPrefix, string deviceId, int disabled);

    [LoggerMessage(Level = LogLevel.Error, Message = "The MQTT connection keeps dropping right after it is made. Another HADA is probably connected with the same Device ID '{DeviceId}': every computer needs its own, and only one copy of the service may run. Retrying less and less often.")]
    private static partial void LogConnectionKeepsDropping(ILogger logger, string deviceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Disconnected from MQTT broker; reconnecting in {RetryDelay}.")]
    private static partial void LogDisconnected(ILogger logger, TimeSpan retryDelay);

    // The reason only: the stack trace of a refused connection says nothing more, and it repeats on every retry.
    [LoggerMessage(Level = LogLevel.Warning, Message = "MQTT connection failed: {Reason} Retrying in {RetryDelay}.")]
    private static partial void LogConnectFailed(ILogger logger, string reason, TimeSpan retryDelay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to disconnect cleanly from MQTT broker.")]
    private static partial void LogDisconnectFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Failed to publish to {Topic}.")]
    private static partial void LogPublishFailed(ILogger logger, Exception exception, string topic);

    [LoggerMessage(Level = LogLevel.Information, Message = "Home Assistant came online; re-announcing entities.")]
    private static partial void LogHomeAssistantOnline(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ignored a command for '{EntityId}': the entity is not exposed, or does not accept that payload.")]
    private static partial void LogIgnoredCommand(ILogger logger, string entityId);
}

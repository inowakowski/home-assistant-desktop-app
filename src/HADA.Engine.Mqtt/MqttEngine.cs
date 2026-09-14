using System.Collections.Concurrent;
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
/// </summary>
public sealed partial class MqttEngine : ICommunicationEngine
{
    private const string Online = "online";
    private const string Offline = "offline";
    private const string PressPayload = "PRESS";
    private const int MaxStateLength = 255;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IEventBus _bus;
    private readonly IEntityRegistry _registry;
    private readonly MqttOptions _options;
    private readonly ILogger _logger;
    private readonly MqttTopics _topics;
    private readonly DiscoveryDevice _device;
    private readonly IMqttClient _client;
    private readonly ConcurrentDictionary<string, TelemetryEvent> _lastReadings = new(StringComparer.Ordinal);

    private CancellationTokenSource? _stopping;
    private CancellationToken _stoppingToken;
    private Task _running = Task.CompletedTask;
    private TaskCompletionSource _disconnected = NewSignal();
    private volatile EngineConnectionState _state;

    public MqttEngine(IEventBus bus, IEntityRegistry registry, IOptions<MqttOptions> options, ILogger<MqttEngine> logger)
    {
        _bus = bus;
        _registry = registry;
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
        var registrations = _bus.Subscribe<EntityRegistered>();

        _running = Task.WhenAll(
            Task.Run(() => MaintainConnectionAsync(token)),
            Task.Run(() => ForwardTelemetryAsync(readings, token)),
            Task.Run(() => AnnounceRegistrationsAsync(registrations, token)));

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
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _state = EngineConnectionState.Connecting;
                _disconnected = NewSignal();
                await ConnectAsync(cancellationToken).ConfigureAwait(false);
                retryDelay = _options.MinReconnectDelay;

                await _disconnected.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                _state = EngineConnectionState.Disconnected;
                LogDisconnected(_logger, retryDelay);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _state = EngineConnectionState.Faulted;
                LogConnectFailed(_logger, ex, retryDelay);
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

    private MqttClientOptions BuildClientOptions()
    {
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(_options.Host, _options.Port, System.Net.Sockets.AddressFamily.Unspecified)
            .WithClientId($"hada-{_topics.DeviceId}")
            .WithCleanSession(true)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
            .WithWillTopic(_topics.Availability)
            .WithWillPayload(Offline)
            .WithWillRetain(true)
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);

        if (NullIfEmpty(_options.Username) is { } username)
        {
            builder.WithCredentials(username, _options.Password);
        }

        if (_options.UseTls)
        {
            builder.WithTlsOptions(tls => tls.UseTls(true));
        }

        return builder.Build();
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
        if (!_registry.TryGet(entityId, out var entity) || entity.Kind != EntityKind.Button || payload != PressPayload)
        {
            LogIgnoredCommand(_logger, entityId, payload);
            return;
        }

        try
        {
            await _bus.PublishAsync(new ActionCommand { ActionId = entity.Id, Origin = Name }, _stoppingToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Engine is stopping.
        }
    }

    private async Task ForwardTelemetryAsync(IEventSubscription<TelemetryEvent> readings, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var reading in readings.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!_registry.TryGet(reading.SensorId, out var entity) || entity.Kind != EntityKind.Sensor)
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

    private async Task AnnounceRegistrationsAsync(IEventSubscription<EntityRegistered> registrations, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var registration in registrations.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_state == EngineConnectionState.Connected)
                {
                    await TryPublishDiscoveryAsync(registration.Entity, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await registrations.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task AnnounceAllAsync(CancellationToken cancellationToken)
    {
        foreach (var entity in _registry.Entities)
        {
            await TryPublishDiscoveryAsync(entity, cancellationToken).ConfigureAwait(false);
        }

        foreach (var reading in _lastReadings.Values)
        {
            await TryPublishReadingAsync(reading, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task TryPublishDiscoveryAsync(EntityDescriptor entity, CancellationToken cancellationToken)
    {
        var isSensor = entity.Kind == EntityKind.Sensor;
        var isButton = entity.Kind == EntityKind.Button;
        var payload = new DiscoveryPayload(
            Name: entity.Name,
            UniqueId: $"{_device.Identifiers[0]}_{entity.Id}",
            AvailabilityTopic: _topics.Availability,
            StateTopic: isSensor ? _topics.State(entity.Id) : null,
            JsonAttributesTopic: isSensor ? _topics.Attributes(entity.Id) : null,
            CommandTopic: isButton ? _topics.Command(entity.Id) : null,
            PayloadPress: isButton ? PressPayload : null,
            Icon: entity.Icon,
            DeviceClass: entity.DeviceClass,
            UnitOfMeasurement: entity.UnitOfMeasurement,
            StateClass: entity.StateClass,
            Device: _device);

        return TryPublishAsync(_topics.Discovery(entity), JsonSerializer.Serialize(payload, JsonOptions), retain: true, cancellationToken);
    }

    private async Task TryPublishReadingAsync(TelemetryEvent reading, CancellationToken cancellationToken)
    {
        if (reading.Attributes.Count > 0)
        {
            await TryPublishAsync(
                    _topics.Attributes(reading.SensorId), JsonSerializer.Serialize(reading.Attributes), retain: false, cancellationToken)
                .ConfigureAwait(false);
        }

        var state = reading.State.Length > MaxStateLength ? reading.State[..MaxStateLength] : reading.State;
        await TryPublishAsync(_topics.State(reading.SensorId), state, retain: false, cancellationToken).ConfigureAwait(false);
    }

    private async Task TryPublishAsync(string topic, string payload, bool retain, CancellationToken cancellationToken)
    {
        try
        {
            await PublishAsync(topic, payload, retain, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The connection loop owns recovery; cached readings are re-sent after reconnecting.
            LogPublishFailed(_logger, ex, topic);
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

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    [LoggerMessage(Level = LogLevel.Warning, Message = "MQTT engine is idle: Mqtt:Host is not configured.")]
    private static partial void LogNotConfigured(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected to MQTT broker {Host}:{Port} as device '{DeviceId}'.")]
    private static partial void LogConnected(ILogger logger, string host, int port, string deviceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Disconnected from MQTT broker; reconnecting in {RetryDelay}.")]
    private static partial void LogDisconnected(ILogger logger, TimeSpan retryDelay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MQTT connection failed; retrying in {RetryDelay}.")]
    private static partial void LogConnectFailed(ILogger logger, Exception exception, TimeSpan retryDelay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to disconnect cleanly from MQTT broker.")]
    private static partial void LogDisconnectFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Failed to publish to {Topic}.")]
    private static partial void LogPublishFailed(ILogger logger, Exception exception, string topic);

    [LoggerMessage(Level = LogLevel.Information, Message = "Home Assistant came online; re-announcing entities.")]
    private static partial void LogHomeAssistantOnline(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ignored command for '{EntityId}' with payload '{Payload}'.")]
    private static partial void LogIgnoredCommand(ILogger logger, string entityId, string payload);
}

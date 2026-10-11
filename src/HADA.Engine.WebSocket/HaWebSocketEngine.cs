using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HADA.Engine.WebSocket;

/// <summary>
/// Engine B: talks to Home Assistant directly, without an MQTT broker.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The WebSocket API (<c>/api/websocket</c>) carries authentication, a heartbeat and commands, which arrive as
/// custom events (<see cref="HaWebSocketOptions.CommandEventType"/>) naming the device and the entity, with a
/// <c>value</c> for switches and numbers, or a <c>message</c> and optional <c>title</c> for notifications.</item>
/// <item>Sensor states are written through the REST API (<c>POST /api/states/sensor.*</c> or <c>binary_sensor.*</c>), since the WebSocket API
/// cannot set states. These entities have no unique id, so they cannot be edited in the Home Assistant UI and
/// disappear when Home Assistant restarts; the engine re-sends them after every reconnect.</item>
/// <item>Sensors disabled in settings are deleted from Home Assistant (<c>DELETE /api/states/sensor.*</c>) on connect.</item>
/// <item>On graceful shutdown every sensor is set to <c>unavailable</c> before the socket is closed.
/// A crash leaves the last states in place, as there is no equivalent of an MQTT last will.</item>
/// </list>
/// </remarks>
public sealed partial class HaWebSocketEngine : ICommunicationEngine
{
    private const int MaxStateLength = 255;

    // How many readings go out in one message at most; more than a computer has sensors, as a rule.
    private const int MaxBatch = 100;
    private const string Unavailable = "unavailable";

    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    private readonly IEventBus _bus;
    private readonly IEntityRegistry _registry;
    private readonly IEntityFilter _filter;
    private readonly HaWebSocketOptions _options;
    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private readonly string _deviceId;
    private readonly string _deviceName;
    private readonly string _serverId;
    private readonly IMobileAppRegistrationStore _registrations;
    private readonly ConcurrentDictionary<string, TelemetryEvent> _lastReadings = new(StringComparer.Ordinal);

    // The address in use: the main one, or the local one while the computer is on a home network.
    private volatile Uri? _baseUrl;
    private CancellationTokenSource? _stopping;
    private CancellationToken _stoppingToken;
    private Task _connectionLoop = Task.CompletedTask;
    private Task _telemetryLoop = Task.CompletedTask;
    private Task _registryLoop = Task.CompletedTask;
    private Task _eventLoop = Task.CompletedTask;
    private volatile HaConnection? _connection;
    private volatile EngineConnectionState _state;
    private volatile bool _isAdministrator;
    private bool _statesRemoved;

    public HaWebSocketEngine(
        IEventBus bus,
        IEntityRegistry registry,
        IOptions<HaWebSocketOptions> options,
        ILogger<HaWebSocketEngine> logger,
        IEntityFilter? filter = null,
        IMobileAppRegistrationStore? registrations = null,
        INetworkLocation? networks = null)
    {
        _bus = bus;
        _registry = registry;
        _filter = filter ?? EntityFilter.AllEnabled;
        _registrations = registrations ?? new InMemoryMobileAppRegistrationStore();
        _networks = networks ?? UnknownNetworkLocation.Instance;
        _options = options.Value;
        _serverId = NullIfEmpty(_options.Id) ?? HaWebSocketOptions.DefaultId;
        _logger = logger;
        _deviceId = ToObjectId(NullIfEmpty(_options.DeviceId) ?? Environment.MachineName);
        _deviceName = NullIfEmpty(_options.DeviceName) ?? Environment.MachineName;

        // Several of these engines can run side by side, one per Home Assistant, so each says which one it is.
        var serverName = NullIfEmpty(_options.Name)?.Trim();
        Name = serverName is null ? EngineName : $"{EngineName} ({serverName})";
        _http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
    }

    /// <summary>What every WebSocket engine's <see cref="Name"/> starts with; the whole name of one whose server has no name.</summary>
    public const string EngineName = "websocket";

    /// <summary><c>websocket</c>, or <c>websocket (Flat)</c> for a Home Assistant the user named.</summary>
    public string Name { get; }

    public EngineConnectionState State => _state;

    /// <summary>
    /// Connects, authenticates and subscribes to the command event with <paramref name="options"/>, then closes again.
    /// Proves both that the token is valid and that it belongs to an administrator; a connection that is there for
    /// notifications may do without the latter. Registers nothing with Home Assistant.
    /// </summary>
    public static async Task<ConnectionTestResult> TestConnectionAsync(HaWebSocketOptions options, CancellationToken cancellationToken)
    {
        if (!TryParseBaseUrl(options.BaseUrl, out var baseUrl))
        {
            return new ConnectionTestResult(false, "The Home Assistant URL must be an absolute http:// or https:// address.");
        }

        if (string.IsNullOrWhiteSpace(options.AccessToken))
        {
            return new ConnectionTestResult(false, "No access token is configured.");
        }

        var main = await TestAddressAsync(options, baseUrl, cancellationToken).ConfigureAwait(false);
        if (NullIfEmpty(options.LocalUrl) is null)
        {
            return main;
        }

        // Both are tried, wherever the computer is: the test is asked for by someone who is there to read the answer.
        var local = TryParseBaseUrl(options.LocalUrl, out var localUrl)
            ? await TestAddressAsync(options, localUrl, cancellationToken).ConfigureAwait(false)
            : new ConnectionTestResult(false, "The local address must be an absolute http:// or https:// address.");
        return new ConnectionTestResult(
            main.Success && local.Success, $"Main address: {main.Message}{Environment.NewLine}Local address: {local.Message}");
    }

    private static async Task<ConnectionTestResult> TestAddressAsync(HaWebSocketOptions options, Uri baseUrl, CancellationToken cancellationToken)
    {
        using var connection = new HaConnection(new ClientWebSocket());
        try
        {
            var (haVersion, hasCommands) = await HandshakeAsync(
                    connection, baseUrl, options.AccessToken!, options.CommandEventType, commandsRequired: !WorksWithoutAdministrator(options),
                    options.AllowUnencrypted, cancellationToken)
                .ConfigureAwait(false);

            using var closeTimeout = new CancellationTokenSource(CloseTimeout);
            try
            {
                await connection.CloseOutputAsync(closeTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or InvalidOperationException)
            {
                // The test already succeeded; a messy close does not change that.
            }

            return new ConnectionTestResult(
                true,
                hasCommands
                    ? $"Connected to Home Assistant {haVersion ?? "(unknown version)"}; the token can subscribe to '{options.CommandEventType}' events."
                    : $"Connected to Home Assistant {haVersion ?? "(unknown version)"}. The token is not an administrator's: notifications and sensors as entities will work, but sensors as states and '{options.CommandEventType}' events will not.");
        }
        catch (HaAuthenticationException ex)
        {
            return new ConnectionTestResult(false, $"Home Assistant rejected the access token: {ex.Message}");
        }
        catch (HaUnencryptedException ex)
        {
            return new ConnectionTestResult(
                false, $"{ex.Message} Use an https:// address, or allow the unencrypted connection for this server.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ConnectionTestResult(false, $"Home Assistant did not respond within {HandshakeTimeout.TotalSeconds:0} s.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ConnectionTestResult(false, Describe(ex));
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!TryParseBaseUrl(_options.BaseUrl, out var baseUrl) || string.IsNullOrWhiteSpace(_options.AccessToken))
        {
            LogNotConfigured(_logger);
            return Task.CompletedTask;
        }

        if (_stopping is not null)
        {
            throw new InvalidOperationException("The WebSocket engine is already running.");
        }

        _mainUrl = baseUrl;
        _baseUrl = baseUrl;
        StartFollowingNetworks();
        _stopping = new CancellationTokenSource();
        _stoppingToken = _stopping.Token;
        var token = _stoppingToken;

        // Subscribe before connecting so readings produced meanwhile are cached and sent once connected.
        var readings = _bus.Subscribe<TelemetryEvent>(
            new EventSubscriptionOptions { Capacity = 256, Backpressure = BackpressureMode.DropOldest });

        var registryChanges = _bus.Subscribe<EntityRegistryChange>();

        // Not started with the caller's token: the loops end through StopAsync, not when starting is cancelled.
        _connectionLoop = Task.Run(() => MaintainConnectionAsync(token), CancellationToken.None);
        _telemetryLoop = Task.Run(() => ForwardTelemetryAsync(readings, token), CancellationToken.None);
        _registryLoop = Task.Run(() => FollowRegistryAsync(registryChanges, token), CancellationToken.None);

        var events = _bus.Subscribe<DeviceEvent>(
            new EventSubscriptionOptions { Capacity = 32, Backpressure = BackpressureMode.DropOldest });
        _eventLoop = Task.Run(() => ForwardEventsAsync(events, token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stopping is null)
        {
            return;
        }

        // Stop producing updates first so nothing overwrites the "unavailable" states below.
        await _stopping.CancelAsync().ConfigureAwait(false);
        await _telemetryLoop.WaitAsync(cancellationToken).ConfigureAwait(false);
        await _registryLoop.WaitAsync(cancellationToken).ConfigureAwait(false);
        await _eventLoop.WaitAsync(cancellationToken).ConfigureAwait(false);

        if (_state == EngineConnectionState.Connected)
        {
            await MarkSensorsUnavailableAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_connection is { } connection)
        {
            try
            {
                await connection.CloseOutputAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or InvalidOperationException)
            {
                // Already closing or broken; the loop below still ends.
            }
        }

        try
        {
            await _connectionLoop.WaitAsync(CloseTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _connection?.Abort();
            await _connectionLoop.ConfigureAwait(false);
        }

        StopFollowingNetworks();
        _stopping.Dispose();
        _stopping = null;
        _state = EngineConnectionState.Disconnected;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _http.Dispose();
        _integrationSignal.Dispose();
    }

    private async Task MaintainConnectionAsync(CancellationToken cancellationToken)
    {
        var retryDelay = _options.MinReconnectDelay;
        while (!cancellationToken.IsCancellationRequested)
        {
            _state = EngineConnectionState.Connecting;
            _baseUrl = ChooseAddress();
            using var connection = new HaConnection(new ClientWebSocket());
            try
            {
                var (haVersion, hasCommands) = await HandshakeAsync(
                        connection, _baseUrl!, _options.AccessToken!, _options.CommandEventType, commandsRequired: !WorksWithoutAdministrator(_options),
                        _options.AllowUnencrypted, cancellationToken)
                    .ConfigureAwait(false);
                SetConnectionIssue(null);
                LogConnected(_logger, _baseUrl!, haVersion ?? "unknown", _deviceId);
                _isAdministrator = hasCommands;
                if (!hasCommands)
                {
                    LogNoCommands(_logger, _options.CommandEventType);
                }

                if (UsesMobileApp(_options))
                {
                    await ConnectMobileAppAsync(connection, cancellationToken).ConfigureAwait(false);
                }

                _connection = connection;
                _state = EngineConnectionState.Connected;
                AddressAnswered();
                retryDelay = _options.MinReconnectDelay;

                await RemoveDisabledSensorsAsync(cancellationToken).ConfigureAwait(false);
                await PublishCachedReadingsAsync(cancellationToken).ConfigureAwait(false);
                await RunSessionAsync(connection, cancellationToken).ConfigureAwait(false);

                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                _state = EngineConnectionState.Disconnected;
                LogDisconnected(_logger, retryDelay);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (HaAuthenticationException ex)
            {
                // Retrying with the same token is pointless and can get this machine's IP banned by Home Assistant.
                _state = EngineConnectionState.Faulted;
                LogAuthenticationFailed(_logger, ex.Message);
                return;
            }
            catch (HaUnencryptedException)
            {
                // Tried again all the same: a name may stand for a local address on one network and a public one
                // on another, as a laptop finds out.
                _state = EngineConnectionState.Faulted;
                SetConnectionIssue(IssueUnencrypted);
                if (AddressDidNotAnswer())
                {
                    // It was the local address that was refused; the main one may be fine.
                    retryDelay = TimeSpan.Zero;
                }
            }
            catch (Exception ex)
            {
                _state = EngineConnectionState.Faulted;
                SetConnectionIssue(null);
                if (AddressDidNotAnswer())
                {
                    // The main address is tried next, and at once: Home Assistant may well be there.
                    LogLocalAddressFailed(_logger, _baseUrl!, Describe(ex));
                    retryDelay = TimeSpan.Zero;
                }
                else
                {
                    LogConnectionFailed(_logger, ex, retryDelay);
                }
            }
            finally
            {
                _connection = null;
                DisconnectMobileApp();
                DisconnectIntegration();
            }

            try
            {
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            retryDelay = TimeSpan.FromTicks(
                Math.Clamp(retryDelay.Ticks * 2, _options.MinReconnectDelay.Ticks, _options.MaxReconnectDelay.Ticks));
        }
    }

    /// <summary>
    /// Connects, authenticates and subscribes to the command event. Returns the Home Assistant version, and whether
    /// the subscription was granted, which it is to administrators only.
    /// </summary>
    /// <param name="commandsRequired">Whether a refused subscription is a failure, rather than something to do without.</param>
    /// <param name="allowUnencrypted">Whether an <c>http</c> address out on the internet is connected to all the same.</param>
    private static async Task<(string? HaVersion, bool HasCommands)> HandshakeAsync(
        HaConnection connection,
        Uri baseUrl,
        string accessToken,
        string commandEventType,
        bool commandsRequired,
        bool allowUnencrypted,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HandshakeTimeout);
        var token = timeout.Token;

        // Before anything is sent: the first thing sent after connecting is the access token.
        await EnsureEncryptedOrLocalAsync(baseUrl, allowUnencrypted, token).ConfigureAwait(false);
        await connection.ConnectAsync(WebSocketUri(baseUrl), token).ConfigureAwait(false);

        string? haVersion;
        using (var greeting = await ReceiveRequiredAsync(connection, token).ConfigureAwait(false))
        {
            ExpectType(greeting, "auth_required");
            haVersion = greeting.RootElement.TryGetProperty("ha_version", out var version) ? version.GetString() : null;
        }

        await connection.SendAsync(new { type = "auth", access_token = accessToken }, token).ConfigureAwait(false);
        using (var reply = await ReceiveRequiredAsync(connection, token).ConfigureAwait(false))
        {
            switch (HaConnection.TypeOf(reply))
            {
                case "auth_ok":
                    break;
                case "auth_invalid":
                    throw new HaAuthenticationException(
                        reply.RootElement.TryGetProperty("message", out var message) ? message.GetString() : null);
                default:
                    ExpectType(reply, "auth_ok");
                    break;
            }
        }

        var subscriptionId = connection.NextId();
        await connection.SendAsync(new { id = subscriptionId, type = "subscribe_events", event_type = commandEventType }, token)
            .ConfigureAwait(false);
        using (var result = await ReceiveRequiredAsync(connection, token).ConfigureAwait(false))
        {
            var root = result.RootElement;
            var answered = HaConnection.TypeOf(result) == "result" && root.TryGetProperty("id", out var id) && id.GetInt32() == subscriptionId;
            var granted = answered && root.TryGetProperty("success", out var success) && success.GetBoolean();
            if (!answered || (!granted && commandsRequired))
            {
                throw new InvalidDataException(
                    $"Subscribing to '{commandEventType}' events failed (is the token an administrator's?): {root.GetRawText()}");
            }

            return (haVersion, granted);
        }
    }

    /// <summary>Handles incoming messages until Home Assistant closes the socket or the heartbeat detects a dead link.</summary>
    private async Task RunSessionAsync(HaConnection connection, CancellationToken cancellationToken)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeat = Task.Run(() => KeepAliveAsync(connection, session.Token), CancellationToken.None);
        var integration = UsesIntegration
            ? Task.Run(() => RunIntegrationAsync(connection, session.Token), CancellationToken.None)
            : Task.CompletedTask;
        try
        {
            // Not cancelled by the stopping token: StopAsync closes the socket gracefully instead, which ends this loop.
            while (await connection.ReceiveAsync(CancellationToken.None).ConfigureAwait(false) is { } message)
            {
                using (message)
                {
                    await HandleMessageAsync(message.RootElement).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await session.CancelAsync().ConfigureAwait(false);
            await heartbeat.ConfigureAwait(false);
            await integration.ConfigureAwait(false);
        }
    }

    private async Task KeepAliveAsync(HaConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(_options.HeartbeatInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (connection.SinceLastReceived > _options.HeartbeatInterval * 2)
                {
                    LogHeartbeatTimedOut(_logger);
                    connection.Abort();
                    return;
                }

                await connection.SendAsync(new { id = connection.NextId(), type = "ping" }, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or InvalidOperationException)
        {
            // Session is ending; the receive loop reports why.
        }
    }

    private async Task HandleMessageAsync(JsonElement message)
    {
        if (TryCompleteRequest(message))
        {
            return;
        }

        if (!message.TryGetProperty("type", out var type) || type.GetString() != "event"
            || !message.TryGetProperty("event", out var @event) || @event.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (IsIntegrationCommand(message))
        {
            await HandleIntegrationCommandAsync(@event).ConfigureAwait(false);
            return;
        }

        if (IsPushNotification(message))
        {
            await HandlePushNotificationAsync(@event).ConfigureAwait(false);
            return;
        }

        if (!@event.TryGetProperty("event_type", out var eventType) || eventType.GetString() != _options.CommandEventType
            || !@event.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        // Commands must name this device explicitly, so one event never locks every computer at once.
        var deviceId = data.TryGetProperty("device_id", out var device) ? device.GetString() : null;
        var action = data.TryGetProperty("action", out var actionProperty) ? actionProperty.GetString() : null;
        if (deviceId != _deviceId
            || action is null
            || !TryGetExposed(action, out var entity)
            || !entity.Kind.AcceptsCommands()
            || !CommandValue.TryNormalize(entity, ReadText(data, entity.Kind == EntityKind.Notify ? "message" : "value"), out var value))
        {
            LogIgnoredCommand(_logger, deviceId, action);
            return;
        }

        Dictionary<string, object?>? parameters = null;
        if (entity.Kind == EntityKind.Notify)
        {
            NotificationContent.TryRead(data, out _, out parameters, _baseUrl);
        }

        var command = new ActionCommand { ActionId = entity.Id, Value = value, Origin = Name };
        try
        {
            await _bus.PublishAsync(parameters is null ? command : command with { Parameters = parameters }, _stoppingToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Engine is stopping.
        }
    }

    /// <summary>A text or number property of the event data, as text; YAML makes <c>value: 40</c> a number.</summary>
    private static string? ReadText(JsonElement data, string property) =>
        !data.TryGetProperty(property, out var element) ? null
        : element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => BinaryState.On,
            JsonValueKind.False => BinaryState.Off,
            _ => null,
        };

    private async Task ForwardTelemetryAsync(IEventSubscription<TelemetryEvent> readings, CancellationToken cancellationToken)
    {
        try
        {
            var batch = new Dictionary<string, SensorReading>(StringComparer.Ordinal);
            await foreach (var first in readings.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                // Sending waits for Home Assistant, and sensors report in bursts. What gathered meanwhile goes out
                // together, the newest of each sensor only: one message instead of one per reading.
                batch.Clear();
                var reading = first;
                do
                {
                    if (!TryGetExposedSensor(reading.SensorId, out var entity))
                    {
                        continue;
                    }

                    // A replayed reading must not bring an unavailable sensor back to its stale value.
                    _lastReadings[entity.Id] = reading;
                    if (_state == EngineConnectionState.Connected && _registry.IsAvailable(entity.Id))
                    {
                        batch[entity.Id] = new SensorReading(entity, reading.State, reading.Attributes);
                    }
                }
                while (batch.Count < MaxBatch && readings.TryRead(out reading));

                if (batch.Count > 0)
                {
                    await TrySetStatesAsync([.. batch.Values], cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Fires what happens on the computer as a Home Assistant event of the type <see cref="HaWebSocketOptions.DeviceEventType"/>,
    /// with <c>device_id</c>, <c>name</c> and <c>value</c> as its data.
    /// </summary>
    private async Task ForwardEventsAsync(IEventSubscription<DeviceEvent> events, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var deviceEvent in events.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var isExposed = deviceEvent.IsWellFormed
                    && deviceEvent.IsFor(Name)
                    && (deviceEvent.Name != DeviceEvent.QuickAction
                        || (TryGetExposed(deviceEvent.Value, out var trigger) && trigger.Kind == EntityKind.Trigger));
                if (!isExposed || _connection is not { } connection || _state != EngineConnectionState.Connected)
                {
                    continue;
                }

                try
                {
                    // The integration fires the event itself, and triggers the entity of a quick action besides.
                    // Without it, firing an event over the socket is an administrator's to do; anyone else's
                    // goes through the mobile_app registration, where there is one.
                    if (!await TrySendIntegrationEventAsync(connection, deviceEvent, cancellationToken).ConfigureAwait(false)
                        && !await TryFireThroughWebhookAsync(deviceEvent, cancellationToken).ConfigureAwait(false))
                    {
                        await connection.SendAsync(
                                new
                                {
                                    id = connection.NextId(),
                                    type = "fire_event",
                                    event_type = _options.DeviceEventType,
                                    event_data = new { device_id = _deviceId, name = deviceEvent.Name, value = deviceEvent.Value },
                                },
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is WebSocketException or InvalidOperationException or ObjectDisposedException)
                {
                    // The connection is going down; the connection loop reports that.
                }

                if (deviceEvent.Name == DeviceEvent.NotificationAction)
                {
                    await FireNotificationActionAsync(deviceEvent.Value, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await events.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Deletes the state of sensors removed from the registry, e.g. a deleted custom sensor, and marks sensors
    /// unavailable while their source is away, e.g. the tray app's sensors after it exits.
    /// </summary>
    private async Task FollowRegistryAsync(IEventSubscription<EntityRegistryChange> changes, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var change in changes.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var entity = change.Entity;
                var connected = _state == EngineConnectionState.Connected;
                if (change is EntityRegistered or EntityUnregistered && IntegrationKind(entity.Kind) is not null)
                {
                    NotifyEntitiesChanged();
                }

                switch (change)
                {
                    case EntityUnregistered:
                        _lastReadings.TryRemove(entity.Id, out _);
                        if (connected && entity.Kind.ReportsState())
                        {
                            if (_options.SensorMode == HomeAssistantSensorMode.States)
                            {
                                await TrySendStateRequestAsync(HttpMethod.Delete, entity, content: null, cancellationToken).ConfigureAwait(false);
                            }

                            await MarkSensorEntitiesUnavailableAsync([entity], cancellationToken).ConfigureAwait(false);
                        }

                        break;
                    case EntityAvailabilityChanged { IsAvailable: false } when connected && TryGetExposedSensor(entity.Id, out _):
                        await TrySetStateAsync(entity, Unavailable, attributes: null, cancellationToken).ConfigureAwait(false);
                        break;
                    case EntityAvailabilityChanged { IsAvailable: true } when connected
                        && TryGetExposedSensor(entity.Id, out _)
                        && _lastReadings.TryGetValue(entity.Id, out var reading):
                        await TrySetStateAsync(entity, reading.State, reading.Attributes, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// States written before a sensor was disabled, or before sensors were switched off for this Home Assistant,
    /// would otherwise linger there.
    /// </summary>
    private async Task RemoveDisabledSensorsAsync(CancellationToken cancellationToken)
    {
        var sensors = _registry.Entities.Where(entity => entity.Kind.ReportsState()).ToList();
        var writesStates = _options.SensorMode == HomeAssistantSensorMode.States;

        // Once is enough when no states are written any more: what is deleted does not come back.
        if (writesStates || !_statesRemoved)
        {
            foreach (var entity in sensors.Where(entity => !writesStates || !_filter.IsEnabled(entity)))
            {
                await TrySendStateRequestAsync(HttpMethod.Delete, entity, content: null, cancellationToken).ConfigureAwait(false);
            }

            _statesRemoved = !writesStates;
        }

        // The entities of the mobile_app device cannot be deleted from here; what is no longer sent is shown as
        // unavailable instead of keeping its last value.
        var writesEntities = _options.SensorMode == HomeAssistantSensorMode.Entities;
        await MarkSensorEntitiesUnavailableAsync(
                [.. sensors.Where(entity => !writesEntities || !_filter.IsEnabled(entity))], cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task PublishCachedReadingsAsync(CancellationToken cancellationToken)
    {
        var cached = new List<SensorReading>();
        foreach (var reading in _lastReadings.Values)
        {
            if (TryGetExposedSensor(reading.SensorId, out var entity))
            {
                var isAvailable = _registry.IsAvailable(entity.Id);
                cached.Add(new SensorReading(entity, isAvailable ? reading.State : Unavailable, isAvailable ? reading.Attributes : null));
            }
        }

        foreach (var chunk in cached.Chunk(MaxBatch))
        {
            await TrySetStatesAsync(chunk, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task MarkSensorsUnavailableAsync(CancellationToken cancellationToken)
    {
        var sensors = new List<SensorReading>();
        foreach (var sensorId in _lastReadings.Keys)
        {
            if (TryGetExposedSensor(sensorId, out var entity))
            {
                sensors.Add(new SensorReading(entity, Unavailable, Attributes: null));
            }
        }

        foreach (var chunk in sensors.Chunk(MaxBatch))
        {
            await TrySetStatesAsync(chunk, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reports what several sensors say, in one message where the way they are sent allows it: as entities of
    /// the mobile_app device, and through the HADA integration. States are written one request each.
    /// </summary>
    private async Task TrySetStatesAsync(IReadOnlyList<SensorReading> readings, CancellationToken cancellationToken)
    {
        switch (_options.SensorMode)
        {
            case HomeAssistantSensorMode.Entities:
                await SetSensorEntitiesAsync(readings, cancellationToken).ConfigureAwait(false);
                break;
            case HomeAssistantSensorMode.Integration:
                await SetIntegrationStatesAsync(readings).ConfigureAwait(false);
                break;
            case HomeAssistantSensorMode.States:
                foreach (var reading in readings)
                {
                    await TrySetStateAsync(reading.Entity, reading.State, reading.Attributes, cancellationToken).ConfigureAwait(false);
                }

                break;
        }
    }

    private Task TrySetStateAsync(
        EntityDescriptor entity, string state, IReadOnlyDictionary<string, object?>? attributes, CancellationToken cancellationToken)
    {
        switch (_options.SensorMode)
        {
            case HomeAssistantSensorMode.Entities:
                return SetSensorEntitiesAsync([new SensorReading(entity, state, attributes)], cancellationToken);
            case HomeAssistantSensorMode.Integration:
                return SetIntegrationStatesAsync([new SensorReading(entity, state, attributes)]);
            case HomeAssistantSensorMode.Off:
                return Task.CompletedTask;
        }

        var allAttributes = attributes is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(attributes);
        allAttributes["friendly_name"] = $"{_deviceName} {entity.Name}";
        AddIfSet(allAttributes, "icon", entity.Icon);
        AddIfSet(allAttributes, "device_class", entity.DeviceClass);
        AddIfSet(allAttributes, "unit_of_measurement", entity.UnitOfMeasurement);
        AddIfSet(allAttributes, "state_class", entity.StateClass);

        var content = JsonContent.Create(new
        {
            state = state.Length > MaxStateLength ? state[..MaxStateLength] : state,
            attributes = allAttributes,
        });

        return TrySendStateRequestAsync(HttpMethod.Post, entity, content, cancellationToken);
    }

    private async Task TrySendStateRequestAsync(
        HttpMethod method, EntityDescriptor entity, HttpContent? content, CancellationToken cancellationToken)
    {
        // A state written through the REST API is not backed by an entity Home Assistant could send commands to, so
        // a switch is shown as what it reports, a binary sensor, and a number as a sensor.
        var domain = entity.Kind.IsBinary() ? "binary_sensor" : "sensor";
        using var request = new HttpRequestMessage(method, new Uri(_baseUrl!, $"api/states/{domain}.{_deviceId}_{entity.Id}"))
        {
            Content = content,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            // Deleting a state that does not exist is fine.
            if (method == HttpMethod.Delete && response.StatusCode == HttpStatusCode.NotFound)
            {
                return;
            }

            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            LogStateRequestFailed(_logger, ex, method.Method, entity.Id);
        }
    }

    private bool TryGetExposed(string entityId, [MaybeNullWhen(false)] out EntityDescriptor entity) =>
        _registry.TryGet(entityId, out entity) && _filter.IsEnabled(entity);

    private bool TryGetExposedSensor(string entityId, [MaybeNullWhen(false)] out EntityDescriptor entity) =>
        TryGetExposed(entityId, out entity) && ReportsHere(entity.Kind);

    /// <summary>
    /// Whether what entities of this kind report is sent through this connection. A media player's is where the
    /// HADA integration is there to make an entity of it; states and the mobile_app device have no such thing.
    /// </summary>
    private bool ReportsHere(EntityKind kind) => kind.ReportsState() || (kind == EntityKind.MediaPlayer && UsesIntegration);

    private static async Task<JsonDocument> ReceiveRequiredAsync(HaConnection connection, CancellationToken cancellationToken) =>
        await connection.ReceiveAsync(cancellationToken).ConfigureAwait(false)
        ?? throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely, "Home Assistant closed the connection during the handshake.");

    private static void ExpectType(JsonDocument message, string expected)
    {
        var actual = HaConnection.TypeOf(message);
        if (actual != expected)
        {
            throw new InvalidDataException($"Expected a '{expected}' message from Home Assistant but got '{actual}'.");
        }
    }

    private static bool TryParseBaseUrl(string? configured, [NotNullWhen(true)] out Uri? baseUrl)
    {
        var value = NullIfEmpty(configured)?.Trim();
        if (value is not null
            && Uri.TryCreate(value.EndsWith('/') ? value : value + "/", UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
        {
            baseUrl = parsed;
            return true;
        }

        baseUrl = null;
        return false;
    }

    private static Uri WebSocketUri(Uri baseUrl) =>
        new UriBuilder(new Uri(baseUrl, "api/websocket"))
        {
            Scheme = baseUrl.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
        }.Uri;

    /// <summary>Home Assistant object ids allow lowercase letters, digits and underscores.</summary>
    private static string ToObjectId(string value) =>
        string.Create(value.Length, value, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var c = char.ToLowerInvariant(source[i]);
                span[i] = char.IsAsciiLetterOrDigit(c) ? c : '_';
            }
        });

    private static void AddIfSet(Dictionary<string, object?> attributes, string key, string? value)
    {
        if (value is not null)
        {
            attributes[key] = value;
        }
    }

    private static string Describe(Exception exception) =>
        exception.InnerException is null ? exception.Message : $"{exception.Message} ({exception.GetBaseException().Message})";

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    [LoggerMessage(Level = LogLevel.Warning, Message = "WebSocket engine is idle: HomeAssistant:BaseUrl or HomeAssistant:AccessToken is not configured.")]
    private static partial void LogNotConfigured(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected to Home Assistant {HaVersion} at {BaseUrl} as device '{DeviceId}'.")]
    private static partial void LogConnected(ILogger logger, Uri baseUrl, string haVersion, string deviceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Home Assistant did not let this token subscribe to '{EventType}' events, as it is not an administrator's; commands sent that way will not arrive.")]
    private static partial void LogNoCommands(ILogger logger, string eventType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Disconnected from Home Assistant; reconnecting in {RetryDelay}.")]
    private static partial void LogDisconnected(ILogger logger, TimeSpan retryDelay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Home Assistant connection failed; retrying in {RetryDelay}.")]
    private static partial void LogConnectionFailed(ILogger logger, Exception exception, TimeSpan retryDelay);

    [LoggerMessage(Level = LogLevel.Error, Message = "Home Assistant rejected the access token ({Reason}). The WebSocket engine will not retry until restarted.")]
    private static partial void LogAuthenticationFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No message from Home Assistant within two heartbeat intervals; reconnecting.")]
    private static partial void LogHeartbeatTimedOut(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ignored command event for device '{DeviceId}', action '{Action}'.")]
    private static partial void LogIgnoredCommand(ILogger logger, string? deviceId, string? action);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Method} request for the Home Assistant state of '{EntityId}' failed.")]
    private static partial void LogStateRequestFailed(ILogger logger, Exception exception, string method, string entityId);
}

internal sealed class HaAuthenticationException(string? reason) : Exception(reason ?? "Invalid access token");

/// <summary>What a sensor says at one moment, on its way to Home Assistant.</summary>
internal sealed record SensorReading(EntityDescriptor Entity, string State, IReadOnlyDictionary<string, object?>? Attributes);

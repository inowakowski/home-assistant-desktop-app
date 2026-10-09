using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;

namespace HADA.Tests.HomeAssistant;

/// <param name="Body">The JSON body of a POST; <see langword="default"/> for a DELETE.</param>
public sealed record StateWrite(string Method, string EntityId, string? Authorization, JsonElement Body);

/// <summary>A request to a webhook of the mobile_app integration: which one, and its JSON body.</summary>
public sealed record WebhookCall(string WebhookId, string? Authorization, JsonElement Body);

/// <summary>Whether the fake Home Assistant has the HADA integration.</summary>
public enum FakeIntegration
{
    /// <summary>Not installed: its commands are unknown.</summary>
    Missing,

    /// <summary>Installed, but not added under Devices &amp; services.</summary>
    NotSetUp,

    SetUp,
}

/// <summary>What the HADA integration was last told about an entity.</summary>
/// <param name="Descriptor">As in the list of entities: kind, name and so on.</param>
/// <param name="State">The state as JSON text, e.g. <c>12.5</c>, <c>true</c> or <c>"text"</c>; <see langword="null"/> before any.</param>
public sealed record IntegrationEntity(JsonElement Descriptor, string? State, JsonElement? Attributes, bool Available);

/// <summary>Just enough of Home Assistant's WebSocket and REST APIs to exercise the WebSocket engine.</summary>
internal sealed class FakeHomeAssistant : IAsyncDisposable
{
    public const string ValidToken = "valid-token";
    public const string Version = "2026.9.0";

    private readonly HttpListener _listener = new();
    private readonly Task _acceptLoop;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly List<JsonElement> _socketMessages = [];
    private readonly List<StateWrite> _stateWrites = [];
    private readonly List<JsonElement> _registrations = [];
    private readonly List<WebhookCall> _webhookCalls = [];
    private readonly HashSet<string> _webhookIds = [];
    private readonly HashSet<string> _forgottenWebhookIds = [];
    private readonly Dictionary<string, JsonElement> _sensors = new(StringComparer.Ordinal);
    private int _pushSubscriptionId;
    private readonly Dictionary<string, IntegrationEntity> _integrationEntities = new(StringComparer.Ordinal);
    private bool _integrationConnected;
    private int _integrationSubscriptionId;
    private volatile WebSocket? _socket;
    private int _connectionAttempts;

    public FakeHomeAssistant()
    {
        BaseUrl = new Uri($"http://localhost:{GetFreePort()}/");
        _listener.Prefixes.Add(BaseUrl.ToString());
        _listener.Start();
        _acceptLoop = AcceptAsync();
    }

    public Uri BaseUrl { get; }

    public int ConnectionAttempts => Volatile.Read(ref _connectionAttempts);

    public WebSocketCloseStatus? ClientCloseStatus { get; private set; }

    /// <summary>Whether the token is an administrator's, who alone may subscribe to custom events.</summary>
    public bool IsAdministrator { get; set; } = true;

    /// <summary>Whether the HADA integration is there to connect to. May be changed while a client is connected.</summary>
    public FakeIntegration Integration { get; set; } = FakeIntegration.Missing;

    /// <summary>The device of the last <c>hada/connect</c>; <see langword="null"/> before any.</summary>
    public JsonElement? IntegrationDevice { get; private set; }

    /// <summary>Whether a client is connected to the HADA integration as a device right now.</summary>
    public bool IsIntegrationConnected
    {
        get
        {
            lock (_integrationEntities)
            {
                return _integrationConnected;
            }
        }
    }

    /// <summary>The ids of the entities the integration has for the device, sorted.</summary>
    public string[] IntegrationEntityIds
    {
        get
        {
            lock (_integrationEntities)
            {
                return [.. _integrationEntities.Keys.Order(StringComparer.Ordinal)];
            }
        }
    }

    public IntegrationEntity? IntegrationEntity(string id)
    {
        lock (_integrationEntities)
        {
            return _integrationEntities.GetValueOrDefault(id);
        }
    }

    /// <summary>Sends a command for the computer, as the integration does when an entity is used: <c>command_id</c>, <c>command</c>, <c>entity</c>, <c>value</c>.</summary>
    public Task SendIntegrationCommandAsync(object command) =>
        SendAsync(
            _socket ?? throw new InvalidOperationException("No authenticated client is connected."),
            new { id = Volatile.Read(ref _integrationSubscriptionId), type = "event", @event = command });

    /// <summary>As when the integration is reloaded in Home Assistant: it no longer has the client as the device.</summary>
    public void ReloadIntegration()
    {
        lock (_integrationEntities)
        {
            _integrationConnected = false;
        }
    }

    /// <summary>Whether the mobile_app integration is there to register with.</summary>
    public bool HasMobileApp { get; set; } = true;

    /// <summary>Whether a client has the channel open that notifications are sent on.</summary>
    public bool HasPushChannel => Volatile.Read(ref _pushSubscriptionId) != 0;

    /// <summary>The bodies of the registrations with mobile_app, in the order they were made.</summary>
    public JsonElement[] Registrations
    {
        get
        {
            lock (_registrations)
            {
                return [.. _registrations];
            }
        }
    }

    public WebhookCall[] WebhookCalls
    {
        get
        {
            lock (_webhookCalls)
            {
                return [.. _webhookCalls];
            }
        }
    }

    /// <summary>The webhook id handed out by the registration of that number, counted from one.</summary>
    public static string WebhookIdOf(int registration) => $"webhook-{registration}";

    /// <summary>As when the device is deleted in Home Assistant: its webhook answers 410 and its channel cannot be opened.</summary>
    public void DeleteDevice(string webhookId)
    {
        lock (_webhookIds)
        {
            _webhookIds.Remove(webhookId);
        }
    }

    /// <summary>As when Home Assistant never heard of the webhook, e.g. after it was set up anew: answered, with nothing.</summary>
    public void ForgetDevice(string webhookId)
    {
        lock (_webhookIds)
        {
            _webhookIds.Remove(webhookId);
            _forgottenWebhookIds.Add(webhookId);
        }
    }

    /// <summary>A device class no entity has; registering a sensor with it is refused.</summary>
    public const string UnknownDeviceClass = "no_such_class";

    /// <summary>What was last sent for the sensor entity of that unique id, when registering or updating it; <see langword="null"/> when there is none.</summary>
    public JsonElement? SensorEntity(string uniqueId)
    {
        lock (_sensors)
        {
            return _sensors.TryGetValue(uniqueId, out var sensor) ? sensor : null;
        }
    }

    /// <summary>As when the entity is deleted in Home Assistant.</summary>
    public void DeleteSensorEntity(string uniqueId)
    {
        lock (_sensors)
        {
            _sensors.Remove(uniqueId);
        }
    }

    /// <summary>Sends what a <c>notify.mobile_app_*</c> action sends: <c>message</c>, <c>title</c>, <c>data</c>, <c>hass_confirm_id</c>.</summary>
    public Task SendNotificationAsync(object notification) =>
        SendAsync(
            _socket ?? throw new InvalidOperationException("No authenticated client is connected."),
            new { id = Volatile.Read(ref _pushSubscriptionId), type = "event", @event = notification });

    public JsonElement[] SocketMessages
    {
        get
        {
            lock (_socketMessages)
            {
                return [.. _socketMessages];
            }
        }
    }

    public StateWrite[] StateWrites
    {
        get
        {
            lock (_stateWrites)
            {
                return [.. _stateWrites];
            }
        }
    }

    public Task SendCommandEventAsync(object data) =>
        SendAsync(
            _socket ?? throw new InvalidOperationException("No authenticated client is connected."),
            new { id = 1, type = "event", @event = new { event_type = "hada_command", data } });

    public async ValueTask DisposeAsync()
    {
        _socket?.Abort();
        _listener.Close();
        await _acceptLoop;
        _sendLock.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var path = request.Url!.AbsolutePath;
        const string statesPrefix = "/api/states/";
        const string webhookPrefix = "/api/webhook/";

        if (path == "/api/websocket" && request.IsWebSocketRequest)
        {
            Interlocked.Increment(ref _connectionAttempts);
            var webSocketContext = await context.AcceptWebSocketAsync(subProtocol: null);
            await RunSessionAsync(webSocketContext.WebSocket);
        }
        else if (request.HttpMethod is "POST" or "DELETE" && path.StartsWith(statesPrefix, StringComparison.Ordinal))
        {
            JsonElement body = default;
            if (request.HttpMethod == "POST")
            {
                using var document = await JsonDocument.ParseAsync(request.InputStream);
                body = document.RootElement.Clone();
            }

            lock (_stateWrites)
            {
                _stateWrites.Add(new StateWrite(request.HttpMethod, path[statesPrefix.Length..], request.Headers["Authorization"], body));
            }

            context.Response.StatusCode = request.HttpMethod == "POST" ? 201 : 200;
            context.Response.Close();
        }
        else if (request.HttpMethod == "POST" && path == "/api/mobile_app/registrations" && HasMobileApp)
        {
            using var document = await JsonDocument.ParseAsync(request.InputStream);
            string webhookId;
            lock (_registrations)
            {
                _registrations.Add(document.RootElement.Clone());
                webhookId = WebhookIdOf(_registrations.Count);
            }

            lock (_webhookIds)
            {
                _webhookIds.Add(webhookId);
            }

            var authorized = request.Headers["Authorization"] == $"Bearer {ValidToken}";
            context.Response.StatusCode = authorized ? 201 : 401;
            if (authorized)
            {
                await context.Response.OutputStream.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(new { webhook_id = webhookId }));
            }

            context.Response.Close();
        }
        else if (request.HttpMethod == "POST" && path.StartsWith(webhookPrefix, StringComparison.Ordinal))
        {
            var webhookId = path[webhookPrefix.Length..];
            using var document = await JsonDocument.ParseAsync(request.InputStream);
            lock (_webhookCalls)
            {
                _webhookCalls.Add(new WebhookCall(webhookId, request.Headers["Authorization"], document.RootElement.Clone()));
            }

            bool known;
            bool forgotten;
            lock (_webhookIds)
            {
                known = _webhookIds.Contains(webhookId);
                forgotten = _forgottenWebhookIds.Contains(webhookId);
            }

            // A webhook nobody registered is answered like any other, only with nothing; one of a deleted device is gone.
            object? answer = null;
            context.Response.StatusCode = known || forgotten ? 200 : 410;
            if (known)
            {
                var body = document.RootElement;
                switch (body.GetProperty("type").GetString())
                {
                    case "update_registration":
                        answer = new { device_name = body.GetProperty("data").GetProperty("device_name").GetString() };
                        break;
                    case "register_sensor":
                        var sensor = body.GetProperty("data");
                        if (sensor.TryGetProperty("device_class", out var deviceClass) && deviceClass.GetString() == UnknownDeviceClass)
                        {
                            context.Response.StatusCode = 400;
                            break;
                        }

                        lock (_sensors)
                        {
                            _sensors[sensor.GetProperty("unique_id").GetString()!] = sensor.Clone();
                        }

                        context.Response.StatusCode = 201;
                        answer = new { success = true };
                        break;
                    case "update_sensor_states":
                        var results = new Dictionary<string, object>();
                        lock (_sensors)
                        {
                            foreach (var update in body.GetProperty("data").EnumerateArray())
                            {
                                var id = update.GetProperty("unique_id").GetString()!;
                                if (_sensors.ContainsKey(id))
                                {
                                    _sensors[id] = update.Clone();
                                    results[id] = new { success = true };
                                }
                                else
                                {
                                    results[id] = new { success = false, error = new { code = "not_registered", message = $"{id} is not registered" } };
                                }
                            }
                        }

                        answer = results;
                        break;
                    default:
                        answer = new { };
                        break;
                }
            }

            if (answer is not null)
            {
                await context.Response.OutputStream.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(answer));
            }

            context.Response.Close();
        }
        else
        {
            context.Response.StatusCode = 404;
            context.Response.Close();
        }
    }

    private async Task RunSessionAsync(WebSocket socket)
    {
        try
        {
            await SendAsync(socket, new { type = "auth_required", ha_version = Version });
            if (await ReceiveAsync(socket) is not { } auth)
            {
                return;
            }

            Record(auth);
            if (auth.GetProperty("access_token").GetString() != ValidToken)
            {
                await SendAsync(socket, new { type = "auth_invalid", message = "Invalid access token or password" });
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                return;
            }

            await SendAsync(socket, new { type = "auth_ok", ha_version = Version });
            _socket = socket;

            while (await ReceiveAsync(socket) is { } message)
            {
                Record(message);
                var id = message.TryGetProperty("id", out var idProperty) ? idProperty.GetInt32() : 0;
                switch (message.GetProperty("type").GetString())
                {
                    case "subscribe_events" when !IsAdministrator:
                        await SendAsync(socket, new { id, type = "result", success = false, error = new { code = "unauthorized", message = "Unauthorized" } });
                        break;
                    case "subscribe_events":
                        await SendAsync(socket, new { id, type = "result", success = true, result = (object?)null });
                        break;
                    case "mobile_app/push_notification_channel":
                        bool known;
                        lock (_webhookIds)
                        {
                            known = _webhookIds.Contains(message.GetProperty("webhook_id").GetString()!);
                        }

                        if (known)
                        {
                            Volatile.Write(ref _pushSubscriptionId, id);
                            await SendAsync(socket, new { id, type = "result", success = true, result = (object?)null });
                        }
                        else
                        {
                            await SendAsync(socket, new { id, type = "result", success = false, error = new { code = "not_found", message = "Webhook ID not found" } });
                        }

                        break;
                    case "hada/connect" or "hada/entities" or "hada/update" when Integration == FakeIntegration.Missing:
                        await SendAsync(socket, new { id, type = "result", success = false, error = new { code = "unknown_command", message = "Unknown command." } });
                        break;
                    case "hada/connect" when Integration == FakeIntegration.NotSetUp:
                        await SendAsync(socket, new { id, type = "result", success = false, error = new { code = "not_found", message = "The HADA integration is not set up" } });
                        break;
                    case "hada/command_result" or "hada/event" when IsIntegrationConnected:
                        await SendAsync(socket, new { id, type = "result", success = true, result = (object?)null });
                        break;
                    case "hada/connect":
                        Volatile.Write(ref _integrationSubscriptionId, id);
                        IntegrationDevice = message.GetProperty("device").Clone();
                        SetIntegrationEntities(message.GetProperty("entities"), connect: true);
                        await SendAsync(socket, new { id, type = "result", success = true, result = new { protocol = 1, integration_version = "0.1.0", ignored = Array.Empty<string>() } });
                        break;
                    case "hada/entities" or "hada/update" when !IsIntegrationConnected:
                        await SendAsync(socket, new { id, type = "result", success = false, error = new { code = "not_found", message = "This connection is not connected as that device" } });
                        break;
                    case "hada/entities":
                        SetIntegrationEntities(message.GetProperty("entities"), connect: false);
                        await SendAsync(socket, new { id, type = "result", success = true, result = new { ignored = Array.Empty<string>() } });
                        break;
                    case "hada/update":
                        lock (_integrationEntities)
                        {
                            foreach (var update in message.GetProperty("states").EnumerateArray())
                            {
                                var entityId = update.GetProperty("id").GetString()!;
                                if (_integrationEntities.TryGetValue(entityId, out var entity))
                                {
                                    _integrationEntities[entityId] = Apply(entity, update);
                                }
                            }
                        }

                        await SendAsync(socket, new { id, type = "result", success = true, result = (object?)null });
                        break;
                    case "auth/sign_path":
                        var path = message.GetProperty("path").GetString()!;
                        await SendAsync(socket, new { id, type = "result", success = true, result = new { path = path + (path.Contains('?') ? "&" : "?") + "authSig=signed" } });
                        break;
                    case "mobile_app/push_notification_confirm":
                        await SendAsync(socket, new { id, type = "result", success = true, result = (object?)null });
                        break;
                    case "ping":
                        await SendAsync(socket, new { id, type = "pong" });
                        break;
                }
            }

            ClientCloseStatus = socket.CloseStatus;
            if (socket.State == WebSocketState.CloseReceived)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or HttpListenerException or ObjectDisposedException)
        {
            // Client aborted or the fake is being disposed.
        }
        finally
        {
            if (_socket == socket)
            {
                _socket = null;
                Volatile.Write(ref _pushSubscriptionId, 0);
                ReloadIntegration();
            }
        }
    }

    /// <summary>The list is complete: what is not in it is gone, as the protocol has it.</summary>
    private void SetIntegrationEntities(JsonElement entities, bool connect)
    {
        lock (_integrationEntities)
        {
            _integrationEntities.Clear();
            foreach (var entity in entities.EnumerateArray())
            {
                var described = new IntegrationEntity(entity.Clone(), State: null, Attributes: null, Available: true);
                _integrationEntities[entity.GetProperty("id").GetString()!] = Apply(described, entity);
            }

            _integrationConnected |= connect;
        }
    }

    /// <summary>What is named changes; the rest stays.</summary>
    private static IntegrationEntity Apply(IntegrationEntity entity, JsonElement update) => entity with
    {
        State = update.TryGetProperty("state", out var state) ? state.GetRawText() : entity.State,
        Attributes = update.TryGetProperty("attributes", out var attributes) ? attributes.Clone() : entity.Attributes,
        Available = update.TryGetProperty("available", out var available) ? available.GetBoolean() : entity.Available,
    };

    private void Record(JsonElement message)
    {
        lock (_socketMessages)
        {
            _socketMessages.Add(message);
        }
    }

    private async Task SendAsync(WebSocket socket, object message)
    {
        await _sendLock.WaitAsync();
        try
        {
            await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static async Task<JsonElement?> ReceiveAsync(WebSocket socket)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var result = await socket.ReceiveAsync(chunk, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            buffer.Write(chunk, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

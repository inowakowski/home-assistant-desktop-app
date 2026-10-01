using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;

namespace HADA.Tests.HomeAssistant;

/// <param name="Body">The JSON body of a POST; <see langword="default"/> for a DELETE.</param>
public sealed record StateWrite(string Method, string EntityId, string? Authorization, JsonElement Body);

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
                    case "subscribe_events":
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
            }
        }
    }

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

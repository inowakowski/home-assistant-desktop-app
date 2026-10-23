using System.Buffers;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;

namespace HADA.Engine.WebSocket;

/// <summary>One authenticated-or-not WebSocket session with Home Assistant: JSON framing, ids and send serialization.</summary>
internal sealed class HaConnection(ClientWebSocket socket) : IDisposable
{
    private const int MaxMessageBytes = 1024 * 1024;

    private readonly SemaphoreSlim _sendLock = new(1, 1);

    // Only touched while the send lock is held.
    private int _lastId;
    private long _lastReceivedTimestamp = Stopwatch.GetTimestamp();

    public WebSocketState State => socket.State;

    public TimeSpan SinceLastReceived => Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastReceivedTimestamp));

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) => socket.ConnectAsync(uri, cancellationToken);

    /// <summary>Sends a message that has no id: the access token, the one message sent before any command.</summary>
    public async Task SendAsync<T>(T message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message);
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Sends a command with the next id, and returns the id. Home Assistant answers a command whose id is not larger
    /// than the one before it with <c>id_reuse</c>, and does nothing else with it; so the id is given out only once
    /// it is the command's turn to go out, and the commands of the loops that send at once go out in the order of
    /// their ids.
    /// </summary>
    /// <param name="command">Makes the command, given its id. Called once, before anything of it is sent.</param>
    public async Task<int> SendCommandAsync<T>(Func<int, T> command, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var id = ++_lastId;
            var payload = JsonSerializer.SerializeToUtf8Bytes(command(id));
            await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
            return id;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Returns <see langword="null"/> once the server has closed the connection.
    /// Cancelling a receive aborts the socket, so only cancel when the session is being torn down anyway.
    /// </summary>
    public async Task<JsonDocument?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>(4096);
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.GetMemory(4096), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            buffer.Advance(result.Count);
            if (buffer.WrittenCount > MaxMessageBytes)
            {
                throw new InvalidDataException($"Home Assistant sent a message larger than {MaxMessageBytes} bytes.");
            }

            if (result.EndOfMessage)
            {
                break;
            }
        }

        Interlocked.Exchange(ref _lastReceivedTimestamp, Stopwatch.GetTimestamp());
        return JsonDocument.Parse(buffer.WrittenMemory);
    }

    /// <summary>Starts the close handshake; the pending receive then completes once Home Assistant answers.</summary>
    public async Task CloseOutputAsync(CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "HADA shutting down", cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public void Abort() => socket.Abort();

    public void Dispose()
    {
        socket.Dispose();
        _sendLock.Dispose();
    }

    public static string? TypeOf(JsonDocument message) =>
        message.RootElement.TryGetProperty("type", out var type) ? type.GetString() : null;
}

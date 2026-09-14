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
    private int _lastId;
    private long _lastReceivedTimestamp = Stopwatch.GetTimestamp();

    public WebSocketState State => socket.State;

    public TimeSpan SinceLastReceived => Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastReceivedTimestamp));

    public int NextId() => Interlocked.Increment(ref _lastId);

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) => socket.ConnectAsync(uri, cancellationToken);

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

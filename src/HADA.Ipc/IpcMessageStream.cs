using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HADA.Ipc;

/// <summary>
/// Length-prefixed JSON framing: a 4-byte little-endian payload length followed by that many bytes of UTF-8 JSON.
/// Writes are serialized, so several producers may share one stream; reads must come from a single reader.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The semaphore's wait handle is never used, so it holds nothing to release; the stream belongs to the caller.")]
public sealed class IpcMessageStream(Stream stream)
{
    /// <summary>Version 2 added client roles and the control requests; version 3 binary sensors and custom sensors.</summary>
    public const int ProtocolVersion = 3;

    /// <summary>Caps how much memory a misbehaving peer can make the other side allocate.</summary>
    public const int MaxMessageBytes = 256 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        RespectNullableAnnotations = true,
    };

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public async Task WriteAsync(IpcMessage message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (payload.Length > MaxMessageBytes)
        {
            throw new InvalidDataException($"IPC message of {payload.Length} bytes exceeds the {MaxMessageBytes}-byte limit.");
        }

        // One write per frame so a header is never separated from its payload.
        var frame = new byte[sizeof(int) + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, sizeof(int));

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Returns <see langword="null"/> when the peer closed the connection between messages.</summary>
    /// <exception cref="InvalidDataException">The frame or its JSON is malformed.</exception>
    /// <exception cref="EndOfStreamException">The peer closed the connection mid-message.</exception>
    public async Task<IpcMessage?> ReadAsync(CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        var read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken)
            .ConfigureAwait(false);
        if (read == 0)
        {
            return null;
        }

        if (read < header.Length)
        {
            throw new EndOfStreamException("IPC connection closed in the middle of a message header.");
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxMessageBytes)
        {
            throw new InvalidDataException($"Invalid IPC message length {length}.");
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);

        try
        {
            return JsonSerializer.Deserialize<IpcMessage>(payload, JsonOptions)
                ?? throw new InvalidDataException("Empty IPC message.");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new InvalidDataException("Malformed IPC message.", ex);
        }
    }
}

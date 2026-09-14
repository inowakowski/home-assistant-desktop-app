using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using HADA.Core.Entities;
using HADA.Core.Models;
using HADA.Ipc;

namespace HADA.Tests.Ipc;

public class IpcMessageStreamTests
{
    [Fact]
    public async Task Messages_round_trip_through_the_frame_format()
    {
        var entity = new EntityDescriptor { Id = "audio_volume", Name = "Volume", Kind = EntityKind.Sensor, UnitOfMeasurement = "%" };
        var buffer = new MemoryStream();
        var writer = new IpcMessageStream(buffer);
        await writer.WriteAsync(new HelloMessage(IpcMessageStream.ProtocolVersion, "tray"), CancellationToken.None);
        await writer.WriteAsync(new EntityRegistrationMessage(entity), CancellationToken.None);
        await writer.WriteAsync(
            new TelemetryMessage(new TelemetryEvent
            {
                SensorId = "audio_volume",
                State = "40",
                Attributes = new Dictionary<string, object?> { ["muted"] = true },
            }),
            CancellationToken.None);

        buffer.Position = 0;
        var reader = new IpcMessageStream(buffer);

        Assert.Equal(new HelloMessage(IpcMessageStream.ProtocolVersion, "tray"), await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(new EntityRegistrationMessage(entity), await reader.ReadAsync(CancellationToken.None));
        var telemetry = Assert.IsType<TelemetryMessage>(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal("audio_volume", telemetry.Reading.SensorId);
        Assert.Equal("40", telemetry.Reading.State);
        Assert.True(((JsonElement)telemetry.Reading.Attributes["muted"]!).GetBoolean());
        Assert.Null(await reader.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Oversized_frames_are_rejected_before_allocating()
    {
        var reader = new IpcMessageStream(new MemoryStream(Header(IpcMessageStream.MaxMessageBytes + 1)));

        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Truncated_frames_are_rejected()
    {
        var reader = new IpcMessageStream(new MemoryStream([.. Header(10), 1, 2, 3]));

        await Assert.ThrowsAsync<EndOfStreamException>(() => reader.ReadAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"$type":"unknown"}""")]
    [InlineData("""{"$type":"telemetry","reading":{"sensorId":"x","state":null}}""")]
    public async Task Malformed_messages_are_rejected(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var reader = new IpcMessageStream(new MemoryStream([.. Header(payload.Length), .. payload]));

        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadAsync(CancellationToken.None));
    }

    private static byte[] Header(int length)
    {
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        return header;
    }
}

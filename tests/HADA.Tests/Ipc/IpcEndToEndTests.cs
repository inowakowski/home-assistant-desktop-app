using System.IO.Pipes;
using System.Text.Json;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Messaging;
using HADA.Core.Models;
using HADA.Ipc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HADA.Tests.Ipc;

/// <summary>Service-side server and tray-side client talking over a real named pipe with a unique name.</summary>
public sealed class IpcEndToEndTests : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly IpcOptions _options = new()
    {
        PipeName = "HADA.Tests." + Guid.NewGuid().ToString("N"),
        ClientName = "test-tray",
        MinReconnectDelay = TimeSpan.FromMilliseconds(50),
    };

    private readonly ChannelEventBus _trayBus = new();
    private readonly EntityRegistry _trayRegistry;
    private readonly List<BackgroundService> _started = [];
    private readonly List<ChannelEventBus> _buses = [];

    public IpcEndToEndTests() => _trayRegistry = new EntityRegistry(_trayBus);

    public async ValueTask DisposeAsync()
    {
        foreach (var service in _started)
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }

        foreach (var bus in _buses)
        {
            await bus.DisposeAsync();
        }

        await _trayBus.DisposeAsync();
    }

    [Fact]
    public async Task Tray_entities_and_readings_reach_the_service()
    {
        var (serviceBus, serviceRegistry) = CreateServiceSide();
        await using var serviceReadings = serviceBus.Subscribe<TelemetryEvent>();
        await StartServerAsync(serviceBus, serviceRegistry);
        var client = await StartClientAsync();

        await _trayRegistry.RegisterAsync(Sensor("audio_volume"));
        await WaitUntilAsync(() => client.IsConnected);
        await _trayBus.PublishAsync(new TelemetryEvent
        {
            SensorId = "audio_volume",
            State = "40",
            Attributes = new Dictionary<string, object?> { ["muted"] = true },
        });

        var reading = await ReadAsync(serviceReadings);
        Assert.Equal("audio_volume", reading.SensorId);
        Assert.Equal("40", reading.State);
        Assert.Equal("test-tray", reading.Source);
        Assert.True(((JsonElement)reading.Attributes["muted"]!).GetBoolean());
        Assert.True(serviceRegistry.TryGet("audio_volume", out var entity));
        Assert.Equal(Sensor("audio_volume"), entity);
    }

    [Fact]
    public async Task Entities_of_a_tray_that_went_away_become_unavailable_until_it_reconnects()
    {
        var (serviceBus, serviceRegistry) = CreateServiceSide();
        await StartServerAsync(serviceBus, serviceRegistry);
        var client = await StartClientAsync();
        await _trayRegistry.RegisterAsync(Sensor("user_active"));
        await WaitUntilAsync(() => serviceRegistry.TryGet("user_active", out _));
        Assert.True(serviceRegistry.IsAvailable("user_active"));

        // The tray exits, e.g. because the user signed out.
        await client.StopAsync(CancellationToken.None);
        await WaitUntilAsync(() => !serviceRegistry.IsAvailable("user_active"));

        await StartClientAsync();
        await WaitUntilAsync(() => serviceRegistry.IsAvailable("user_active"));
    }

    [Fact]
    public async Task Client_replays_entities_and_latest_reading_after_service_restart()
    {
        var (firstBus, firstRegistry) = CreateServiceSide();
        await using (var firstReadings = firstBus.Subscribe<TelemetryEvent>())
        {
            var firstServer = await StartServerAsync(firstBus, firstRegistry);
            await StartClientAsync();
            await _trayRegistry.RegisterAsync(Sensor("active_window"));
            await _trayBus.PublishAsync(new TelemetryEvent { SensorId = "active_window", State = "Editor" });
            Assert.Equal("Editor", (await ReadAsync(firstReadings)).State);

            await firstServer.StopAsync(CancellationToken.None);
        }

        var (secondBus, secondRegistry) = CreateServiceSide();
        await using var secondReadings = secondBus.Subscribe<TelemetryEvent>();
        await StartServerAsync(secondBus, secondRegistry);

        Assert.Equal("Editor", (await ReadAsync(secondReadings)).State);
        Assert.True(secondRegistry.TryGet("active_window", out _));
    }

    [Fact]
    public async Task Service_rejects_buttons_foreign_entities_and_unregistered_readings()
    {
        var (serviceBus, serviceRegistry) = CreateServiceSide();
        var serviceEntity = new EntityDescriptor { Id = "cpu_load", Name = "CPU load", Kind = EntityKind.Sensor };
        await serviceRegistry.RegisterAsync(serviceEntity);
        await using var serviceReadings = serviceBus.Subscribe<TelemetryEvent>();
        await StartServerAsync(serviceBus, serviceRegistry);

        await using var pipe = await ConnectRawClientAsync();
        var stream = new IpcMessageStream(pipe);
        await stream.WriteAsync(new HelloMessage(IpcMessageStream.ProtocolVersion, "rogue"), CancellationToken.None);
        await stream.WriteAsync(
            new EntityRegistrationMessage(new EntityDescriptor { Id = "rogue_button", Name = "Rogue", Kind = EntityKind.Button }),
            CancellationToken.None);
        await stream.WriteAsync(
            new EntityRegistrationMessage(serviceEntity with { Name = "Hijacked" }), CancellationToken.None);
        await stream.WriteAsync(new TelemetryMessage(new TelemetryEvent { SensorId = "cpu_load", State = "99" }), CancellationToken.None);
        await stream.WriteAsync(new TelemetryMessage(new TelemetryEvent { SensorId = "never_registered", State = "1" }), CancellationToken.None);
        await stream.WriteAsync(new EntityRegistrationMessage(Sensor("audio_volume")), CancellationToken.None);
        await stream.WriteAsync(new TelemetryMessage(new TelemetryEvent { SensorId = "audio_volume", State = "40" }), CancellationToken.None);

        var reading = await ReadAsync(serviceReadings);
        Assert.Equal("audio_volume", reading.SensorId);
        Assert.False(serviceReadings.TryRead(out _));
        Assert.False(serviceRegistry.TryGet("rogue_button", out _));
        Assert.True(serviceRegistry.TryGet("cpu_load", out var cpu));
        Assert.Equal("CPU load", cpu.Name);
    }

    [Fact]
    public async Task Service_drops_clients_speaking_another_protocol_version()
    {
        var (serviceBus, serviceRegistry) = CreateServiceSide();
        await StartServerAsync(serviceBus, serviceRegistry);

        await using var pipe = await ConnectRawClientAsync();
        var stream = new IpcMessageStream(pipe);
        await stream.WriteAsync(new HelloMessage(ProtocolVersion: 99, "future-tray"), CancellationToken.None);

        using var timeout = new CancellationTokenSource(Timeout);
        Assert.Null(await stream.ReadAsync(timeout.Token));
    }

    private static EntityDescriptor Sensor(string id) => new() { Id = id, Name = id, Kind = EntityKind.Sensor };

    private (ChannelEventBus Bus, EntityRegistry Registry) CreateServiceSide()
    {
        var bus = new ChannelEventBus();
        _buses.Add(bus);
        return (bus, new EntityRegistry(bus));
    }

    private async Task<IpcServer> StartServerAsync(IEventBus bus, IEntityRegistry registry)
    {
        var server = new IpcServer(bus, registry, Options.Create(_options), NullLogger<IpcServer>.Instance);
        _started.Add(server);
        await server.StartAsync(CancellationToken.None);
        return server;
    }

    private async Task<IpcClient> StartClientAsync()
    {
        var client = new IpcClient(_trayBus, _trayRegistry, Options.Create(_options), NullLogger<IpcClient>.Instance);
        _started.Add(client);
        await client.StartAsync(CancellationToken.None);
        return client;
    }

    private async Task<NamedPipeClientStream> ConnectRawClientAsync()
    {
        var pipe = new NamedPipeClientStream(".", _options.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(Timeout);
        await pipe.ConnectAsync(timeout.Token);
        return pipe;
    }

    private static async Task<T> ReadAsync<T>(IEventSubscription<T> subscription)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        await using var enumerator = subscription.ReadAllAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await enumerator.MoveNextAsync());
        return enumerator.Current;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }
}

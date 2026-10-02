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
    public async Task A_service_that_finds_the_pipe_taken_takes_it_over_once_it_is_free()
    {
        _options.PipeRetryDelay = TimeSpan.FromMilliseconds(100);

        // Another copy of the service got there first, as a development build left running would.
        var (firstBus, firstRegistry) = CreateServiceSide();
        var first = await StartServerAsync(firstBus, firstRegistry);

        var (secondBus, secondRegistry) = CreateServiceSide();
        await StartServerAsync(secondBus, secondRegistry);
        await _trayRegistry.RegisterAsync(Sensor("audio_volume"));
        await StartClientAsync();
        await WaitUntilAsync(() => firstRegistry.TryGet("audio_volume", out _));
        Assert.False(secondRegistry.TryGet("audio_volume", out _));

        await first.StopAsync(CancellationToken.None);

        // The second service must not have given up: the tray reconnects, and now reaches it.
        await WaitUntilAsync(() => secondRegistry.TryGet("audio_volume", out _));
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
    public async Task Commands_for_a_tray_entity_reach_the_tray_and_those_for_service_entities_do_not()
    {
        var (serviceBus, serviceRegistry) = CreateServiceSide();
        await serviceRegistry.RegisterAsync(new EntityDescriptor { Id = "lock_screen", Name = "Lock screen", Kind = EntityKind.Button });
        await StartServerAsync(serviceBus, serviceRegistry);
        await using var trayCommands = _trayBus.Subscribe<ActionCommand>();
        await StartClientAsync();
        await _trayRegistry.RegisterAsync(new EntityDescriptor { Id = "audio_mute", Name = "Mute", Kind = EntityKind.Switch });
        await WaitUntilAsync(() => serviceRegistry.TryGet("audio_mute", out _));

        await serviceBus.PublishAsync(new ActionCommand { ActionId = "lock_screen", Origin = "mqtt" });
        await serviceBus.PublishAsync(new ActionCommand
        {
            ActionId = "audio_mute",
            Value = BinaryState.On,
            Origin = "mqtt",
            Parameters = new Dictionary<string, object?> { ["title"] = "Hello" },
        });

        var command = await ReadAsync(trayCommands);
        Assert.Equal("audio_mute", command.ActionId);
        Assert.Equal(BinaryState.On, command.Value);
        Assert.Equal("mqtt", command.Origin);
        Assert.Equal("Hello", command.GetParameter("title"));
        Assert.False(trayCommands.TryRead(out _));
    }

    [Fact]
    public async Task The_service_can_ask_the_tray_to_start_a_program_without_any_entity()
    {
        var (serviceBus, serviceRegistry) = CreateServiceSide();
        await StartServerAsync(serviceBus, serviceRegistry);
        await using var trayCommands = _trayBus.Subscribe<ActionCommand>();
        var client = await StartClientAsync();
        await _trayRegistry.RegisterAsync(Sensor("audio_volume"));
        await WaitUntilAsync(() => client.IsConnected && serviceRegistry.TryGet("audio_volume", out _));

        await serviceBus.PublishAsync(new ActionCommand { ActionId = SessionCommands.Launch, Value = "notepad" });

        var command = await ReadAsync(trayCommands);
        Assert.Equal(SessionCommands.Launch, command.ActionId);
        Assert.Equal("notepad", command.Value);
    }

    [Fact]
    public async Task With_several_users_signed_in_only_the_one_at_the_computer_is_reported()
    {
        var sessions = new FakeSessions { ConsoleSessionId = 1 };
        sessions.SessionOf["tray:alice"] = 1;
        sessions.SessionOf["tray:bob"] = 2;
        _options.SessionCheckInterval = TimeSpan.FromMilliseconds(50);

        var (serviceBus, serviceRegistry) = CreateServiceSide();
        await using var serviceReadings = serviceBus.Subscribe<TelemetryEvent>();
        await StartServerAsync(serviceBus, serviceRegistry, sessions);

        // Alice sits at the computer; Bob is signed in too, switched away from.
        await using var alice = await SecondTray.StartAsync(_options.PipeName, "tray:alice");
        await alice.ReportAsync("active_window", "Alice's editor");
        Assert.Equal("Alice's editor", (await ReadAsync(serviceReadings)).State);

        await using var bob = await SecondTray.StartAsync(_options.PipeName, "tray:bob");
        await bob.ReportAsync("active_window", "Bob's browser");
        await bob.Registry.RegisterAsync(new EntityDescriptor { Id = "audio_mute", Name = "Mute", Kind = EntityKind.Switch });
        await alice.Registry.RegisterAsync(new EntityDescriptor { Id = "audio_mute", Name = "Mute", Kind = EntityKind.Switch });
        await WaitUntilAsync(() => serviceRegistry.TryGet("audio_mute", out _));
        await Task.Delay(300);
        Assert.False(serviceReadings.TryRead(out _));

        // Bob comes to the front: his window is reported without him having to do anything, and commands go to him.
        sessions.ConsoleSessionId = 2;
        var reading = await ReadAsync(serviceReadings);
        Assert.Equal("Bob's browser", reading.State);
        Assert.Equal("tray:bob", reading.Source);

        await using var bobCommands = bob.Bus.Subscribe<ActionCommand>();
        await using var aliceCommands = alice.Bus.Subscribe<ActionCommand>();
        await serviceBus.PublishAsync(new ActionCommand { ActionId = "audio_mute", Value = BinaryState.On });
        Assert.Equal("audio_mute", (await ReadAsync(bobCommands)).ActionId);
        Assert.False(aliceCommands.TryRead(out _));

        // The sign-in screen: nobody's desktop is in use, so nobody's window is reported.
        sessions.ConsoleSessionId = 3;
        await WaitUntilAsync(() => !serviceRegistry.IsAvailable("active_window"));

        sessions.ConsoleSessionId = 1;
        Assert.Equal("Alice's editor", (await ReadAsync(serviceReadings)).State);
        Assert.True(serviceRegistry.IsAvailable("active_window"));
    }

    [Fact]
    public async Task Service_rejects_foreign_entities_and_unregistered_readings()
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
            new EntityRegistrationMessage(new EntityDescriptor { Id = "Not An Id", Name = "Rogue", Kind = EntityKind.Button }),
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
        Assert.DoesNotContain(serviceRegistry.Entities, entity => entity.Name == "Rogue");
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

    private async Task<IpcServer> StartServerAsync(IEventBus bus, IEntityRegistry registry, ISessionDirectory? sessions = null)
    {
        var server = new IpcServer(bus, registry, Options.Create(_options), NullLogger<IpcServer>.Instance, sessions: sessions);
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

    /// <summary>Sessions as the test says they are; clients are told apart by their names.</summary>
    private sealed class FakeSessions : ISessionDirectory
    {
        private volatile uint _console;

        public Dictionary<string, uint> SessionOf { get; } = [];

        public uint? ConsoleSessionId
        {
            get => _console;
            set => _console = value ?? 0;
        }

        public uint? GetClientSessionId(NamedPipeServerStream pipe, string clientName) =>
            SessionOf.TryGetValue(clientName, out var session) ? session : null;

        // Only the console is in use; nobody is connected remotely.
        public bool IsConnected(uint sessionId) => false;
    }

    /// <summary>The tray of a further signed-in user, with a bus and registry of its own.</summary>
    private sealed class SecondTray : IAsyncDisposable
    {
        private readonly IpcClient _client;

        private SecondTray(string pipeName, string clientName)
        {
            Registry = new EntityRegistry(Bus);
            _client = new IpcClient(
                Bus,
                Registry,
                Options.Create(new IpcOptions { PipeName = pipeName, ClientName = clientName, MinReconnectDelay = TimeSpan.FromMilliseconds(50) }),
                NullLogger<IpcClient>.Instance);
        }

        public ChannelEventBus Bus { get; } = new();

        public EntityRegistry Registry { get; }

        public static async Task<SecondTray> StartAsync(string pipeName, string clientName)
        {
            var tray = new SecondTray(pipeName, clientName);
            await tray._client.StartAsync(CancellationToken.None);
            await WaitUntilAsync(() => tray._client.IsConnected);
            return tray;
        }

        public async Task ReportAsync(string sensorId, string state)
        {
            await Registry.RegisterAsync(Sensor(sensorId));
            await Bus.PublishAsync(new TelemetryEvent { SensorId = sensorId, State = state });
        }

        public async ValueTask DisposeAsync()
        {
            await _client.StopAsync(CancellationToken.None);
            _client.Dispose();
            await Bus.DisposeAsync();
        }
    }
}

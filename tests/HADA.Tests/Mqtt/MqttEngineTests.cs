using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Messaging;
using HADA.Core.Models;
using HADA.Engine.Mqtt;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;

namespace HADA.Tests.Mqtt;

/// <summary>Runs the engine against an in-process MQTT broker, observed by a second client subscribed to everything.</summary>
public sealed class MqttEngineTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly ChannelEventBus _bus = new();
    private readonly EntityRegistry _registry;
    private readonly List<ObservedMessage> _observed = [];
    private readonly SemaphoreSlim _messageArrived = new(0);
    private readonly int _port = GetFreePort();
    private MqttServer _broker = null!;
    private IMqttClient _observer = null!;

    public MqttEngineTests() => _registry = new EntityRegistry(_bus);

    public async Task InitializeAsync()
    {
        var serverFactory = new MqttServerFactory();
        _broker = serverFactory.CreateMqttServer(serverFactory.CreateServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointBoundIPAddress(IPAddress.Loopback)
            .WithDefaultEndpointBoundIPV6Address(IPAddress.IPv6Loopback)
            .WithDefaultEndpointPort(_port)
            .Build());
        await _broker.StartAsync();

        _observer = new MqttClientFactory().CreateMqttClient();
        _observer.ApplicationMessageReceivedAsync += e =>
        {
            var message = e.ApplicationMessage;
            lock (_observed)
            {
                // MQTTnet returns null rather than "" for an empty payload, e.g. a cleared retained message.
                _observed.Add(new ObservedMessage(message.Topic, message.ConvertPayloadToString() ?? string.Empty));
            }

            _messageArrived.Release();
            return Task.CompletedTask;
        };
        await _observer.ConnectAsync(ClientOptions("observer"), CancellationToken.None);
        await _observer.SubscribeAsync("#", MqttQualityOfServiceLevel.AtLeastOnce, CancellationToken.None);

        await _registry.RegisterAsync(new EntityDescriptor
        {
            Id = "cpu_load",
            Name = "CPU load",
            Kind = EntityKind.Sensor,
            UnitOfMeasurement = "%",
            StateClass = "measurement",
        });
        await _registry.RegisterAsync(new EntityDescriptor { Id = "lock_screen", Name = "Lock screen", Kind = EntityKind.Button });
    }

    public async Task DisposeAsync()
    {
        _observer.Dispose();
        await _broker.StopAsync(new MqttServerFactory().CreateMqttServerStopOptionsBuilder().Build());
        _broker.Dispose();
        _messageArrived.Dispose();
        await _bus.DisposeAsync();
    }

    [Fact]
    public async Task Start_publishes_availability_and_discovery()
    {
        await using var engine = CreateEngine();
        await engine.StartAsync(CancellationToken.None);

        await WaitForMessageAsync("hada/testpc/availability", "online");

        var sensor = await WaitForMessageAsync("homeassistant/sensor/testpc/cpu_load/config");
        using (var config = JsonDocument.Parse(sensor.Payload))
        {
            var root = config.RootElement;
            Assert.Equal("CPU load", root.GetProperty("name").GetString());
            Assert.Equal("hada_testpc_cpu_load", root.GetProperty("unique_id").GetString());
            Assert.Equal("hada/testpc/cpu_load/state", root.GetProperty("state_topic").GetString());
            Assert.Equal("hada/testpc/availability", root.GetProperty("availability")[0].GetProperty("topic").GetString());
            Assert.Equal("hada/testpc/cpu_load/availability", root.GetProperty("availability")[1].GetProperty("topic").GetString());
            Assert.Equal("all", root.GetProperty("availability_mode").GetString());
            Assert.Equal("%", root.GetProperty("unit_of_measurement").GetString());
            Assert.Equal("Test PC", root.GetProperty("device").GetProperty("name").GetString());
            Assert.Equal("hada_testpc", root.GetProperty("device").GetProperty("identifiers")[0].GetString());
            Assert.False(root.TryGetProperty("command_topic", out _));
        }

        // Live delivery to an existing subscriber clears the retain flag, so ask the broker what it retained.
        var retained = await _broker.GetRetainedMessageAsync("homeassistant/sensor/testpc/cpu_load/config");
        Assert.Equal(sensor.Payload, retained?.ConvertPayloadToString());

        var button = await WaitForMessageAsync("homeassistant/button/testpc/lock_screen/config");
        using (var config = JsonDocument.Parse(button.Payload))
        {
            Assert.Equal("hada/testpc/lock_screen/set", config.RootElement.GetProperty("command_topic").GetString());
            Assert.Equal("PRESS", config.RootElement.GetProperty("payload_press").GetString());
        }
    }

    [Fact]
    public async Task Telemetry_for_registered_sensor_is_published_to_state_topic()
    {
        await using var engine = CreateEngine();
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilConnectedAsync(engine);

        await _bus.PublishAsync(new TelemetryEvent { SensorId = "unknown_sensor", State = "1" });
        await _bus.PublishAsync(new TelemetryEvent
        {
            SensorId = "cpu_load",
            State = "12.5",
            Attributes = new Dictionary<string, object?> { ["cores"] = 8 },
        });

        var state = await WaitForMessageAsync("hada/testpc/cpu_load/state");
        Assert.Equal("12.5", state.Payload);
        var attributes = await WaitForMessageAsync("hada/testpc/cpu_load/attributes");
        Assert.Equal("""{"cores":8}""", attributes.Payload);
        Assert.DoesNotContain(Snapshot(), m => m.Topic.Contains("unknown_sensor"));

        // Retained, so Home Assistant still gets the value when it subscribes after processing the discovery config.
        Assert.Equal("12.5", (await _broker.GetRetainedMessageAsync("hada/testpc/cpu_load/state"))?.ConvertPayloadToString());
    }

    [Fact]
    public async Task Button_press_publishes_action_command_and_ignores_replays_and_non_buttons()
    {
        // A retained press must not lock the screen again every time the engine reconnects.
        await PublishAsync("hada/testpc/lock_screen/set", "PRESS", retain: true);
        await using var commands = _bus.Subscribe<ActionCommand>();
        await using var engine = CreateEngine();
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilConnectedAsync(engine);

        await PublishAsync("hada/testpc/cpu_load/set", "PRESS");
        await PublishAsync("hada/testpc/lock_screen/set", "WRONG");
        await PublishAsync("hada/testpc/lock_screen/set", "PRESS");

        using var timeout = new CancellationTokenSource(Timeout);
        await using var enumerator = commands.ReadAllAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("lock_screen", enumerator.Current.ActionId);
        Assert.Equal("mqtt", enumerator.Current.Origin);
        Assert.False(commands.TryRead(out _));
    }

    [Fact]
    public async Task Binary_sensors_are_discovered_as_binary_sensors_with_on_off_payloads()
    {
        await using var engine = CreateEngine();
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilConnectedAsync(engine);

        // Registered after connecting, like a tray sensor or a custom sensor added in the settings window.
        await _registry.RegisterAsync(new EntityDescriptor { Id = "display_on", Name = "Display", Kind = EntityKind.BinarySensor });
        await _bus.PublishAsync(new TelemetryEvent { SensorId = "display_on", State = BinaryState.On });

        var discovery = await WaitForMessageAsync("homeassistant/binary_sensor/testpc/display_on/config");
        using (var config = JsonDocument.Parse(discovery.Payload))
        {
            var root = config.RootElement;
            Assert.Equal("hada/testpc/display_on/state", root.GetProperty("state_topic").GetString());
            Assert.Equal("on", root.GetProperty("payload_on").GetString());
            Assert.Equal("off", root.GetProperty("payload_off").GetString());
        }

        await WaitForMessageAsync("hada/testpc/display_on/state", "on");
        Assert.False(JsonDocument.Parse((await WaitForMessageAsync("homeassistant/sensor/testpc/cpu_load/config")).Payload)
            .RootElement.TryGetProperty("payload_on", out _));
    }

    [Fact]
    public async Task An_entity_whose_source_is_away_is_reported_unavailable_until_it_returns()
    {
        await using var engine = CreateEngine();
        await engine.StartAsync(CancellationToken.None);
        await WaitForMessageAsync("hada/testpc/cpu_load/availability", "online");

        await _registry.SetAvailabilityAsync("cpu_load", isAvailable: false);
        await WaitForMessageAsync("hada/testpc/cpu_load/availability", "offline");

        // Retained, so it still holds after Home Assistant or this engine reconnects.
        Assert.Equal("offline", (await _broker.GetRetainedMessageAsync("hada/testpc/cpu_load/availability"))?.ConvertPayloadToString());

        await _registry.SetAvailabilityAsync("cpu_load", isAvailable: true);
        await WaitUntilAsync(async () =>
            (await _broker.GetRetainedMessageAsync("hada/testpc/cpu_load/availability"))?.ConvertPayloadToString() == "online");
    }

    [Fact]
    public async Task Unregistered_entities_are_removed_from_home_assistant()
    {
        await using var engine = CreateEngine();
        await engine.StartAsync(CancellationToken.None);
        await WaitForMessageAsync("homeassistant/sensor/testpc/cpu_load/config");
        await _bus.PublishAsync(new TelemetryEvent { SensorId = "cpu_load", State = "12.5" });
        await WaitForMessageAsync("hada/testpc/cpu_load/state", "12.5");

        Assert.True(await _registry.UnregisterAsync("cpu_load"));

        await WaitForMessageAsync("homeassistant/sensor/testpc/cpu_load/config", payload: string.Empty);
        await WaitForMessageAsync("hada/testpc/cpu_load/state", payload: string.Empty);
        Assert.Null(await _broker.GetRetainedMessageAsync("homeassistant/sensor/testpc/cpu_load/config"));
        Assert.Null(await _broker.GetRetainedMessageAsync("hada/testpc/cpu_load/state"));
    }

    [Fact]
    public async Task Stop_publishes_offline_availability()
    {
        await using var engine = CreateEngine();
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilConnectedAsync(engine);

        await engine.StopAsync(CancellationToken.None);

        await WaitForMessageAsync("hada/testpc/availability", "offline");
        Assert.Equal(EngineConnectionState.Disconnected, engine.State);
    }

    [Fact]
    public async Task Start_without_host_stays_idle()
    {
        await using var engine = new MqttEngine(
            _bus, _registry, Options.Create(new MqttOptions()), NullLogger<MqttEngine>.Instance);

        await engine.StartAsync(CancellationToken.None);

        Assert.Equal(EngineConnectionState.Disconnected, engine.State);
    }

    [Fact]
    public async Task Disabled_entities_are_removed_from_home_assistant_and_ignored()
    {
        // As if the sensor had been announced before it was disabled.
        await PublishAsync("homeassistant/sensor/testpc/cpu_load/config", "{}", retain: true);
        await using var commands = _bus.Subscribe<ActionCommand>();
        await using var engine = CreateEngine(new EntityFilter(["cpu_load", "lock_screen"]));
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilConnectedAsync(engine);

        await WaitForMessageAsync("homeassistant/sensor/testpc/cpu_load/config", payload: string.Empty);
        Assert.Null(await _broker.GetRetainedMessageAsync("homeassistant/sensor/testpc/cpu_load/config"));

        await _bus.PublishAsync(new TelemetryEvent { SensorId = "cpu_load", State = "50" });
        await PublishAsync("hada/testpc/lock_screen/set", "PRESS");
        await Task.Delay(300);

        Assert.DoesNotContain(Snapshot(), message => message.Topic == "hada/testpc/cpu_load/state" && message.Payload.Length > 0);
        Assert.DoesNotContain(Snapshot(), message =>
            message.Topic == "homeassistant/button/testpc/lock_screen/config" && message.Payload.Length > 0);
        Assert.False(commands.TryRead(out _));
    }

    [Fact]
    public async Task TestConnectionAsync_succeeds_against_a_running_broker()
    {
        var result = await MqttEngine.TestConnectionAsync(new MqttOptions { Host = "127.0.0.1", Port = _port }, CancellationToken.None);

        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public async Task TestConnectionAsync_fails_when_nothing_is_listening()
    {
        var result = await MqttEngine.TestConnectionAsync(new MqttOptions { Host = "127.0.0.1", Port = GetFreePort() }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    private MqttEngine CreateEngine(IEntityFilter? filter = null) => new(
        _bus,
        _registry,
        Options.Create(new MqttOptions
        {
            Host = "127.0.0.1",
            Port = _port,
            DeviceId = "TestPC",
            DeviceName = "Test PC",
            MinReconnectDelay = TimeSpan.FromMilliseconds(100),
        }),
        NullLogger<MqttEngine>.Instance,
        filter);

    private MqttClientOptions ClientOptions(string clientId) => new MqttClientOptionsBuilder()
        .WithTcpServer("127.0.0.1", _port, AddressFamily.Unspecified)
        .WithClientId(clientId)
        .Build();

    private Task PublishAsync(string topic, string payload, bool retain = false) =>
        _observer.PublishStringAsync(topic, payload, MqttQualityOfServiceLevel.AtLeastOnce, retain, CancellationToken.None);

    private ObservedMessage[] Snapshot()
    {
        lock (_observed)
        {
            return [.. _observed];
        }
    }

    /// <summary>Searches everything observed so far, then waits for new messages. Order of arrival does not matter.</summary>
    private async Task<ObservedMessage> WaitForMessageAsync(string topic, string? payload = null)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var match = Array.Find(Snapshot(), m => m.Topic == topic && (payload is null || m.Payload == payload));
            if (match is not null)
            {
                return match;
            }

            var remaining = Timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero || !await _messageArrived.WaitAsync(remaining))
            {
                throw new TimeoutException($"No message on '{topic}' (payload '{payload ?? "*"}') within {Timeout}.");
            }
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (!await condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private static async Task WaitUntilConnectedAsync(MqttEngine engine)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (engine.State != EngineConnectionState.Connected)
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed record ObservedMessage(string Topic, string Payload);
}

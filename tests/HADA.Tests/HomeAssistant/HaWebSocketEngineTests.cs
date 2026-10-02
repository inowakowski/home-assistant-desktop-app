using System.Net.WebSockets;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Messaging;
using HADA.Core.Models;
using HADA.Engine.WebSocket;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HADA.Tests.HomeAssistant;

public sealed class HaWebSocketEngineTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly FakeHomeAssistant _homeAssistant = new();
    private readonly ChannelEventBus _bus = new();
    private readonly EntityRegistry _registry;

    public HaWebSocketEngineTests() => _registry = new EntityRegistry(_bus);

    public async Task InitializeAsync()
    {
        await _registry.RegisterAsync(new EntityDescriptor
        {
            Id = "cpu_load",
            Name = "CPU load",
            Kind = EntityKind.Sensor,
            UnitOfMeasurement = "%",
        });
        await _registry.RegisterAsync(new EntityDescriptor { Id = "lock_screen", Name = "Lock screen", Kind = EntityKind.Button });
    }

    public async Task DisposeAsync()
    {
        await _homeAssistant.DisposeAsync();
        await _bus.DisposeAsync();
    }

    [Fact]
    public async Task Authenticates_and_subscribes_to_command_events()
    {
        await using var engine = CreateEngine();
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => engine.State == EngineConnectionState.Connected);

        var messages = _homeAssistant.SocketMessages;
        Assert.Equal("auth", messages[0].GetProperty("type").GetString());
        Assert.Equal(FakeHomeAssistant.ValidToken, messages[0].GetProperty("access_token").GetString());
        Assert.Contains(messages, m =>
            m.GetProperty("type").GetString() == "subscribe_events"
            && m.GetProperty("event_type").GetString() == "hada_command");
    }

    [Fact]
    public async Task Rejected_token_faults_without_retrying()
    {
        await using var engine = CreateEngine(accessToken: "wrong-token");
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => engine.State == EngineConnectionState.Faulted);

        // Several reconnect delays' worth of time.
        await Task.Delay(500);

        Assert.Equal(1, _homeAssistant.ConnectionAttempts);
        Assert.Equal(EngineConnectionState.Faulted, engine.State);
    }

    [Fact]
    public async Task Readings_are_written_as_states_with_entity_metadata()
    {
        await using var engine = CreateEngine();
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => engine.State == EngineConnectionState.Connected);

        await _bus.PublishAsync(new TelemetryEvent { SensorId = "unknown_sensor", State = "1" });
        await _bus.PublishAsync(new TelemetryEvent
        {
            SensorId = "cpu_load",
            State = "12.5",
            Attributes = new Dictionary<string, object?> { ["cores"] = 8 },
        });

        var write = await WaitForStateWriteAsync("sensor.testpc_cpu_load");
        Assert.Equal($"Bearer {FakeHomeAssistant.ValidToken}", write.Authorization);
        Assert.Equal("12.5", write.Body.GetProperty("state").GetString());
        var attributes = write.Body.GetProperty("attributes");
        Assert.Equal("Test PC CPU load", attributes.GetProperty("friendly_name").GetString());
        Assert.Equal("%", attributes.GetProperty("unit_of_measurement").GetString());
        Assert.Equal(8, attributes.GetProperty("cores").GetInt32());
        Assert.DoesNotContain(_homeAssistant.StateWrites, w => w.EntityId.Contains("unknown_sensor"));
    }

    [Fact]
    public async Task Binary_sensors_are_written_to_the_binary_sensor_domain_and_deleted_when_unregistered()
    {
        await using var engine = CreateEngine();
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => engine.State == EngineConnectionState.Connected);

        await _registry.RegisterAsync(new EntityDescriptor { Id = "display_on", Name = "Display", Kind = EntityKind.BinarySensor });
        await _bus.PublishAsync(new TelemetryEvent { SensorId = "display_on", State = BinaryState.On });

        var write = await WaitForStateWriteAsync("binary_sensor.testpc_display_on");
        Assert.Equal("on", write.Body.GetProperty("state").GetString());

        await _registry.UnregisterAsync("display_on");

        await WaitUntilAsync(() => Array.Exists(
            _homeAssistant.StateWrites, w => w.Method == "DELETE" && w.EntityId == "binary_sensor.testpc_display_on"));
    }

    [Fact]
    public async Task A_sensor_whose_source_is_away_is_set_unavailable_and_restored_when_it_returns()
    {
        await using var engine = CreateEngine();
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => engine.State == EngineConnectionState.Connected);
        await _bus.PublishAsync(new TelemetryEvent { SensorId = "cpu_load", State = "12.5" });
        await WaitForStateWriteAsync("sensor.testpc_cpu_load");

        await _registry.SetAvailabilityAsync("cpu_load", isAvailable: false);
        await WaitUntilAsync(() => LastState("sensor.testpc_cpu_load") == "unavailable");

        await _registry.SetAvailabilityAsync("cpu_load", isAvailable: true);
        await WaitUntilAsync(() => LastState("sensor.testpc_cpu_load") == "12.5");
    }

    private string? LastState(string entityId) =>
        _homeAssistant.StateWrites.LastOrDefault(write => write.EntityId == entityId && write.Method == "POST")
            ?.Body.GetProperty("state").GetString();

    [Fact]
    public async Task Command_event_for_this_device_publishes_action_command()
    {
        await using var commands = _bus.Subscribe<ActionCommand>();
        await using var engine = CreateEngine();
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => engine.State == EngineConnectionState.Connected);

        await _homeAssistant.SendCommandEventAsync(new { device_id = "other_pc", action = "lock_screen" });
        await _homeAssistant.SendCommandEventAsync(new { device_id = "testpc", action = "cpu_load" });
        await _homeAssistant.SendCommandEventAsync(new { device_id = "testpc", action = "lock_screen" });

        using var timeout = new CancellationTokenSource(Timeout);
        await using var enumerator = commands.ReadAllAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("lock_screen", enumerator.Current.ActionId);
        Assert.Equal("websocket", enumerator.Current.Origin);
        Assert.False(commands.TryRead(out _));
    }

    [Fact]
    public async Task Command_events_carry_values_for_switches_numbers_and_notifications()
    {
        await _registry.RegisterAsync(new EntityDescriptor { Id = "audio_mute", Name = "Mute", Kind = EntityKind.Switch });
        await _registry.RegisterAsync(new EntityDescriptor { Id = "volume_level", Name = "Volume level", Kind = EntityKind.Number, Min = 0, Max = 100 });
        await _registry.RegisterAsync(new EntityDescriptor { Id = "notification", Name = "Notification", Kind = EntityKind.Notify });
        await using var commands = _bus.Subscribe<ActionCommand>();
        await using var engine = CreateEngine();
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => engine.State == EngineConnectionState.Connected);

        await _homeAssistant.SendCommandEventAsync(new { device_id = "testpc", action = "audio_mute" });
        await _homeAssistant.SendCommandEventAsync(new { device_id = "testpc", action = "volume_level", value = 250 });
        await _homeAssistant.SendCommandEventAsync(new { device_id = "testpc", action = "audio_mute", value = "on" });
        await _homeAssistant.SendCommandEventAsync(new { device_id = "testpc", action = "volume_level", value = 40 });
        await _homeAssistant.SendCommandEventAsync(new { device_id = "testpc", action = "notification", title = "Laundry", message = "Done" });

        using var timeout = new CancellationTokenSource(Timeout);
        var received = new List<ActionCommand>();
        await foreach (var command in commands.ReadAllAsync(timeout.Token))
        {
            received.Add(command);
            if (received.Count == 3)
            {
                break;
            }
        }

        Assert.Equal(("audio_mute", "on"), (received[0].ActionId, received[0].Value));
        Assert.Equal(("volume_level", "40"), (received[1].ActionId, received[1].Value));
        Assert.Equal(("notification", "Done"), (received[2].ActionId, received[2].Value));
        Assert.Equal("Laundry", received[2].GetParameter("title"));
        Assert.False(commands.TryRead(out _));
    }

    [Fact]
    public async Task What_happens_on_the_computer_is_fired_as_an_event()
    {
        await _registry.RegisterAsync(new EntityDescriptor { Id = "toggle_lamp", Name = "Toggle lamp", Kind = EntityKind.Trigger });
        await using var engine = CreateEngine();
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => engine.State == EngineConnectionState.Connected);

        await _bus.PublishAsync(new DeviceEvent { Name = DeviceEvent.QuickAction, Value = "no_such_action" });
        await _bus.PublishAsync(new DeviceEvent { Name = DeviceEvent.QuickAction, Value = "toggle_lamp" });

        await WaitUntilAsync(() => Array.Exists(_homeAssistant.SocketMessages, m => m.GetProperty("type").GetString() == "fire_event"));
        var fired = Assert.Single(_homeAssistant.SocketMessages, m => m.GetProperty("type").GetString() == "fire_event");
        Assert.Equal("hada_event", fired.GetProperty("event_type").GetString());
        var data = fired.GetProperty("event_data");
        Assert.Equal("testpc", data.GetProperty("device_id").GetString());
        Assert.Equal("quick_action", data.GetProperty("name").GetString());
        Assert.Equal("toggle_lamp", data.GetProperty("value").GetString());
    }

    [Fact]
    public async Task A_switch_is_shown_as_a_binary_sensor_and_a_number_as_a_sensor()
    {
        await _registry.RegisterAsync(new EntityDescriptor { Id = "audio_mute", Name = "Mute", Kind = EntityKind.Switch });
        await _registry.RegisterAsync(new EntityDescriptor { Id = "volume_level", Name = "Volume level", Kind = EntityKind.Number });
        await using var engine = CreateEngine();
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => engine.State == EngineConnectionState.Connected);

        await _bus.PublishAsync(new TelemetryEvent { SensorId = "audio_mute", State = BinaryState.On });
        await _bus.PublishAsync(new TelemetryEvent { SensorId = "volume_level", State = "40" });

        Assert.Equal("on", (await WaitForStateWriteAsync("binary_sensor.testpc_audio_mute")).Body.GetProperty("state").GetString());
        Assert.Equal("40", (await WaitForStateWriteAsync("sensor.testpc_volume_level")).Body.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Stop_marks_sensors_unavailable_and_closes_the_socket_gracefully()
    {
        await using var engine = CreateEngine();
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => engine.State == EngineConnectionState.Connected);
        await _bus.PublishAsync(new TelemetryEvent { SensorId = "cpu_load", State = "12.5" });
        await WaitForStateWriteAsync("sensor.testpc_cpu_load");

        await engine.StopAsync(CancellationToken.None);

        Assert.Equal(EngineConnectionState.Disconnected, engine.State);
        var last = _homeAssistant.StateWrites.Last(w => w.EntityId == "sensor.testpc_cpu_load");
        Assert.Equal("unavailable", last.Body.GetProperty("state").GetString());
        Assert.Equal("Test PC CPU load", last.Body.GetProperty("attributes").GetProperty("friendly_name").GetString());
        await WaitUntilAsync(() => _homeAssistant.ClientCloseStatus == WebSocketCloseStatus.NormalClosure);
    }

    [Fact]
    public async Task Start_without_configuration_stays_idle()
    {
        await using var engine = new HaWebSocketEngine(
            _bus, _registry, Options.Create(new HaWebSocketOptions()), NullLogger<HaWebSocketEngine>.Instance);

        await engine.StartAsync(CancellationToken.None);

        Assert.Equal(EngineConnectionState.Disconnected, engine.State);
        Assert.Equal(0, _homeAssistant.ConnectionAttempts);
    }

    [Fact]
    public async Task Disabled_sensors_are_deleted_from_home_assistant_and_ignored()
    {
        await using var engine = CreateEngine(filter: new EntityFilter(["cpu_load"]));
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => Array.Exists(
            _homeAssistant.StateWrites, write => write.Method == "DELETE" && write.EntityId == "sensor.testpc_cpu_load"));

        await _bus.PublishAsync(new TelemetryEvent { SensorId = "cpu_load", State = "12.5" });
        await Task.Delay(300);

        Assert.DoesNotContain(_homeAssistant.StateWrites, write => write.Method == "POST");
    }

    [Fact]
    public async Task TestConnectionAsync_succeeds_with_a_valid_token()
    {
        var result = await HaWebSocketEngine.TestConnectionAsync(CreateOptions(FakeHomeAssistant.ValidToken), CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Contains(FakeHomeAssistant.Version, result.Message);
    }

    [Fact]
    public async Task TestConnectionAsync_reports_a_rejected_token()
    {
        var result = await HaWebSocketEngine.TestConnectionAsync(CreateOptions("wrong-token"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("rejected", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private HaWebSocketEngine CreateEngine(string accessToken = FakeHomeAssistant.ValidToken, IEntityFilter? filter = null) => new(
        _bus,
        _registry,
        Options.Create(CreateOptions(accessToken)),
        NullLogger<HaWebSocketEngine>.Instance,
        filter);

    private HaWebSocketOptions CreateOptions(string accessToken) => new()
    {
        BaseUrl = _homeAssistant.BaseUrl.ToString(),
        AccessToken = accessToken,
        DeviceId = "TestPC",
        DeviceName = "Test PC",
        MinReconnectDelay = TimeSpan.FromMilliseconds(50),
    };

    private async Task<StateWrite> WaitForStateWriteAsync(string entityId)
    {
        StateWrite? write = null;
        await WaitUntilAsync(() => (write = Array.Find(_homeAssistant.StateWrites, w => w.EntityId == entityId)) is not null);
        return write!;
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

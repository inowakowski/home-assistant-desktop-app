using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Messaging;
using HADA.Core.Models;
using HADA.Engine.WebSocket;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HADA.Tests.HomeAssistant;

/// <summary>
/// The WebSocket engine talking to the HADA integration for Home Assistant: naming the computer and its entities,
/// reporting what they say, and coping with a Home Assistant that does not have the integration.
/// </summary>
public sealed class IntegrationTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly FakeHomeAssistant _homeAssistant = new() { Integration = FakeIntegration.SetUp };
    private readonly ChannelEventBus _bus = new();
    private readonly EntityRegistry _registry;

    public IntegrationTests() => _registry = new EntityRegistry(_bus);

    public async Task InitializeAsync()
    {
        await _registry.RegisterAsync(new EntityDescriptor
        {
            Id = "cpu_load", Name = "CPU load", Kind = EntityKind.Sensor, UnitOfMeasurement = "%", StateClass = "measurement", Icon = "mdi:cpu-64-bit",
        });
        await _registry.RegisterAsync(new EntityDescriptor { Id = "display_on", Name = "Display", Kind = EntityKind.BinarySensor, DeviceClass = "power" });
        await _registry.RegisterAsync(new EntityDescriptor { Id = "lock_screen", Name = "Lock screen", Kind = EntityKind.Button });
    }

    public async Task DisposeAsync()
    {
        await _homeAssistant.DisposeAsync();
        await _bus.DisposeAsync();
    }

    [Fact]
    public async Task The_computer_names_itself_and_every_entity_it_has()
    {
        await _registry.RegisterAsync(new EntityDescriptor { Id = "audio_mute", Name = "Mute", Kind = EntityKind.Switch });
        await _registry.RegisterAsync(new EntityDescriptor
        {
            Id = "volume_level", Name = "Volume", Kind = EntityKind.Number, StateClass = "measurement", Min = 0, Max = 100, Step = 5,
        });
        await using var engine = CreateEngine();

        // Reported before the connection is there: part of what is sent on connecting.
        await engine.StartAsync(CancellationToken.None);
        await _bus.PublishAsync(new TelemetryEvent { SensorId = "cpu_load", State = "12.5", Attributes = new Dictionary<string, object?> { ["cores"] = 8 } });
        await WaitUntilAsync(() => _homeAssistant.IntegrationEntity("cpu_load")?.State == "12.5");

        var connect = Assert.Single(_homeAssistant.SocketMessages, m => m.GetProperty("type").GetString() == "hada/connect");
        Assert.Equal(1, connect.GetProperty("protocol").GetInt32());
        var device = _homeAssistant.IntegrationDevice!.Value;
        Assert.Equal("testpc", device.GetProperty("id").GetString());
        Assert.Equal("Test PC", device.GetProperty("name").GetString());
        Assert.False(string.IsNullOrEmpty(device.GetProperty("app_version").GetString()));

        Assert.Equal(["audio_mute", "cpu_load", "display_on", "lock_screen", "volume_level"], _homeAssistant.IntegrationEntityIds);

        var cpu = _homeAssistant.IntegrationEntity("cpu_load")!;
        Assert.Equal("sensor", cpu.Descriptor.GetProperty("kind").GetString());
        Assert.Equal("CPU load", cpu.Descriptor.GetProperty("name").GetString());
        Assert.Equal("%", cpu.Descriptor.GetProperty("unit").GetString());
        Assert.Equal("measurement", cpu.Descriptor.GetProperty("state_class").GetString());
        Assert.Equal("mdi:cpu-64-bit", cpu.Descriptor.GetProperty("icon").GetString());
        Assert.Equal(8, cpu.Attributes!.Value.GetProperty("cores").GetInt32());

        var display = _homeAssistant.IntegrationEntity("display_on")!;
        Assert.Equal("binary_sensor", display.Descriptor.GetProperty("kind").GetString());
        Assert.Equal("power", display.Descriptor.GetProperty("device_class").GetString());
        Assert.Null(display.State);
        Assert.Equal("switch", _homeAssistant.IntegrationEntity("audio_mute")!.Descriptor.GetProperty("kind").GetString());
        Assert.Equal("button", _homeAssistant.IntegrationEntity("lock_screen")!.Descriptor.GetProperty("kind").GetString());

        var volume = _homeAssistant.IntegrationEntity("volume_level")!.Descriptor;
        Assert.Equal("number", volume.GetProperty("kind").GetString());
        Assert.Equal((0, 100, 5), (volume.GetProperty("min").GetInt32(), volume.GetProperty("max").GetInt32(), volume.GetProperty("step").GetInt32()));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, volume.GetProperty("state_class").ValueKind);

        Assert.Null(engine.Issue);
        Assert.DoesNotContain(_homeAssistant.StateWrites, write => write.Method == "POST");
        Assert.Empty(_homeAssistant.Registrations);
    }

    [Fact]
    public async Task What_an_entity_reports_is_sent_as_its_kind_of_value()
    {
        await using var engine = CreateEngine();
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => _homeAssistant.IsIntegrationConnected);

        await _bus.PublishAsync(new TelemetryEvent { SensorId = "cpu_load", State = "14" });
        await _bus.PublishAsync(new TelemetryEvent { SensorId = "display_on", State = BinaryState.On });
        await WaitUntilAsync(() => _homeAssistant.IntegrationEntity("display_on")?.State == "true");

        Assert.Equal("14", _homeAssistant.IntegrationEntity("cpu_load")!.State);

        await _bus.PublishAsync(new TelemetryEvent { SensorId = "display_on", State = BinaryState.Off });
        await WaitUntilAsync(() => _homeAssistant.IntegrationEntity("display_on")?.State == "false");
    }

    [Fact]
    public async Task What_is_pressed_switched_or_set_in_home_assistant_becomes_a_command_and_is_answered()
    {
        await _registry.RegisterAsync(new EntityDescriptor { Id = "audio_mute", Name = "Mute", Kind = EntityKind.Switch });
        await _registry.RegisterAsync(new EntityDescriptor { Id = "volume_level", Name = "Volume", Kind = EntityKind.Number, Min = 0, Max = 100 });
        await using var commands = _bus.Subscribe<ActionCommand>();
        await using var engine = CreateEngine();
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => _homeAssistant.IsIntegrationConnected);

        await _homeAssistant.SendIntegrationCommandAsync(new { command_id = "c1", command = "press", entity = "lock_screen" });
        await _homeAssistant.SendIntegrationCommandAsync(new { command_id = "c2", command = "set", entity = "audio_mute", value = true });
        await _homeAssistant.SendIntegrationCommandAsync(new { command_id = "c3", command = "set", entity = "volume_level", value = 30.0 });

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

        Assert.Equal(("lock_screen", null), (received[0].ActionId, received[0].Value));
        Assert.Equal(("audio_mute", "on"), (received[1].ActionId, received[1].Value));
        Assert.Equal(("volume_level", "30"), (received[2].ActionId, received[2].Value));
        Assert.All(received, command => Assert.Equal("websocket (Home)", command.Origin));

        await WaitUntilAsync(() => Results().Count == 3);
        Assert.All(Results(), result => Assert.True(result.GetProperty("success").GetBoolean()));
        Assert.Equal(["c1", "c2", "c3"], Results().Select(result => result.GetProperty("command_id").GetString()));
        Assert.All(Results(), result => Assert.Equal("testpc", result.GetProperty("device_id").GetString()));
    }

    [Fact]
    public async Task A_command_that_cannot_be_taken_is_refused_with_the_reason()
    {
        await _registry.RegisterAsync(new EntityDescriptor { Id = "volume_level", Name = "Volume", Kind = EntityKind.Number, Min = 0, Max = 100 });
        await _registry.RegisterAsync(new EntityDescriptor { Id = "shutdown", Name = "Shut down", Kind = EntityKind.Button });
        await using var commands = _bus.Subscribe<ActionCommand>();
        await using var engine = CreateEngine(filter: new EntityFilter(["shutdown"]));
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => _homeAssistant.IsIntegrationConnected);

        // Switched off in HADA, not there at all, a sensor, a value out of range, and something from a later version.
        await _homeAssistant.SendIntegrationCommandAsync(new { command_id = "c1", command = "press", entity = "shutdown" });
        await _homeAssistant.SendIntegrationCommandAsync(new { command_id = "c2", command = "press", entity = "no_such_entity" });
        await _homeAssistant.SendIntegrationCommandAsync(new { command_id = "c3", command = "set", entity = "cpu_load", value = 1 });
        await _homeAssistant.SendIntegrationCommandAsync(new { command_id = "c4", command = "set", entity = "volume_level", value = 250 });
        await _homeAssistant.SendIntegrationCommandAsync(new { command_id = "c5", command = "teleport", entity = "lock_screen" });

        await WaitUntilAsync(() => Results().Count == 5);
        Assert.All(Results(), result =>
        {
            Assert.False(result.GetProperty("success").GetBoolean());
            Assert.False(string.IsNullOrEmpty(result.GetProperty("error").GetString()));
        });
        Assert.False(commands.TryRead(out _));
    }

    [Fact]
    public async Task A_notification_sent_through_the_integration_is_shown_with_all_it_carries()
    {
        await _registry.RegisterAsync(new EntityDescriptor { Id = "notification", Name = "Notification", Kind = EntityKind.Notify });
        await using var commands = _bus.Subscribe<ActionCommand>();
        await using var engine = CreateEngine();
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => _homeAssistant.IsIntegrationConnected);
        Assert.Equal("notify", _homeAssistant.IntegrationEntity("notification")!.Descriptor.GetProperty("kind").GetString());

        await _homeAssistant.SendIntegrationCommandAsync(new
        {
            command_id = "c1",
            command = "notify",
            entity = "notification",
            message = "Someone is at the door.",
            title = "Front door",
            data = new
            {
                image = "/api/camera_proxy/camera.door?authSig=signed",
                tag = "door",
                sticky = true,
                actions = new[] { new { action = "open_door", title = "Open" } },
            },
        });
        await _homeAssistant.SendIntegrationCommandAsync(new { command_id = "c2", command = "notify", entity = "notification", title = "No message" });

        using var timeout = new CancellationTokenSource(Timeout);
        ActionCommand? shown = null;
        await foreach (var command in commands.ReadAllAsync(timeout.Token))
        {
            shown = command;
            break;
        }

        Assert.Equal(("notification", "Someone is at the door."), (shown!.ActionId, shown.Value));
        Assert.Equal("websocket (Home)", shown.Origin);
        Assert.Equal("Front door", shown.GetParameter(NotificationContent.Title));
        Assert.Equal("door", shown.GetParameter(NotificationContent.Tag));
        Assert.Equal(NotificationContent.True, shown.GetParameter(NotificationContent.Sticky));
        Assert.Equal(["open_door"], NotificationContent.ParseButtons(shown.GetParameter(NotificationContent.Actions)).Select(button => button.Action));

        // The integration signed the picture's address; it is only made whole here, not signed again.
        Assert.Equal(
            new Uri(_homeAssistant.BaseUrl, "api/camera_proxy/camera.door?authSig=signed").AbsoluteUri,
            shown.GetParameter(NotificationContent.Image));
        Assert.DoesNotContain(_homeAssistant.SocketMessages, m => m.GetProperty("type").GetString() == "auth/sign_path");

        await WaitUntilAsync(() => Results().Count == 2);
        Assert.True(Results()[0].GetProperty("success").GetBoolean());
        Assert.False(Results()[1].GetProperty("success").GetBoolean());
        Assert.False(commands.TryRead(out _));
    }

    [Fact]
    public async Task What_happens_on_the_computer_is_told_to_the_integration_which_fires_the_event_itself()
    {
        await _registry.RegisterAsync(new EntityDescriptor { Id = "toggle_lamp", Name = "Toggle lamp", Kind = EntityKind.Trigger });
        await using var engine = CreateEngine();
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => _homeAssistant.IsIntegrationConnected);
        Assert.Equal("event", _homeAssistant.IntegrationEntity("toggle_lamp")!.Descriptor.GetProperty("kind").GetString());

        await _bus.PublishAsync(new DeviceEvent { Name = DeviceEvent.QuickAction, Value = "toggle_lamp" });
        await _bus.PublishAsync(new DeviceEvent { Name = DeviceEvent.NotificationAction, Value = "open_door", Target = "websocket (Home)" });
        await _bus.PublishAsync(new DeviceEvent { Name = DeviceEvent.NotificationAction, Value = "ignore", Target = "mqtt" });

        await WaitUntilAsync(() => Events().Count == 2);
        Assert.Equal(
            [("quick_action", "toggle_lamp"), ("notification_action", "open_door")],
            Events().Select(e => (e.GetProperty("name").GetString(), e.GetProperty("value").GetString())));
        Assert.All(Events(), e => Assert.Equal("testpc", e.GetProperty("device_id").GetString()));

        // Not fired a second time by HADA, which would also need an administrator's token.
        Assert.DoesNotContain(_homeAssistant.SocketMessages, m => m.GetProperty("type").GetString() == "fire_event");

        List<System.Text.Json.JsonElement> Events() =>
            [.. _homeAssistant.SocketMessages.Where(m => m.GetProperty("type").GetString() == "hada/event")];
    }

    private List<System.Text.Json.JsonElement> Results() =>
        [.. _homeAssistant.SocketMessages.Where(m => m.GetProperty("type").GetString() == "hada/command_result")];

    [Fact]
    public async Task An_entity_whose_source_is_away_is_unavailable_and_keeps_what_it_said()
    {
        await using var engine = CreateEngine();
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => _homeAssistant.IsIntegrationConnected);
        await _bus.PublishAsync(new TelemetryEvent { SensorId = "cpu_load", State = "14" });
        await WaitUntilAsync(() => _homeAssistant.IntegrationEntity("cpu_load")?.State == "14");

        await _registry.SetAvailabilityAsync("cpu_load", isAvailable: false);
        await WaitUntilAsync(() => _homeAssistant.IntegrationEntity("cpu_load")?.Available == false);
        Assert.Equal("14", _homeAssistant.IntegrationEntity("cpu_load")!.State);

        await _registry.SetAvailabilityAsync("cpu_load", isAvailable: true);
        await WaitUntilAsync(() => _homeAssistant.IntegrationEntity("cpu_load")?.Available == true);
    }

    [Fact]
    public async Task Entities_that_come_and_go_change_the_list()
    {
        await using var engine = CreateEngine();
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => _homeAssistant.IsIntegrationConnected);

        await _registry.RegisterAsync(new EntityDescriptor { Id = "gpu_load", Name = "GPU load", Kind = EntityKind.Sensor });
        await _registry.RegisterAsync(new EntityDescriptor { Id = "memory_usage", Name = "Memory", Kind = EntityKind.Sensor });
        await _registry.UnregisterAsync("display_on");
        await WaitUntilAsync(() => _homeAssistant.IntegrationEntityIds.SequenceEqual(["cpu_load", "gpu_load", "lock_screen", "memory_usage"]));

        // Registered in a row, sent as one list.
        Assert.Single(_homeAssistant.SocketMessages, m => m.GetProperty("type").GetString() == "hada/entities");
    }

    [Fact]
    public async Task Entities_switched_off_are_not_listed()
    {
        await using var engine = CreateEngine(filter: new EntityFilter(["display_on"]));
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => _homeAssistant.IsIntegrationConnected);

        Assert.Equal(["cpu_load", "lock_screen"], _homeAssistant.IntegrationEntityIds);
    }

    [Theory]
    [InlineData(FakeIntegration.Missing, HaWebSocketEngine.IssueIntegrationMissing)]
    [InlineData(FakeIntegration.NotSetUp, HaWebSocketEngine.IssueIntegrationNotSetUp)]
    public async Task Without_the_integration_the_connection_stays_and_says_what_is_missing(FakeIntegration integration, string issue)
    {
        _homeAssistant.Integration = integration;
        await using var engine = CreateEngine();
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => engine.Issue == issue);

        Assert.Equal(EngineConnectionState.Connected, engine.State);
        Assert.False(_homeAssistant.IsIntegrationConnected);

        // Set up meanwhile, without Home Assistant restarting: found at the next try.
        await _bus.PublishAsync(new TelemetryEvent { SensorId = "cpu_load", State = "5" });
        _homeAssistant.Integration = FakeIntegration.SetUp;
        await WaitUntilAsync(() => _homeAssistant.IntegrationEntity("cpu_load")?.State == "5");

        Assert.Null(engine.Issue);
    }

    [Fact]
    public async Task An_integration_that_was_reloaded_is_connected_to_again()
    {
        await using var engine = CreateEngine();
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => _homeAssistant.IsIntegrationConnected);

        _homeAssistant.ReloadIntegration();
        await _bus.PublishAsync(new TelemetryEvent { SensorId = "cpu_load", State = "7" });

        await WaitUntilAsync(() => _homeAssistant.IsIntegrationConnected && _homeAssistant.IntegrationEntity("cpu_load")?.State == "7");
        Assert.Equal(2, _homeAssistant.SocketMessages.Count(m => m.GetProperty("type").GetString() == "hada/connect"));
    }

    [Fact]
    public async Task The_token_of_an_ordinary_user_is_enough()
    {
        _homeAssistant.IsAdministrator = false;
        await using var engine = CreateEngine();
        await engine.StartAsync(CancellationToken.None);

        await WaitUntilAsync(() => _homeAssistant.IsIntegrationConnected);
        Assert.Equal(EngineConnectionState.Connected, engine.State);
    }

    [Fact]
    public async Task Notifications_through_mobile_app_work_beside_the_integration()
    {
        await _registry.RegisterAsync(new EntityDescriptor { Id = "notification", Name = "Notification", Kind = EntityKind.Notify });
        await using var commands = _bus.Subscribe<ActionCommand>();
        await using var engine = CreateEngine(notifications: true);
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => _homeAssistant.IsIntegrationConnected && _homeAssistant.HasPushChannel);

        await _homeAssistant.SendNotificationAsync(new { message = "Hello", hass_confirm_id = "c1" });

        using var timeout = new CancellationTokenSource(Timeout);
        await foreach (var command in commands.ReadAllAsync(timeout.Token))
        {
            Assert.Equal("Hello", command.Value);
            break;
        }

        // The sensors go to the integration, not to the mobile_app device as well.
        Assert.DoesNotContain(_homeAssistant.WebhookCalls, call => call.Body.GetProperty("type").GetString() == "register_sensor");
    }

    private HaWebSocketEngine CreateEngine(bool notifications = false, IEntityFilter? filter = null) => new(
        _bus,
        _registry,
        Options.Create(new HaWebSocketOptions
        {
            Id = "home",
            Name = "Home",
            BaseUrl = _homeAssistant.BaseUrl.ToString(),
            AccessToken = FakeHomeAssistant.ValidToken,
            DeviceId = "TestPC",
            DeviceName = "Test PC",
            Notifications = notifications,
            SensorMode = HomeAssistantSensorMode.Integration,
            IntegrationRetryInterval = TimeSpan.FromMilliseconds(100),
            MinReconnectDelay = TimeSpan.FromMilliseconds(50),
        }),
        NullLogger<HaWebSocketEngine>.Instance,
        filter,
        new InMemoryMobileAppRegistrationStore());

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }
}

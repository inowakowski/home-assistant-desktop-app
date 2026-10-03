using System.Text.Json;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Messaging;
using HADA.Core.Models;
using HADA.Engine.WebSocket;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HADA.Tests.HomeAssistant;

/// <summary>
/// The WebSocket engine as one of Home Assistant's mobile_app devices: registering once, receiving what
/// <c>notify.mobile_app_*</c> sends, and reporting pressed buttons the way a phone does.
/// </summary>
public sealed class MobileAppTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly FakeHomeAssistant _homeAssistant = new();
    private readonly ChannelEventBus _bus = new();
    private readonly EntityRegistry _registry;
    private readonly InMemoryMobileAppRegistrationStore _registrations = new();

    public MobileAppTests() => _registry = new EntityRegistry(_bus);

    public async Task InitializeAsync()
    {
        await _registry.RegisterAsync(new EntityDescriptor { Id = "cpu_load", Name = "CPU load", Kind = EntityKind.Sensor });
        await _registry.RegisterAsync(new EntityDescriptor { Id = "notification", Name = "Notification", Kind = EntityKind.Notify });
    }

    public async Task DisposeAsync()
    {
        await _homeAssistant.DisposeAsync();
        await _bus.DisposeAsync();
    }

    [Fact]
    public async Task Registers_as_a_device_that_takes_notifications_over_the_connection()
    {
        await using var engine = CreateEngine();
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => _homeAssistant.HasPushChannel);

        var registration = Assert.Single(_homeAssistant.Registrations);
        Assert.Equal("testpc", registration.GetProperty("device_id").GetString());
        Assert.Equal("Test PC", registration.GetProperty("device_name").GetString());
        Assert.Equal("HADA", registration.GetProperty("app_name").GetString());
        Assert.False(registration.GetProperty("supports_encryption").GetBoolean());
        Assert.True(registration.GetProperty("app_data").GetProperty("push_websocket_channel").GetBoolean());

        var channel = Assert.Single(_homeAssistant.SocketMessages, m => m.GetProperty("type").GetString() == "mobile_app/push_notification_channel");
        Assert.Equal(FakeHomeAssistant.WebhookIdOf(1), channel.GetProperty("webhook_id").GetString());
        Assert.True(channel.GetProperty("support_confirm").GetBoolean());

        var saved = _registrations.Load("home");
        Assert.Equal(FakeHomeAssistant.WebhookIdOf(1), saved?.WebhookId);
        Assert.DoesNotContain(FakeHomeAssistant.WebhookIdOf(1), saved!.ToString());
    }

    [Fact]
    public async Task Without_the_switch_nothing_is_registered()
    {
        await using var engine = CreateEngine(notifications: false);
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => engine.State == EngineConnectionState.Connected);

        Assert.Empty(_homeAssistant.Registrations);
        Assert.False(_homeAssistant.HasPushChannel);
    }

    [Fact]
    public async Task A_saved_registration_is_used_again_instead_of_registering_twice()
    {
        await using (var first = CreateEngine())
        {
            await first.StartAsync(CancellationToken.None);
            await WaitUntilAsync(() => _homeAssistant.HasPushChannel);
        }

        await WaitUntilAsync(() => !_homeAssistant.HasPushChannel);
        await using var second = CreateEngine();
        await second.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => _homeAssistant.HasPushChannel);

        Assert.Single(_homeAssistant.Registrations);

        // Home Assistant is told what version and name the device has now; the access token stays out of it.
        var update = Assert.Single(Calls("update_registration"));
        Assert.Equal(FakeHomeAssistant.WebhookIdOf(1), update.WebhookId);
        Assert.Null(update.Authorization);
        Assert.Equal("update_registration", update.Body.GetProperty("type").GetString());
        Assert.Equal("Test PC", update.Body.GetProperty("data").GetProperty("device_name").GetString());
    }

    [Fact]
    public async Task A_device_deleted_in_home_assistant_is_registered_again()
    {
        await using (var first = CreateEngine())
        {
            await first.StartAsync(CancellationToken.None);
            await WaitUntilAsync(() => _homeAssistant.HasPushChannel);
        }

        await WaitUntilAsync(() => !_homeAssistant.HasPushChannel);
        _homeAssistant.DeleteDevice(FakeHomeAssistant.WebhookIdOf(1));
        await using var second = CreateEngine();
        await second.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => _homeAssistant.HasPushChannel);

        Assert.Equal(2, _homeAssistant.Registrations.Length);
        Assert.Equal(FakeHomeAssistant.WebhookIdOf(2), _registrations.Load("home")?.WebhookId);
    }

    [Fact]
    public async Task A_registration_made_with_another_home_assistant_or_device_id_is_not_used()
    {
        _registrations.Save("home", new MobileAppRegistration("someone-elses", "http://other.local:8123/", "testpc"));
        await using var engine = CreateEngine();
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => _homeAssistant.HasPushChannel);

        Assert.Single(_homeAssistant.Registrations);
        Assert.Empty(Calls("update_registration"));
        Assert.DoesNotContain(_homeAssistant.WebhookCalls, call => call.WebhookId == "someone-elses");
        Assert.Equal(FakeHomeAssistant.WebhookIdOf(1), _registrations.Load("home")?.WebhookId);
    }

    [Fact]
    public async Task A_notification_is_confirmed_and_shown_with_its_title_picture_and_buttons()
    {
        await using var commands = _bus.Subscribe<ActionCommand>();
        await using var engine = CreateEngine();
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => engine.State == EngineConnectionState.Connected && _homeAssistant.HasPushChannel);

        await _homeAssistant.SendNotificationAsync(new
        {
            message = "Someone is at the door.",
            title = "Front door",
            hass_confirm_id = "confirm-1",
            data = new
            {
                image = "/local/door.jpg",
                actions = new[] { new { action = "open_door", title = "Open" }, new { action = "ignore", title = "Ignore" } },
            },
        });

        var command = await NextAsync(commands);
        Assert.Equal(("notification", "Someone is at the door."), (command.ActionId, command.Value));
        Assert.Equal("websocket (Home)", command.Origin);
        Assert.Equal("Front door", command.GetParameter(NotificationContent.Title));
        Assert.Equal(new Uri(_homeAssistant.BaseUrl, "local/door.jpg").AbsoluteUri, command.GetParameter(NotificationContent.Image));
        Assert.Equal(
            ["open_door", "ignore"],
            NotificationContent.ParseButtons(command.GetParameter(NotificationContent.Actions)).Select(button => button.Action));

        await WaitUntilAsync(() => Array.Exists(
            _homeAssistant.SocketMessages, m => m.GetProperty("type").GetString() == "mobile_app/push_notification_confirm"));
        var confirm = Assert.Single(_homeAssistant.SocketMessages, m => m.GetProperty("type").GetString() == "mobile_app/push_notification_confirm");
        Assert.Equal("confirm-1", confirm.GetProperty("confirm_id").GetString());
        Assert.Equal(FakeHomeAssistant.WebhookIdOf(1), confirm.GetProperty("webhook_id").GetString());
    }

    [Fact]
    public async Task What_is_not_a_notification_to_show_is_confirmed_all_the_same()
    {
        await using var commands = _bus.Subscribe<ActionCommand>();
        await using var engine = CreateEngine();
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => engine.State == EngineConnectionState.Connected && _homeAssistant.HasPushChannel);

        // Taking a notification back without saying which, and one without a message.
        await _homeAssistant.SendNotificationAsync(new { message = "clear_notification", hass_confirm_id = "c1" });
        await _homeAssistant.SendNotificationAsync(new { title = "No message", hass_confirm_id = "c2" });
        await _homeAssistant.SendNotificationAsync(new { message = "Shown", hass_confirm_id = "c3" });

        Assert.Equal("Shown", (await NextAsync(commands)).Value);
        Assert.False(commands.TryRead(out _));
        await WaitUntilAsync(() => Confirmed().Count == 3);
        Assert.Equal(["c1", "c2", "c3"], Confirmed());

        List<string?> Confirmed() =>
        [
            .. _homeAssistant.SocketMessages
                .Where(m => m.GetProperty("type").GetString() == "mobile_app/push_notification_confirm")
                .Select(m => m.GetProperty("confirm_id").GetString()),
        ];
    }

    [Fact]
    public async Task What_a_phone_is_told_about_a_notification_is_understood_here_too()
    {
        await using var commands = _bus.Subscribe<ActionCommand>();
        await using var engine = CreateEngine();
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => engine.State == EngineConnectionState.Connected && _homeAssistant.HasPushChannel);

        await _homeAssistant.SendNotificationAsync(new
        {
            message = "The washing machine is done.",
            data = new
            {
                tag = "laundry",
                clickAction = "/lovelace/laundry",
                persistent = true,
                importance = "low",
                actions = new object[]
                {
                    new { action = "URI", title = "Open the camera", uri = "/lovelace/cameras" },
                    new { action = "done", title = "Done" },
                },
            },
        });
        await _homeAssistant.SendNotificationAsync(new { message = "clear_notification", data = new { tag = "laundry" } });

        var shown = await NextAsync(commands);
        Assert.Equal("laundry", shown.GetParameter(NotificationContent.Tag));
        Assert.Equal(new Uri(_homeAssistant.BaseUrl, "lovelace/laundry").AbsoluteUri, shown.GetParameter(NotificationContent.Url));
        Assert.Equal(NotificationContent.True, shown.GetParameter(NotificationContent.Sticky));
        Assert.Equal(NotificationContent.True, shown.GetParameter(NotificationContent.Silent));
        Assert.Null(shown.GetParameter(NotificationContent.Clear));
        Assert.Equal(
            [
                new NotificationButton("URI", "Open the camera", new Uri(_homeAssistant.BaseUrl, "lovelace/cameras").AbsoluteUri),
                new NotificationButton("done", "Done"),
            ],
            NotificationContent.ParseButtons(shown.GetParameter(NotificationContent.Actions)));

        // Taken back in the order it was sent in: after the notification, not before.
        var cleared = await NextAsync(commands);
        Assert.Equal("laundry", cleared.GetParameter(NotificationContent.Tag));
        Assert.Equal(NotificationContent.True, cleared.GetParameter(NotificationContent.Clear));
    }

    [Fact]
    public async Task A_picture_that_needs_signing_in_gets_an_address_that_is_good_without()
    {
        await using var commands = _bus.Subscribe<ActionCommand>();
        await using var engine = CreateEngine();
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => engine.State == EngineConnectionState.Connected && _homeAssistant.HasPushChannel);

        await _homeAssistant.SendNotificationAsync(new { message = "Camera", data = new { image = "/api/camera_proxy/camera.door?width=640" } });
        await _homeAssistant.SendNotificationAsync(new { message = "Public", data = new { image = "/local/door.jpg" } });
        await _homeAssistant.SendNotificationAsync(new { message = "Elsewhere", data = new { image = "https://example.com/api/a.png" } });

        Assert.Equal(
            new Uri(_homeAssistant.BaseUrl, "api/camera_proxy/camera.door?width=640&authSig=signed").AbsoluteUri,
            (await NextAsync(commands)).GetParameter(NotificationContent.Image));
        Assert.Equal(new Uri(_homeAssistant.BaseUrl, "local/door.jpg").AbsoluteUri, (await NextAsync(commands)).GetParameter(NotificationContent.Image));
        Assert.Equal("https://example.com/api/a.png", (await NextAsync(commands)).GetParameter(NotificationContent.Image));

        // Only the path is sent to be signed, and only the one at this Home Assistant; the access token goes nowhere.
        var signed = Assert.Single(_homeAssistant.SocketMessages, m => m.GetProperty("type").GetString() == "auth/sign_path");
        Assert.Equal("/api/camera_proxy/camera.door?width=640", signed.GetProperty("path").GetString());
    }

    [Fact]
    public void The_same_is_understood_in_what_is_published_through_mqtt()
    {
        using var json = JsonDocument.Parse("""
            {
              "message": "clear_notification",
              "tag": "laundry",
              "url": "https://ha.example.com/lovelace/laundry",
              "sticky": "true",
              "silent": true,
              "actions": [{ "action": "open", "title": "Open", "uri": "javascript:alert(1)" }]
            }
            """);

        Assert.True(NotificationContent.TryRead(json.RootElement, out _, out var parameters));

        Assert.Equal("laundry", parameters![NotificationContent.Tag]);
        Assert.Equal(NotificationContent.True, parameters[NotificationContent.Clear]);
        Assert.Equal("https://ha.example.com/lovelace/laundry", parameters[NotificationContent.Url]);
        Assert.Equal(NotificationContent.True, parameters[NotificationContent.Sticky]);
        Assert.Equal(NotificationContent.True, parameters[NotificationContent.Silent]);

        // An address a browser would not open is not one a button may have; the button is then an ordinary one.
        Assert.Equal([new NotificationButton("open", "Open")], NotificationContent.ParseButtons((string)parameters[NotificationContent.Actions]!));
    }

    [Theory]
    [InlineData("""{ "message": "clear_notification" }""")]
    [InlineData("""{ "message": "Hello", "url": "file:///C:/Windows/win.ini", "sticky": "no", "silent": 1, "tag": 5 }""")]
    public void Without_a_tag_nothing_is_taken_back_and_what_is_wrong_is_left_out(string notification)
    {
        using var json = JsonDocument.Parse(notification);

        Assert.True(NotificationContent.TryRead(json.RootElement, out _, out var parameters));

        Assert.Null(parameters);
    }

    [Fact]
    public async Task A_pressed_button_is_reported_the_way_a_phone_reports_it()
    {
        await using var engine = CreateEngine();
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => engine.State == EngineConnectionState.Connected && _homeAssistant.HasPushChannel);

        // Answers for another Home Assistant are not this one's to pass on.
        await _bus.PublishAsync(new DeviceEvent { Name = DeviceEvent.NotificationAction, Value = "ignore", Target = "mqtt" });
        await _bus.PublishAsync(new DeviceEvent { Name = DeviceEvent.NotificationAction, Value = "open_door", Target = "websocket (Home)" });

        await WaitUntilAsync(() => Calls("fire_event").Count > 0);
        var call = Assert.Single(Calls("fire_event"));
        Assert.Equal(FakeHomeAssistant.WebhookIdOf(1), call.WebhookId);
        Assert.Null(call.Authorization);
        Assert.Equal("fire_event", call.Body.GetProperty("type").GetString());
        var data = call.Body.GetProperty("data");
        Assert.Equal("mobile_app_notification_action", data.GetProperty("event_type").GetString());
        Assert.Equal("open_door", data.GetProperty("event_data").GetProperty("action").GetString());
        Assert.Equal("testpc", data.GetProperty("event_data").GetProperty("device_id").GetString());

        // The event of the WebSocket engine itself is still fired, for automations written for it.
        await WaitUntilAsync(() => Array.Exists(_homeAssistant.SocketMessages, m => m.GetProperty("type").GetString() == "fire_event"));
    }

    [Fact]
    public async Task A_token_that_is_no_administrators_still_gets_notifications()
    {
        _homeAssistant.IsAdministrator = false;
        await using var commands = _bus.Subscribe<ActionCommand>();
        await using var engine = CreateEngine();
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => engine.State == EngineConnectionState.Connected && _homeAssistant.HasPushChannel);

        await _homeAssistant.SendNotificationAsync(new { message = "Hello" });

        Assert.Equal("Hello", (await NextAsync(commands)).Value);

        var test = await HaWebSocketEngine.TestConnectionAsync(CreateOptions(notifications: true, HomeAssistantSensorMode.States), CancellationToken.None);
        Assert.True(test.Success, test.Message);
        Assert.Contains("not an administrator", test.Message);
    }

    [Fact]
    public async Task Without_notifications_a_token_that_is_no_administrators_is_refused_as_before()
    {
        _homeAssistant.IsAdministrator = false;

        var test = await HaWebSocketEngine.TestConnectionAsync(CreateOptions(notifications: false, HomeAssistantSensorMode.States), CancellationToken.None);

        Assert.False(test.Success);
    }

    [Fact]
    public async Task A_home_assistant_without_mobile_app_keeps_the_connection_and_only_lacks_notifications()
    {
        _homeAssistant.HasMobileApp = false;
        await using var engine = CreateEngine();
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => engine.State == EngineConnectionState.Connected);

        await _bus.PublishAsync(new TelemetryEvent { SensorId = "cpu_load", State = "12.5" });

        await WaitUntilAsync(() => Array.Exists(_homeAssistant.StateWrites, write => write.EntityId == "sensor.testpc_cpu_load"));
        Assert.False(_homeAssistant.HasPushChannel);
        Assert.Null(_registrations.Load("home"));
    }

    [Fact]
    public async Task With_sensors_switched_off_no_states_are_written_and_earlier_ones_are_removed()
    {
        await using var engine = CreateEngine(sensors: HomeAssistantSensorMode.Off);
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => Array.Exists(
            _homeAssistant.StateWrites, write => write.Method == "DELETE" && write.EntityId == "sensor.testpc_cpu_load"));

        await _bus.PublishAsync(new TelemetryEvent { SensorId = "cpu_load", State = "12.5" });
        await Task.Delay(300);
        await engine.StopAsync(CancellationToken.None);

        Assert.DoesNotContain(_homeAssistant.StateWrites, write => write.Method == "POST");
    }

    [Fact]
    public async Task Sensors_become_entities_of_the_device_with_their_kind_of_value()
    {
        await _registry.RegisterAsync(new EntityDescriptor
        {
            Id = "cpu_load", Name = "CPU load", Kind = EntityKind.Sensor, UnitOfMeasurement = "%", StateClass = "measurement", Icon = "mdi:cpu-64-bit",
        });
        await _registry.RegisterAsync(new EntityDescriptor { Id = "display_on", Name = "Display", Kind = EntityKind.BinarySensor });
        await _registry.RegisterAsync(new EntityDescriptor { Id = "audio_mute", Name = "Mute", Kind = EntityKind.Switch });
        await _registry.RegisterAsync(new EntityDescriptor { Id = "active_window", Name = "Active window", Kind = EntityKind.Sensor });
        await _registry.RegisterAsync(new EntityDescriptor { Id = "last_boot", Name = "Last boot", Kind = EntityKind.Sensor, DeviceClass = "timestamp" });
        await using var engine = CreateEngine(notifications: false, HomeAssistantSensorMode.Entities);
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => engine.State == EngineConnectionState.Connected);

        await _bus.PublishAsync(new TelemetryEvent { SensorId = "cpu_load", State = "12.5", Attributes = new Dictionary<string, object?> { ["cores"] = 8 } });
        await _bus.PublishAsync(new TelemetryEvent { SensorId = "display_on", State = BinaryState.On });
        await _bus.PublishAsync(new TelemetryEvent { SensorId = "audio_mute", State = BinaryState.Off });
        await _bus.PublishAsync(new TelemetryEvent { SensorId = "active_window", State = "42" });
        await _bus.PublishAsync(new TelemetryEvent { SensorId = "last_boot", State = "2026-10-03T08:00:00+00:00" });
        await WaitUntilAsync(() => _homeAssistant.SensorEntity("last_boot") is not null);

        var cpu = _homeAssistant.SensorEntity("cpu_load")!.Value;
        Assert.Equal("sensor", cpu.GetProperty("type").GetString());
        Assert.Equal("CPU load", cpu.GetProperty("name").GetString());
        Assert.Equal(12.5, cpu.GetProperty("state").GetDouble());
        Assert.Equal("%", cpu.GetProperty("unit_of_measurement").GetString());
        Assert.Equal("measurement", cpu.GetProperty("state_class").GetString());
        Assert.Equal("mdi:cpu-64-bit", cpu.GetProperty("icon").GetString());
        Assert.Equal(8, cpu.GetProperty("attributes").GetProperty("cores").GetInt32());

        Assert.Equal("binary_sensor", _homeAssistant.SensorEntity("display_on")!.Value.GetProperty("type").GetString());
        Assert.True(_homeAssistant.SensorEntity("display_on")!.Value.GetProperty("state").GetBoolean());
        Assert.False(_homeAssistant.SensorEntity("audio_mute")!.Value.GetProperty("state").GetBoolean());

        // A text that happens to read as a number stays a text, and a time stays as it was written.
        Assert.Equal("42", _homeAssistant.SensorEntity("active_window")!.Value.GetProperty("state").GetString());
        Assert.Equal("2026-10-03T08:00:00+00:00", _homeAssistant.SensorEntity("last_boot")!.Value.GetProperty("state").GetString());
        Assert.Equal("timestamp", _homeAssistant.SensorEntity("last_boot")!.Value.GetProperty("device_class").GetString());

        // Nothing is written as a state, and without notifications there is no channel and no action to send them.
        Assert.DoesNotContain(_homeAssistant.StateWrites, write => write.Method == "POST");
        Assert.False(_homeAssistant.HasPushChannel);
        Assert.False(Assert.Single(_homeAssistant.Registrations).GetProperty("app_data").TryGetProperty("push_websocket_channel", out _));
    }

    [Fact]
    public async Task A_sensor_entity_is_registered_once_and_then_only_updated()
    {
        await using var engine = CreateEngine(sensors: HomeAssistantSensorMode.Entities);
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => engine.State == EngineConnectionState.Connected);

        await _bus.PublishAsync(new TelemetryEvent { SensorId = "cpu_load", State = "1" });
        await _bus.PublishAsync(new TelemetryEvent { SensorId = "cpu_load", State = "2" });
        await WaitUntilAsync(() => SensorState("cpu_load") == "2");

        Assert.Single(Calls("register_sensor"));

        // Deleted in Home Assistant: the next reading brings it back.
        _homeAssistant.DeleteSensorEntity("cpu_load");
        await _bus.PublishAsync(new TelemetryEvent { SensorId = "cpu_load", State = "3" });
        await WaitUntilAsync(() => SensorState("cpu_load") == "3");

        Assert.Equal(2, Calls("register_sensor").Count);
    }

    [Fact]
    public async Task A_sensor_entity_whose_source_is_away_or_that_is_switched_off_is_unavailable()
    {
        await _registry.RegisterAsync(new EntityDescriptor { Id = "gpu_load", Name = "GPU load", Kind = EntityKind.Sensor });
        await using (var first = CreateEngine(sensors: HomeAssistantSensorMode.Entities))
        {
            await first.StartAsync(CancellationToken.None);
            await WaitUntilAsync(() => first.State == EngineConnectionState.Connected);
            await _bus.PublishAsync(new TelemetryEvent { SensorId = "cpu_load", State = "1" });
            await _bus.PublishAsync(new TelemetryEvent { SensorId = "gpu_load", State = "2" });
            await WaitUntilAsync(() => SensorState("gpu_load") == "2");

            await _registry.SetAvailabilityAsync("cpu_load", isAvailable: false);
            await WaitUntilAsync(() => SensorState("cpu_load") == "unavailable");
            await _registry.SetAvailabilityAsync("cpu_load", isAvailable: true);
            await WaitUntilAsync(() => SensorState("cpu_load") == "1");

            // Stopping leaves nothing looking current.
            await first.StopAsync(CancellationToken.None);
            Assert.Equal("unavailable", SensorState("cpu_load"));
            Assert.Equal("unavailable", SensorState("gpu_load"));
        }

        // Switched off on the Entities page: the entity Home Assistant has stays unavailable, and none is made for
        // a sensor it never had.
        await _registry.RegisterAsync(new EntityDescriptor { Id = "memory_usage", Name = "Memory", Kind = EntityKind.Sensor });
        await using var second = CreateEngine(sensors: HomeAssistantSensorMode.Entities, filter: new EntityFilter(["gpu_load", "memory_usage"]));
        await second.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => second.State == EngineConnectionState.Connected);
        await _bus.PublishAsync(new TelemetryEvent { SensorId = "gpu_load", State = "5" });
        await _bus.PublishAsync(new TelemetryEvent { SensorId = "cpu_load", State = "6" });
        await WaitUntilAsync(() => SensorState("cpu_load") == "6");

        Assert.Equal("unavailable", SensorState("gpu_load"));
        Assert.Null(_homeAssistant.SensorEntity("memory_usage"));
    }

    [Fact]
    public async Task Sensor_entities_left_from_before_are_unavailable_once_sensors_go_another_way()
    {
        await using (var first = CreateEngine(sensors: HomeAssistantSensorMode.Entities))
        {
            await first.StartAsync(CancellationToken.None);
            await WaitUntilAsync(() => first.State == EngineConnectionState.Connected);
            await _bus.PublishAsync(new TelemetryEvent { SensorId = "cpu_load", State = "1" });
            await WaitUntilAsync(() => SensorState("cpu_load") == "1");
        }

        // Still registered, for the notifications; the sensors are off for this connection now.
        _homeAssistant.DeleteSensorEntity("cpu_load");
        await using var second = CreateEngine(sensors: HomeAssistantSensorMode.Off);
        await second.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => second.State == EngineConnectionState.Connected && _homeAssistant.HasPushChannel);
        await _bus.PublishAsync(new TelemetryEvent { SensorId = "cpu_load", State = "2" });
        await Task.Delay(300);

        Assert.Null(_homeAssistant.SensorEntity("cpu_load"));
        Assert.Single(Calls("register_sensor"));
    }

    [Fact]
    public async Task A_device_class_home_assistant_refuses_does_not_cost_the_sensor()
    {
        await _registry.RegisterAsync(new EntityDescriptor
        {
            Id = "odd", Name = "Odd", Kind = EntityKind.Sensor, DeviceClass = FakeHomeAssistant.UnknownDeviceClass,
        });
        await using var engine = CreateEngine(sensors: HomeAssistantSensorMode.Entities);
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => engine.State == EngineConnectionState.Connected);

        await _bus.PublishAsync(new TelemetryEvent { SensorId = "odd", State = "value" });
        await WaitUntilAsync(() => _homeAssistant.SensorEntity("odd") is not null);

        Assert.Equal(JsonValueKind.Null, _homeAssistant.SensorEntity("odd")!.Value.GetProperty("device_class").ValueKind);
    }

    [Fact]
    public async Task A_registration_home_assistant_never_heard_of_is_made_again_even_without_notifications()
    {
        await using (var first = CreateEngine(notifications: false, HomeAssistantSensorMode.Entities))
        {
            await first.StartAsync(CancellationToken.None);
            await WaitUntilAsync(() => first.State == EngineConnectionState.Connected);
        }

        _homeAssistant.ForgetDevice(FakeHomeAssistant.WebhookIdOf(1));
        await using var second = CreateEngine(notifications: false, HomeAssistantSensorMode.Entities);
        await second.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => second.State == EngineConnectionState.Connected);
        await _bus.PublishAsync(new TelemetryEvent { SensorId = "cpu_load", State = "1" });
        await WaitUntilAsync(() => SensorState("cpu_load") == "1");

        Assert.Equal(2, _homeAssistant.Registrations.Length);
        Assert.Equal(FakeHomeAssistant.WebhookIdOf(2), Calls("register_sensor").Last().WebhookId);
    }

    [Fact]
    public async Task Sensor_entities_work_with_a_token_that_is_no_administrators()
    {
        _homeAssistant.IsAdministrator = false;
        await using var engine = CreateEngine(notifications: false, HomeAssistantSensorMode.Entities);
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => engine.State == EngineConnectionState.Connected);

        await _bus.PublishAsync(new TelemetryEvent { SensorId = "cpu_load", State = "1" });

        await WaitUntilAsync(() => SensorState("cpu_load") == "1");
    }

    /// <summary>The state last sent for a sensor entity, as text whatever kind of value it is.</summary>
    private string? SensorState(string uniqueId) =>
        _homeAssistant.SensorEntity(uniqueId) is { } sensor && sensor.TryGetProperty("state", out var state)
            ? (state.ValueKind == JsonValueKind.String ? state.GetString() : state.GetRawText())
            : null;

    private List<WebhookCall> Calls(string type) =>
        [.. _homeAssistant.WebhookCalls.Where(call => call.Body.GetProperty("type").GetString() == type)];

    [Theory]
    [InlineData("/local/door.jpg", "http://ha.local:8123/local/door.jpg")]
    [InlineData("/api/camera_proxy/camera.door", "http://ha.local:8123/api/camera_proxy/camera.door")]
    [InlineData("https://example.com/a.png", "https://example.com/a.png")]
    [InlineData("//evil.example/a.png", null)]
    [InlineData("file:///C:/secret.png", null)]
    [InlineData("door.jpg", null)]
    public void The_picture_of_a_notification_may_be_a_path_at_its_home_assistant(string image, string? expected)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { message = "Hi", data = new { image } }));

        Assert.True(NotificationContent.TryReadMobileApp(json.RootElement, new Uri("http://ha.local:8123/"), out var message, out var parameters));

        Assert.Equal("Hi", message);
        Assert.Equal(expected, parameters?.GetValueOrDefault(NotificationContent.Image));
    }

    private HaWebSocketEngine CreateEngine(
        bool notifications = true, HomeAssistantSensorMode sensors = HomeAssistantSensorMode.States, IEntityFilter? filter = null) => new(
        _bus,
        _registry,
        Options.Create(CreateOptions(notifications, sensors)),
        NullLogger<HaWebSocketEngine>.Instance,
        filter,
        _registrations);

    private HaWebSocketOptions CreateOptions(bool notifications, HomeAssistantSensorMode sensors) => new()
    {
        Id = "home",
        Name = "Home",
        BaseUrl = _homeAssistant.BaseUrl.ToString(),
        AccessToken = FakeHomeAssistant.ValidToken,
        DeviceId = "TestPC",
        DeviceName = "Test PC",
        Notifications = notifications,
        SensorMode = sensors,
        MinReconnectDelay = TimeSpan.FromMilliseconds(50),
    };

    private static async Task<ActionCommand> NextAsync(IEventSubscription<ActionCommand> commands)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        await foreach (var command in commands.ReadAllAsync(timeout.Token))
        {
            return command;
        }

        throw new InvalidOperationException("The subscription ended without a command.");
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

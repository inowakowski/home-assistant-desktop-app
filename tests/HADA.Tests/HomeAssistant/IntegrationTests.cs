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
        await _registry.RegisterAsync(new EntityDescriptor { Id = "volume_level", Name = "Volume", Kind = EntityKind.Number, StateClass = "measurement" });
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

        // What reports a state is listed; a button has nothing to show until the integration can press it.
        Assert.Equal(["audio_mute", "cpu_load", "display_on", "volume_level"], _homeAssistant.IntegrationEntityIds);

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
        Assert.Equal("binary_sensor", _homeAssistant.IntegrationEntity("audio_mute")!.Descriptor.GetProperty("kind").GetString());
        Assert.Equal("sensor", _homeAssistant.IntegrationEntity("volume_level")!.Descriptor.GetProperty("kind").GetString());

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
        await WaitUntilAsync(() => _homeAssistant.IntegrationEntityIds.SequenceEqual(["cpu_load", "gpu_load", "memory_usage"]));

        // Registered in a row, sent as one list.
        Assert.Single(_homeAssistant.SocketMessages, m => m.GetProperty("type").GetString() == "hada/entities");
    }

    [Fact]
    public async Task Entities_switched_off_are_not_listed()
    {
        await using var engine = CreateEngine(filter: new EntityFilter(["display_on"]));
        await engine.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => _homeAssistant.IsIntegrationConnected);

        Assert.Equal(["cpu_load"], _homeAssistant.IntegrationEntityIds);
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

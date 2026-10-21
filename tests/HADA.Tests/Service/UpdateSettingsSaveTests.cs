using HADA.Core.Entities;
using HADA.Core.Messaging;
using HADA.Engine.Mqtt;
using HADA.Engine.WebSocket;
using HADA.Ipc;
using HADA.Service;
using HADA.Service.Logging;
using HADA.Service.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace HADA.Tests.Service;

/// <summary>How updates are looked for is saved on its own, by anyone; saving the other settings leaves it as it is.</summary>
public sealed class UpdateSettingsSaveTests : IAsyncDisposable
{
    private static readonly MqttSettings Broker = new("broker.local", 1883, false, "hada", "", "", "homeassistant", "hada") { Id = "default" };

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "hada-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ChannelEventBus _bus = new();
    private readonly SettingsStore _store;
    private readonly EngineSupervisor _engines;
    private readonly ServiceControl _control;

    public UpdateSettingsSaveTests()
    {
        _store = new SettingsStore(_folder);
        var registry = new EntityRegistry(_bus);
        var mqtt = new TestOptionsMonitor<MqttOptions>(new MqttOptions());
        var mqttServers = new TestOptionsMonitor<MqttServersOptions>(new MqttServersOptions());
        var homeAssistant = new TestOptionsMonitor<HaWebSocketOptions>(new HaWebSocketOptions());
        var homeAssistantServers = new TestOptionsMonitor<HomeAssistantServersOptions>(new HomeAssistantServersOptions());
        var entities = new TestOptionsMonitor<EntityOptions>(new EntityOptions());
        _engines = new EngineSupervisor(
            _bus, registry, mqtt, mqttServers, homeAssistant, homeAssistantServers, entities, NullLoggerFactory.Instance);
        _control = new ServiceControl(
            registry,
            new TelemetryCache(_bus),
            _engines,
            _store,
            new StoredSettingsConfigurationProvider(_store),
            mqtt,
            mqttServers,
            homeAssistant,
            homeAssistantServers,
            entities,
            new TestOptionsMonitor<CustomSensorOptions>(new CustomSensorOptions()),
            new TestOptionsMonitor<UpdateOptions>(new UpdateOptions()),
            new LogBuffer(),
            NullLogger<ServiceControl>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _engines.DisposeAsync();
        await _bus.DisposeAsync();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public async Task The_update_switches_are_saved_without_touching_the_servers()
    {
        _store.Save(new StoredSettings { MqttServers = [new StoredMqttServer(Broker, SettingsStore.Protect("pw"))] });

        var result = await _control.SaveUpdateSettingsAsync(
            new UpdateSettings(CheckAutomatically: false, IncludePrereleases: true), CancellationToken.None);

        Assert.True(result.Success, result.Message);
        var saved = _store.Load();
        Assert.Equal(new UpdateSettings(CheckAutomatically: false, IncludePrereleases: true), saved.Updates);
        var server = Assert.Single(saved.GetMqttServers()!);
        Assert.Equal("broker.local", server.Settings.Host);
        Assert.Equal("pw", SettingsStore.TryUnprotect(server.Password));
    }

    [Fact]
    public async Task Saving_the_settings_keeps_the_update_switches_as_they_are()
    {
        _store.Save(new StoredSettings { Updates = new UpdateSettings(CheckAutomatically: false, IncludePrereleases: true) });

        var result = await _control.SaveSettingsAsync(
            new SettingsUpdate([new MqttServerUpdate(Broker, SecretUpdate.Unchanged)], [], [], [], []), CancellationToken.None);

        Assert.True(result.Success, result.Message);
        var saved = _store.Load();
        Assert.Equal(new UpdateSettings(CheckAutomatically: false, IncludePrereleases: true), saved.Updates);
        Assert.Equal("broker.local", Assert.Single(saved.GetMqttServers()!).Settings.Host);
    }

    [Fact]
    public async Task Switches_left_as_they_are_write_nothing()
    {
        var result = await _control.SaveUpdateSettingsAsync(new UpdateSettings(), CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.False(File.Exists(_store.FilePath));
    }
}

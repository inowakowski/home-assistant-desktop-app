using HADA.Core.Entities;
using HADA.Core.Messaging;
using HADA.Engine.Mqtt;
using HADA.Engine.WebSocket;
using HADA.Ipc;
using HADA.Service;
using HADA.Service.Logging;
using HADA.Service.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace HADA.Tests.Service;

/// <summary>
/// Several Home Assistants connected to directly at once: which ones are in effect, how they are saved, what
/// becomes of the one that earlier versions saved, and that each gets an engine.
/// </summary>
public sealed class HomeAssistantServersTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "hada-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void Without_a_list_the_HomeAssistant_section_is_the_one_server()
    {
        var servers = HomeAssistantServersOptions.Resolve(
            new HomeAssistantServersOptions(), new HaWebSocketOptions { BaseUrl = "http://ha.local:8123" });

        var server = Assert.Single(servers);
        Assert.Equal("http://ha.local:8123", server.BaseUrl);
        Assert.Equal(HaWebSocketOptions.DefaultId, server.Id);
    }

    [Fact]
    public void Servers_listed_by_hand_get_ids_from_their_place_and_duplicates_are_dropped()
    {
        var listed = new HomeAssistantServersOptions
        {
            Items =
            [
                new HaWebSocketOptions { BaseUrl = "http://a.local" },
                new HaWebSocketOptions { BaseUrl = "http://b.local" },
                new HaWebSocketOptions { Id = "server1", BaseUrl = "http://c.local" },
            ],
        };

        var servers = HomeAssistantServersOptions.Resolve(listed, new HaWebSocketOptions { BaseUrl = "http://ignored.local" });

        Assert.Equal(["server1", "server2"], servers.Select(server => server.Id));
        Assert.Equal(["http://a.local", "http://b.local"], servers.Select(server => server.BaseUrl));
    }

    [Fact]
    public void Saving_no_servers_means_none_whatever_appsettings_json_says()
    {
        var saved = new HomeAssistantServersOptions { Count = 0, Items = [new HaWebSocketOptions { BaseUrl = "http://from-appsettings.local" }] };

        Assert.Empty(HomeAssistantServersOptions.Resolve(saved, new HaWebSocketOptions { BaseUrl = "http://also-from-appsettings.local" }));
    }

    [Fact]
    public void The_one_Home_Assistant_an_earlier_version_saved_is_kept_with_its_token()
    {
        // settings.json as 1.3 wrote it: one HomeAssistant section, and the token beside it.
        var store = new SettingsStore(_folder);
        Directory.CreateDirectory(_folder);
        File.WriteAllText(
            store.FilePath,
            $$"""
            {
              "homeAssistant": { "baseUrl": "http://ha.local:8123", "deviceId": "desk", "deviceName": "Desk", "commandEventType": "my_command" },
              "accessToken": "{{SettingsStore.Protect("old-token")}}"
            }
            """);

        var configuration = new ConfigurationBuilder().Add(new StoredSettingsConfigurationSource(store)).Build();
        var options = configuration.GetSection(HomeAssistantServersOptions.SectionName).Get<HomeAssistantServersOptions>()!;
        var server = Assert.Single(HomeAssistantServersOptions.Resolve(options, new HaWebSocketOptions()));

        Assert.Equal(HaWebSocketOptions.DefaultId, server.Id);
        Assert.Equal("http://ha.local:8123", server.BaseUrl);
        Assert.Equal("desk", server.DeviceId);
        Assert.Equal("Desk", server.DeviceName);
        Assert.Equal("my_command", server.CommandEventType);
        Assert.Equal("old-token", server.AccessToken);
    }

    [Fact]
    public async Task Saving_writes_the_list_and_no_longer_the_single_section()
    {
        await using var control = CreateControl(new HomeAssistantServersOptions(), out var store);

        var result = await control.SaveSettingsAsync(
            Update(new HomeAssistantServerUpdate(Settings("http://ha.local:8123", "a", "Flat"), new SecretUpdate(SecretChange.Replace, "token"))),
            CancellationToken.None);

        Assert.True(result.Success, result.Message);
        var saved = store.Load();
        Assert.Null(saved.HomeAssistant);
        Assert.Null(saved.AccessToken);
        Assert.Equal("token", SettingsStore.TryUnprotect(Assert.Single(saved.HomeAssistantServers!).AccessToken));
        Assert.DoesNotContain("\"homeAssistant\":", File.ReadAllText(store.FilePath));
    }

    [Fact]
    public async Task Each_server_keeps_its_own_token_by_its_id_whatever_else_changes()
    {
        await using var control = CreateControl(
            new HomeAssistantServersOptions
            {
                Count = 3,
                Items =
                [
                    new HaWebSocketOptions { Id = "a", Name = "Flat", BaseUrl = "http://flat.local:8123", AccessToken = "flat-token" },
                    new HaWebSocketOptions { Id = "b", Name = "Office", BaseUrl = "http://office.local:8123", AccessToken = "office-token" },
                    new HaWebSocketOptions { Id = "c", Name = "Gone", BaseUrl = "http://gone.local:8123", AccessToken = "gone-token" },
                ],
            },
            out var store);

        var result = await control.SaveSettingsAsync(
            Update(
                // Moved to the front, renamed and given another address: still the same server.
                new HomeAssistantServerUpdate(Settings("https://office.example.com", "b", "Biuro"), SecretUpdate.Unchanged),
                new HomeAssistantServerUpdate(Settings("http://flat.local:8123", "a", "Flat"), new SecretUpdate(SecretChange.Clear)),
                new HomeAssistantServerUpdate(Settings("http://new.local:8123", "d", "New"), new SecretUpdate(SecretChange.Replace, "new-token"))),
            CancellationToken.None);

        Assert.True(result.Success, result.Message);
        var saved = store.Load().GetHomeAssistantServers()!;
        Assert.Equal(["b", "a", "d"], saved.Select(server => server.Settings.Id));
        Assert.Equal("office-token", SettingsStore.TryUnprotect(saved[0].AccessToken));
        Assert.Null(saved[1].AccessToken);
        Assert.Equal("new-token", SettingsStore.TryUnprotect(saved[2].AccessToken));
        Assert.DoesNotContain("gone-token", saved.Select(server => SettingsStore.TryUnprotect(server.AccessToken)));
    }

    [Fact]
    public async Task Settings_say_whether_each_server_has_a_token_but_never_which()
    {
        await using var control = CreateControl(
            new HomeAssistantServersOptions
            {
                Items =
                [
                    new HaWebSocketOptions { Id = "a", Name = "Flat", BaseUrl = "http://flat.local:8123", AccessToken = "flat-token" },
                    new HaWebSocketOptions { Id = "b", Name = "Office", BaseUrl = "http://office.local:8123" },
                ],
            },
            out _);

        var snapshot = await control.GetSettingsAsync(CancellationToken.None);

        Assert.Equal([true, false], snapshot.HomeAssistantServers.Select(server => server.HasAccessToken));
        Assert.Equal(["Flat", "Office"], snapshot.HomeAssistantServers.Select(server => server.Settings.Name));
        Assert.DoesNotContain("flat-token", System.Text.Json.JsonSerializer.Serialize(snapshot));
    }

    [Fact]
    public async Task Every_server_gets_an_engine_and_one_that_is_removed_is_disconnected_from()
    {
        await using var bus = new ChannelEventBus();
        var registry = new EntityRegistry(bus);
        var servers = new TestOptionsMonitor<HomeAssistantServersOptions>(new HomeAssistantServersOptions
        {
            Count = 2,
            Items =
            [
                Unreachable("a", "Flat", port: 1),
                Unreachable("b", "Office", port: 2),
            ],
        });
        await using var supervisor = new EngineSupervisor(
            bus,
            registry,
            new TestOptionsMonitor<MqttOptions>(new MqttOptions()),
            new TestOptionsMonitor<MqttServersOptions>(new MqttServersOptions()),
            new TestOptionsMonitor<HaWebSocketOptions>(new HaWebSocketOptions()),
            servers,
            new TestOptionsMonitor<EntityOptions>(new EntityOptions()),
            NullLoggerFactory.Instance);

        await supervisor.StartAsync(CancellationToken.None);
        var engines = supervisor.GetStatus().Where(engine => engine.Name == "websocket").ToList();
        Assert.Equal(["a", "b"], engines.Select(engine => engine.ServerId));
        Assert.Equal(["Flat", "Office"], engines.Select(engine => engine.ServerName));
        Assert.All(engines, engine => Assert.True(engine.IsConfigured));

        servers.Set(new HomeAssistantServersOptions { Count = 1, Items = [servers.CurrentValue.Items[1]] });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (supervisor.GetStatus().Count(engine => engine.Name == "websocket") != 1)
        {
            await Task.Delay(20, timeout.Token);
        }

        Assert.Equal("b", supervisor.GetStatus().Single(engine => engine.Name == "websocket").ServerId);

        // No server at all still shows the WebSocket connection, as not set up.
        servers.Set(new HomeAssistantServersOptions { Count = 0 });
        while (supervisor.GetStatus().Single(engine => engine.Name == "websocket").ServerId is not null)
        {
            await Task.Delay(20, timeout.Token);
        }

        Assert.False(supervisor.GetStatus().Single(engine => engine.Name == "websocket").IsConfigured);
        await supervisor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task An_engine_is_named_after_its_server_so_that_answers_go_to_the_right_one()
    {
        await using var bus = new ChannelEventBus();
        var registry = new EntityRegistry(bus);

        Assert.Equal("websocket", Engine(new HaWebSocketOptions()).Name);
        Assert.Equal("websocket (Flat)", Engine(new HaWebSocketOptions { Name = " Flat " }).Name);

        HaWebSocketEngine Engine(HaWebSocketOptions options) =>
            new(bus, registry, Microsoft.Extensions.Options.Options.Create(options), NullLogger<HaWebSocketEngine>.Instance);
    }

    private static HaWebSocketOptions Unreachable(string id, string name, int port) => new()
    {
        Id = id,
        Name = name,
        BaseUrl = $"http://127.0.0.1:{port}",
        AccessToken = "token",
        MinReconnectDelay = TimeSpan.FromMinutes(1),
    };

    private static HomeAssistantSettings Settings(string baseUrl, string id, string name) =>
        new(baseUrl, "", "", "hada_command") { Id = id, Name = name };

    private static SettingsUpdate Update(params HomeAssistantServerUpdate[] servers) => new(
        [],
        servers,
        [],
        [],
        [],
        new UpdateSettings());

    private sealed class Control(ChannelEventBus bus, EngineSupervisor engines, ServiceControl control) : IAsyncDisposable
    {
        public Task<OperationResult> SaveSettingsAsync(SettingsUpdate settings, CancellationToken cancellationToken) =>
            control.SaveSettingsAsync(settings, cancellationToken);

        public Task<SettingsSnapshot> GetSettingsAsync(CancellationToken cancellationToken) =>
            control.GetSettingsAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await engines.DisposeAsync();
            await bus.DisposeAsync();
        }
    }

    private Control CreateControl(HomeAssistantServersOptions saved, out SettingsStore store)
    {
        store = new SettingsStore(_folder);
        var bus = new ChannelEventBus();
        var registry = new EntityRegistry(bus);
        var mqtt = new TestOptionsMonitor<MqttOptions>(new MqttOptions());
        var mqttServers = new TestOptionsMonitor<MqttServersOptions>(new MqttServersOptions());
        var homeAssistant = new TestOptionsMonitor<HaWebSocketOptions>(new HaWebSocketOptions());
        var homeAssistantServers = new TestOptionsMonitor<HomeAssistantServersOptions>(saved);
        var entities = new TestOptionsMonitor<EntityOptions>(new EntityOptions());
        var engines = new EngineSupervisor(
            bus, registry, mqtt, mqttServers, homeAssistant, homeAssistantServers, entities, NullLoggerFactory.Instance);
        var control = new ServiceControl(
            registry,
            new TelemetryCache(bus),
            engines,
            store,
            new StoredSettingsConfigurationProvider(store),
            mqtt,
            mqttServers,
            homeAssistant,
            homeAssistantServers,
            entities,
            new TestOptionsMonitor<CustomSensorOptions>(new CustomSensorOptions()),
            new TestOptionsMonitor<UpdateOptions>(new UpdateOptions()),
            new LogBuffer(),
            NullLogger<ServiceControl>.Instance);
        return new Control(bus, engines, control);
    }
}

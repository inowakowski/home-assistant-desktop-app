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

/// <summary>Several MQTT servers at once: which ones are in effect, how they are saved, and that each gets an engine.</summary>
public sealed class MqttServersTests : IDisposable
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
    public void Without_a_list_the_Mqtt_section_is_the_one_server()
    {
        var servers = MqttServersOptions.Resolve(new MqttServersOptions(), new MqttOptions { Host = "broker.local" });

        var server = Assert.Single(servers);
        Assert.Equal("broker.local", server.Host);
        Assert.Equal(MqttOptions.DefaultId, server.Id);
    }

    [Fact]
    public void Servers_listed_by_hand_get_ids_from_their_place_and_duplicates_are_dropped()
    {
        var listed = new MqttServersOptions
        {
            Items = [new MqttOptions { Host = "a.local" }, new MqttOptions { Host = "b.local" }, new MqttOptions { Id = "server1", Host = "c.local" }],
        };

        var servers = MqttServersOptions.Resolve(listed, new MqttOptions { Host = "ignored.local" });

        Assert.Equal(["server1", "server2"], servers.Select(server => server.Id));
        Assert.Equal(["a.local", "b.local"], servers.Select(server => server.Host));
    }

    [Fact]
    public void Saving_no_servers_means_none_whatever_appsettings_json_says()
    {
        var saved = new MqttServersOptions { Count = 0, Items = [new MqttOptions { Host = "from-appsettings.local" }] };

        Assert.Empty(MqttServersOptions.Resolve(saved, new MqttOptions { Host = "also-from-appsettings.local" }));
    }

    [Fact]
    public void Saved_servers_replace_a_longer_list_from_appsettings_json()
    {
        // appsettings.json lists three servers; the window saved one. Configuration merges lists item by item.
        var store = new SettingsStore(_folder);
        store.Save(new StoredSettings
        {
            MqttServers = [new StoredMqttServer(Settings("saved.local", "s1", "Saved"), SettingsStore.Protect("pw"))],
        });
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MqttServers:Items:0:Host"] = "one.local",
                ["MqttServers:Items:0:MinReconnectDelay"] = "00:00:05",
                ["MqttServers:Items:1:Host"] = "two.local",
                ["MqttServers:Items:2:Host"] = "three.local",
            })
            .Add(new StoredSettingsConfigurationSource(store))
            .Build();

        var options = configuration.GetSection(MqttServersOptions.SectionName).Get<MqttServersOptions>()!;
        var server = Assert.Single(MqttServersOptions.Resolve(options, new MqttOptions()));

        Assert.Equal("saved.local", server.Host);
        Assert.Equal("Saved", server.Name);
        Assert.Equal("pw", server.Password);

        // What the window does not set, appsettings.json still may.
        Assert.Equal(TimeSpan.FromSeconds(5), server.MinReconnectDelay);
    }

    [Fact]
    public async Task Each_server_keeps_its_own_password_by_its_id_whatever_else_changes()
    {
        var servers = new TestOptionsMonitor<MqttServersOptions>(new MqttServersOptions
        {
            Count = 3,
            Items =
            [
                new MqttOptions { Id = "a", Name = "Flat", Host = "flat.local", Password = "flat-password" },
                new MqttOptions { Id = "b", Name = "Office", Host = "office.local", Password = "office-password" },
                new MqttOptions { Id = "c", Name = "Gone", Host = "gone.local", Password = "gone-password" },
            ],
        });
        await using var control = CreateControl(servers, out var store);

        var result = await control.SaveSettingsAsync(
            Update(
                // Moved to the front, renamed and given another address: still the same server.
                new MqttServerUpdate(Settings("office.example.com", "b", "Biuro"), SecretUpdate.Unchanged),
                new MqttServerUpdate(Settings("flat.local", "a", "Flat"), new SecretUpdate(SecretChange.Clear)),
                new MqttServerUpdate(Settings("new.local", "d", "New"), new SecretUpdate(SecretChange.Replace, "new-password"))),
            CancellationToken.None);

        Assert.True(result.Success, result.Message);
        var saved = store.Load().GetMqttServers()!;
        Assert.Equal(["b", "a", "d"], saved.Select(server => server.Settings.Id));
        Assert.Equal("office-password", SettingsStore.TryUnprotect(saved[0].Password));
        Assert.Null(saved[1].Password);
        Assert.Equal("new-password", SettingsStore.TryUnprotect(saved[2].Password));
        Assert.DoesNotContain("gone-password", saved.Select(server => SettingsStore.TryUnprotect(server.Password)));
    }

    [Fact]
    public async Task Settings_say_whether_each_server_has_a_password_but_never_which()
    {
        var servers = new TestOptionsMonitor<MqttServersOptions>(new MqttServersOptions
        {
            Items =
            [
                new MqttOptions { Id = "a", Name = "Flat", Host = "flat.local", Password = "flat-password" },
                new MqttOptions { Id = "b", Name = "Office", Host = "office.local" },
            ],
        });
        await using var control = CreateControl(servers, out _);

        var snapshot = await control.GetSettingsAsync(CancellationToken.None);

        Assert.Equal([true, false], snapshot.MqttServers.Select(server => server.HasPassword));
        Assert.Equal(["Flat", "Office"], snapshot.MqttServers.Select(server => server.Settings.Name));
        Assert.DoesNotContain("flat-password", System.Text.Json.JsonSerializer.Serialize(snapshot));
    }

    [Fact]
    public async Task Every_server_gets_an_engine_and_one_that_is_removed_is_disconnected_from()
    {
        await using var bus = new ChannelEventBus();
        var registry = new EntityRegistry(bus);
        var servers = new TestOptionsMonitor<MqttServersOptions>(new MqttServersOptions
        {
            Count = 2,
            Items =
            [
                new MqttOptions { Id = "a", Name = "Flat", Host = "127.0.0.1", Port = 1, MinReconnectDelay = TimeSpan.FromMinutes(1) },
                new MqttOptions { Id = "b", Name = "Office", Host = "127.0.0.1", Port = 2, MinReconnectDelay = TimeSpan.FromMinutes(1) },
            ],
        });
        await using var supervisor = new EngineSupervisor(
            bus,
            registry,
            new TestOptionsMonitor<MqttOptions>(new MqttOptions()),
            servers,
            new TestOptionsMonitor<HaWebSocketOptions>(new HaWebSocketOptions()),
            new TestOptionsMonitor<HomeAssistantServersOptions>(new HomeAssistantServersOptions()),
            new TestOptionsMonitor<EntityOptions>(new EntityOptions()),
            NullLoggerFactory.Instance);

        await supervisor.StartAsync(CancellationToken.None);
        var mqtt = supervisor.GetStatus().Where(engine => engine.Name == "mqtt").ToList();
        Assert.Equal(["a", "b"], mqtt.Select(engine => engine.ServerId));
        Assert.Equal(["Flat", "Office"], mqtt.Select(engine => engine.ServerName));
        Assert.All(mqtt, engine => Assert.True(engine.IsConfigured));

        servers.Set(new MqttServersOptions { Count = 1, Items = [servers.CurrentValue.Items[1]] });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (supervisor.GetStatus().Count(engine => engine.Name == "mqtt") != 1)
        {
            await Task.Delay(20, timeout.Token);
        }

        Assert.Equal("b", supervisor.GetStatus().Single(engine => engine.Name == "mqtt").ServerId);

        // No server at all still shows MQTT, as not set up.
        servers.Set(new MqttServersOptions { Count = 0 });
        while (supervisor.GetStatus().Single(engine => engine.Name == "mqtt").ServerId is not null)
        {
            await Task.Delay(20, timeout.Token);
        }

        Assert.False(supervisor.GetStatus().Single(engine => engine.Name == "mqtt").IsConfigured);
        await supervisor.StopAsync(CancellationToken.None);
    }

    private static MqttSettings Settings(string host, string id, string name) =>
        new(host, 1883, false, "", "", "", "homeassistant", "hada") { Id = id, Name = name };

    private static SettingsUpdate Update(params MqttServerUpdate[] servers) => new(
        servers,
        [],
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

    private Control CreateControl(TestOptionsMonitor<MqttServersOptions> servers, out SettingsStore store)
    {
        store = new SettingsStore(_folder);
        var bus = new ChannelEventBus();
        var registry = new EntityRegistry(bus);
        var mqtt = new TestOptionsMonitor<MqttOptions>(new MqttOptions());
        var homeAssistant = new TestOptionsMonitor<HaWebSocketOptions>(new HaWebSocketOptions());
        var homeAssistantServers = new TestOptionsMonitor<HomeAssistantServersOptions>(new HomeAssistantServersOptions());
        var entities = new TestOptionsMonitor<EntityOptions>(new EntityOptions());
        var engines = new EngineSupervisor(
            bus, registry, mqtt, servers, homeAssistant, homeAssistantServers, entities, NullLoggerFactory.Instance);
        var control = new ServiceControl(
            registry,
            new TelemetryCache(bus),
            engines,
            store,
            new StoredSettingsConfigurationProvider(store),
            mqtt,
            servers,
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

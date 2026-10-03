using System.Net;
using System.Net.Sockets;
using HADA.Core.Entities;
using HADA.Core.Messaging;
using HADA.Engine.Mqtt;
using HADA.Engine.WebSocket;
using HADA.Service;
using HADA.Service.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HADA.Tests.Service;

public class EngineSupervisorTests
{
    [Fact]
    public async Task Changed_settings_recreate_only_the_affected_engine()
    {
        await using var bus = new ChannelEventBus();
        var registry = new EntityRegistry(bus);
        var mqtt = new TestOptionsMonitor<MqttOptions>(new MqttOptions());
        var servers = new TestOptionsMonitor<MqttServersOptions>(new MqttServersOptions());
        var homeAssistant = new TestOptionsMonitor<HaWebSocketOptions>(new HaWebSocketOptions());
        var homeAssistantServers = new TestOptionsMonitor<HomeAssistantServersOptions>(new HomeAssistantServersOptions());
        var entities = new TestOptionsMonitor<EntityOptions>(new EntityOptions());
        await using var supervisor = new EngineSupervisor(
            bus, registry, mqtt, servers, homeAssistant, homeAssistantServers, entities, NullLoggerFactory.Instance);

        await supervisor.StartAsync(CancellationToken.None);
        Assert.All(supervisor.GetStatus(), engine => Assert.False(engine.IsConfigured));

        mqtt.Set(new MqttOptions { Host = "127.0.0.1", Port = GetFreePort(), MinReconnectDelay = TimeSpan.FromMinutes(1) });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!supervisor.GetStatus().Single(engine => engine.Name == "mqtt").IsConfigured)
        {
            await Task.Delay(20, timeout.Token);
        }

        Assert.False(supervisor.GetStatus().Single(engine => engine.Name == "websocket").IsConfigured);
        await supervisor.StopAsync(CancellationToken.None);
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

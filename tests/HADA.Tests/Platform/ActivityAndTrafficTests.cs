using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Messaging;
using HADA.Core.Models;
using HADA.Core.Sensors;

namespace HADA.Tests.Platform;

/// <summary>Idle time, the network's traffic and the battery's details, on every system.</summary>
public sealed class ActivityAndTrafficTests : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly ChannelEventBus _bus = new();
    private readonly EntityRegistry _registry;

    public ActivityAndTrafficTests() => _registry = new EntityRegistry(_bus);

    public ValueTask DisposeAsync() => _bus.DisposeAsync();

    [Fact]
    public async Task Idle_time_is_reported_in_whole_minutes()
    {
        await using var readings = _bus.Subscribe<TelemetryEvent>();
        using var sensor = new IdleTimeSensor(_bus, _registry, new Idle(TimeSpan.FromSeconds(185)));
        await sensor.StartAsync(CancellationToken.None);

        using var timeout = new CancellationTokenSource(Timeout);
        var reading = await readings.ReadAllAsync(timeout.Token).FirstAsync(reading => reading.SensorId == BuiltInEntityIds.IdleTime, timeout.Token);

        Assert.Equal("3", reading.State);
        Assert.True(_registry.TryGet(BuiltInEntityIds.IdleTime, out var entity));
        Assert.Equal("min", entity.UnitOfMeasurement);
        await sensor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Without_an_idle_time_there_is_no_sensor()
    {
        using var sensor = new IdleTimeSensor(_bus, _registry, new Idle(null));
        await sensor.StartAsync(CancellationToken.None);
        await sensor.StopAsync(CancellationToken.None);

        Assert.Empty(_registry.Entities);
    }

    [Fact]
    public void Traffic_is_the_difference_over_the_time_between_two_readings()
    {
        var rates = NetworkTrafficSensor.Rates(
            new NetworkTraffic("eth0", 1_000_000, 50_000), new NetworkTraffic("eth0", 3_000_000, 70_000), TimeSpan.FromSeconds(10));

        Assert.Equal((200.0, 2.0), rates);
    }

    [Theory]
    [InlineData("wlan0", 1_000, 1_000)]
    [InlineData("eth0", 10, 1_000)]
    [InlineData("eth0", 1_000, 10)]
    public void Another_connection_or_counters_that_started_over_give_no_traffic(string name, long received, long sent)
    {
        Assert.Null(NetworkTrafficSensor.Rates(new NetworkTraffic("eth0", 500, 500), new NetworkTraffic(name, received, sent), TimeSpan.FromSeconds(10)));
        Assert.Null(NetworkTrafficSensor.Rates(null, new NetworkTraffic("eth0", 500, 500), TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Each_system_counts_the_traffic_of_the_connection_it_routes_through()
    {
        // A machine without a network (some CI sandboxes) has nothing to count.
        if (NetworkAddress.TryRead() is not { InterfaceName.Length: > 0 } address)
        {
            return;
        }

        var traffic = Assert.NotNull(NetworkAddress.TryReadTraffic());
        Assert.Equal(address.InterfaceName, traffic.InterfaceName);
        Assert.True(traffic.BytesReceived >= 0 && traffic.BytesSent >= 0);
    }

    [Fact]
    public async Task Traffic_is_reported_from_the_second_reading_on()
    {
        await using var readings = _bus.Subscribe<TelemetryEvent>();
        var counters = new Queue<NetworkTraffic?>([new NetworkTraffic("eth0", 0, 0), new NetworkTraffic("eth0", 100_000, 10_000)]);
        using var sensor = new NetworkTrafficSensor(_bus, _registry, () => counters.Count > 0 ? counters.Dequeue() : new NetworkTraffic("eth0", 100_000, 10_000));
        await sensor.StartAsync(CancellationToken.None);

        Assert.True(_registry.TryGet(BuiltInEntityIds.NetworkDownload, out var download));
        Assert.Equal("data_rate", download.DeviceClass);
        Assert.True(_registry.TryGet(BuiltInEntityIds.NetworkUpload, out _));

        // The first comparison is ten seconds after the start.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var reading = await readings.ReadAllAsync(timeout.Token).FirstAsync(reading => reading.SensorId == BuiltInEntityIds.NetworkDownload, timeout.Token);
        Assert.InRange(double.Parse(reading.State, System.Globalization.CultureInfo.InvariantCulture), 1, 10);
        Assert.Equal("eth0", reading.Attributes["interface"]);
        await sensor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void The_batterys_details_are_attributes_in_whole_minutes_and_watts()
    {
        var power = new PowerStatus(true, 50, false, false)
        {
            TimeRemaining = TimeSpan.FromMinutes(95.6),
            HealthPercent = 88,
            CycleCount = 312,
            PowerWatts = 7.6,
        };

        var details = BatterySensor.Details(power);

        Assert.Equal(95, details["time_remaining"]);
        Assert.Equal(88, details["health"]);
        Assert.Equal(312, details["cycle_count"]);
        Assert.Equal(8.0, details["power"]);
        Assert.All(BatterySensor.Details(new PowerStatus(true, 50, false, false)), detail => Assert.Null(detail.Value));
    }

    private sealed class Idle(TimeSpan? idle) : ISessionReadings
    {
        public TimeSpan? ReadIdleTime() => idle;

        public IReadOnlyList<string>? AppsUsing(CaptureDevice device) => null;
    }
}

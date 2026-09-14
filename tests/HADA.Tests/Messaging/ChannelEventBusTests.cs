using HADA.Core.Abstractions;
using HADA.Core.Messaging;
using HADA.Core.Models;

namespace HADA.Tests.Messaging;

public class ChannelEventBusTests
{
    private static TelemetryEvent Reading(string state) => new() { SensorId = "cpu_load", State = state };

    [Fact]
    public async Task Subscriber_receives_published_event()
    {
        await using var bus = new ChannelEventBus();
        await using var subscription = bus.Subscribe<TelemetryEvent>();

        var reading = Reading("42");
        await bus.PublishAsync(reading);

        Assert.True(subscription.TryRead(out var received));
        Assert.Same(reading, received);
    }

    [Fact]
    public async Task Every_subscriber_receives_the_event()
    {
        await using var bus = new ChannelEventBus();
        await using var first = bus.Subscribe<TelemetryEvent>();
        await using var second = bus.Subscribe<TelemetryEvent>();

        await bus.PublishAsync(Reading("1"));

        Assert.True(first.TryRead(out _));
        Assert.True(second.TryRead(out _));
    }

    [Fact]
    public async Task Events_are_routed_by_type_including_base_types()
    {
        await using var bus = new ChannelEventBus();
        await using var telemetry = bus.Subscribe<TelemetryEvent>();
        await using var commands = bus.Subscribe<ActionCommand>();
        await using var everything = bus.Subscribe<object>();

        await bus.PublishAsync(new ActionCommand { ActionId = "power.shutdown" });

        Assert.False(telemetry.TryRead(out _));
        Assert.True(commands.TryRead(out _));
        Assert.True(everything.TryRead(out _));
    }

    [Fact]
    public async Task Disposed_subscription_stops_receiving_and_completes_enumeration()
    {
        await using var bus = new ChannelEventBus();
        var subscription = bus.Subscribe<TelemetryEvent>();
        await bus.PublishAsync(Reading("before"));

        await subscription.DisposeAsync();
        await bus.PublishAsync(Reading("after"));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var states = new List<string>();
        await foreach (var reading in subscription.ReadAllAsync(timeout.Token))
        {
            states.Add(reading.State);
        }

        Assert.Equal(["before"], states);
    }

    [Fact]
    public async Task DropOldest_keeps_latest_events_when_buffer_is_full()
    {
        await using var bus = new ChannelEventBus();
        await using var subscription = bus.Subscribe<int>(new EventSubscriptionOptions
        {
            Capacity = 2,
            Backpressure = BackpressureMode.DropOldest,
        });

        await bus.PublishAsync(1);
        await bus.PublishAsync(2);
        await bus.PublishAsync(3);

        Assert.True(subscription.TryRead(out var a));
        Assert.True(subscription.TryRead(out var b));
        Assert.False(subscription.TryRead(out _));
        Assert.Equal((2, 3), (a, b));
    }

    [Fact]
    public async Task Wait_mode_blocks_publisher_until_subscriber_reads()
    {
        await using var bus = new ChannelEventBus();
        await using var subscription = bus.Subscribe<int>(new EventSubscriptionOptions { Capacity = 1 });

        await bus.PublishAsync(1);
        var blocked = bus.PublishAsync(2).AsTask();
        Assert.False(blocked.IsCompleted);

        Assert.True(subscription.TryRead(out _));
        await blocked.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(subscription.TryRead(out var second));
        Assert.Equal(2, second);
    }
}

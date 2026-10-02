using System.Globalization;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Messaging;
using Microsoft.Extensions.Hosting;

namespace HADA.Platform.Windows.Sensors;

/// <summary>Publishes the share of physical memory in use.</summary>
public sealed class MemoryUsageSensor(IEventBus bus, IEntityRegistry registry) : BackgroundService
{
    public const string EntityId = "memory_usage";

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await registry.RegisterAsync(
            new EntityDescriptor
            {
                Id = EntityId,
                Name = "Memory usage",
                Kind = EntityKind.Sensor,
                Icon = "mdi:memory",
                UnitOfMeasurement = "%",
                StateClass = "measurement",
            },
            stoppingToken);

        var publisher = new ChangeOnlyPublisher(bus);
        using var timer = new PeriodicTimer(Interval);
        do
        {
            if (SystemMemory.TryReadLoadPercent() is { } load)
            {
                await publisher.PublishAsync(EntityId, load.ToString(CultureInfo.InvariantCulture), cancellationToken: stoppingToken);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

using System.Globalization;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Models;
using Microsoft.Extensions.Hosting;

namespace HADA.Platform.Windows.Sensors;

/// <summary>Publishes when Windows was started, as a timestamp Home Assistant can show as "3 hours ago".</summary>
public sealed class LastBootSensor(IEventBus bus, IEntityRegistry registry) : BackgroundService
{
    public const string EntityId = "last_boot";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await registry.RegisterAsync(
            new EntityDescriptor
            {
                Id = EntityId,
                Name = "Last boot",
                Kind = EntityKind.Sensor,
                DeviceClass = "timestamp",
                Icon = "mdi:restart",
            },
            stoppingToken);

        // Whole seconds, so the value is the same every time the service starts during one Windows session.
        var bootTime = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        var rounded = DateTimeOffset.FromUnixTimeSeconds(bootTime.ToUnixTimeSeconds());
        await bus.PublishAsync(
            new TelemetryEvent { SensorId = EntityId, State = rounded.ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture) },
            stoppingToken);
    }
}

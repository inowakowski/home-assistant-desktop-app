using System.Globalization;
using System.Runtime.InteropServices;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Hosting;
using HADA.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HADA.Platform.Windows.Sensors;

/// <summary>Publishes system-wide CPU load as a percentage.</summary>
public sealed partial class CpuLoadSensor(IEventBus bus, IEntityRegistry registry, ILogger<CpuLoadSensor> logger)
    : EagerBackgroundService
{
    public const string EntityId = BuiltInEntityIds.CpuLoad;

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await registry.RegisterAsync(
            new EntityDescriptor
            {
                Id = EntityId,
                Name = "CPU load",
                Kind = EntityKind.Sensor,
                Icon = "mdi:cpu-64-bit",
                UnitOfMeasurement = "%",
                StateClass = "measurement",
            },
            stoppingToken);

        if (!CpuTimes.TryRead(out var previous))
        {
            LogUnavailable(logger, Marshal.GetLastPInvokeError());
            return;
        }

        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!CpuTimes.TryRead(out var current))
            {
                continue;
            }

            var load = CpuTimes.LoadPercent(previous, current);
            previous = current;

            await bus.PublishAsync(
                new TelemetryEvent { SensorId = EntityId, State = load.ToString("0.0", CultureInfo.InvariantCulture) },
                stoppingToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "CPU load sensor disabled: GetSystemTimes failed with error {Error}.")]
    private static partial void LogUnavailable(ILogger logger, int error);
}

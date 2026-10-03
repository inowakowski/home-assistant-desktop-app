using System.Globalization;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Hosting;
using HADA.Core.Messaging;
using Microsoft.Extensions.Hosting;

namespace HADA.Platform.Windows.Sensors;

/// <summary>
/// Publishes battery level, whether it is charging and whether the computer is plugged in.
/// Registers nothing on a computer without a battery, so desktops do not get three useless entities.
/// </summary>
public sealed class BatterySensor(IEventBus bus, IEntityRegistry registry) : EagerBackgroundService
{
    public const string LevelEntityId = BuiltInEntityIds.BatteryLevel;
    public const string ChargingEntityId = BuiltInEntityIds.BatteryCharging;
    public const string PluggedInEntityId = BuiltInEntityIds.PluggedIn;

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (SystemPower.TryRead() is not { HasBattery: true })
        {
            return;
        }

        await registry.RegisterAsync(
            new EntityDescriptor
            {
                Id = LevelEntityId,
                Name = "Battery level",
                Kind = EntityKind.Sensor,
                DeviceClass = "battery",
                UnitOfMeasurement = "%",
                StateClass = "measurement",
            },
            stoppingToken);
        await registry.RegisterAsync(
            new EntityDescriptor
            {
                Id = ChargingEntityId,
                Name = "Battery charging",
                Kind = EntityKind.BinarySensor,
                DeviceClass = "battery_charging",
            },
            stoppingToken);
        await registry.RegisterAsync(
            new EntityDescriptor
            {
                Id = PluggedInEntityId,
                Name = "Plugged in",
                Kind = EntityKind.BinarySensor,
                DeviceClass = "plug",
            },
            stoppingToken);

        var publisher = new ChangeOnlyPublisher(bus);
        using var timer = new PeriodicTimer(Interval);
        do
        {
            if (SystemPower.TryRead() is not { } power)
            {
                continue;
            }

            if (power.BatteryPercent is { } percent)
            {
                await publisher.PublishAsync(LevelEntityId, percent.ToString(CultureInfo.InvariantCulture), cancellationToken: stoppingToken);
            }

            await publisher.PublishAsync(ChargingEntityId, BinaryState.From(power.IsCharging), cancellationToken: stoppingToken);
            await publisher.PublishAsync(PluggedInEntityId, BinaryState.From(power.IsPluggedIn), cancellationToken: stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

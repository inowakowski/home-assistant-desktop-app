using System.Globalization;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Hosting;
using HADA.Core.Messaging;
using HADA.Core.Models;
using Microsoft.Extensions.Logging;

namespace HADA.Core.Sensors;

/// <summary>Publishes system-wide CPU load as a percentage.</summary>
public sealed partial class CpuLoadSensor(IEventBus bus, IEntityRegistry registry, ISystemReadings system, ILogger<CpuLoadSensor> logger)
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

        if (system.ReadCpu() is not { } previous)
        {
            LogUnavailable(logger);
            return;
        }

        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (system.ReadCpu() is not { } current)
            {
                continue;
            }

            var load = CpuSample.LoadPercent(previous, current);
            previous = current;

            await bus.PublishAsync(
                new TelemetryEvent { SensorId = EntityId, State = load.ToString("0.0", CultureInfo.InvariantCulture) },
                stoppingToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "CPU load sensor disabled: the CPU times could not be read.")]
    private static partial void LogUnavailable(ILogger logger);
}

/// <summary>Publishes the share of physical memory in use.</summary>
public sealed class MemoryUsageSensor(IEventBus bus, IEntityRegistry registry, ISystemReadings system) : EagerBackgroundService
{
    public const string EntityId = BuiltInEntityIds.MemoryUsage;

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
            if (system.ReadMemoryLoadPercent() is { } load)
            {
                await publisher.PublishAsync(EntityId, load.ToString(CultureInfo.InvariantCulture), cancellationToken: stoppingToken);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>
/// Publishes battery level, whether it is charging and whether the computer is plugged in.
/// Registers nothing on a computer without a battery, so desktops do not get three useless entities.
/// </summary>
public sealed class BatterySensor(IEventBus bus, IEntityRegistry registry, ISystemReadings system) : EagerBackgroundService
{
    public const string LevelEntityId = BuiltInEntityIds.BatteryLevel;
    public const string ChargingEntityId = BuiltInEntityIds.BatteryCharging;
    public const string PluggedInEntityId = BuiltInEntityIds.PluggedIn;

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (system.ReadPower() is not { HasBattery: true })
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
            if (system.ReadPower() is not { } power)
            {
                continue;
            }

            if (power.BatteryPercent is { } percent)
            {
                await publisher.PublishAsync(LevelEntityId, percent.ToString(CultureInfo.InvariantCulture), Details(power), stoppingToken);
            }

            await publisher.PublishAsync(ChargingEntityId, BinaryState.From(power.IsCharging), cancellationToken: stoppingToken);
            await publisher.PublishAsync(PluggedInEntityId, BinaryState.From(power.IsPluggedIn), cancellationToken: stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// What the system tells about the battery besides its level, as attributes of it; <see langword="null"/> for what
    /// it does not tell. Rounded so that they change about as often as the level does.
    /// </summary>
    public static Dictionary<string, object?> Details(PowerStatus power) => new()
    {
        ["time_remaining"] = power.TimeRemaining is { } remaining ? (int)remaining.TotalMinutes : null,
        ["health"] = power.HealthPercent,
        ["cycle_count"] = power.CycleCount,
        ["power"] = power.PowerWatts is { } watts ? Math.Round(watts, MidpointRounding.AwayFromZero) : null,
    };
}

/// <summary>Publishes when the system was started, as a timestamp Home Assistant can show as "3 hours ago".</summary>
public sealed class LastBootSensor(IEventBus bus, IEntityRegistry registry, ISystemReadings system) : EagerBackgroundService
{
    public const string EntityId = BuiltInEntityIds.LastBoot;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (system.ReadBootTime() is not { } bootTime)
        {
            return;
        }

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

        // Whole seconds, so the value is the same every time the service starts while the system runs.
        var rounded = DateTimeOffset.FromUnixTimeSeconds(bootTime.ToUnixTimeSeconds());
        await bus.PublishAsync(
            new TelemetryEvent { SensorId = EntityId, State = rounded.ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture) },
            stoppingToken);
    }
}

/// <summary>
/// Publishes who is signed in at the computer's own screen. Runs in the service, so it also reports that nobody is.
/// With several users signed in, this is the one whose desktop is shown, and whose session sensors are reported.
/// </summary>
public sealed class ActiveUserSensor(IEventBus bus, IEntityRegistry registry, ISystemReadings system) : EagerBackgroundService
{
    public const string EntityId = BuiltInEntityIds.ActiveUser;

    /// <summary>State while the sign-in screen is shown. A state cannot be empty.</summary>
    public const string Nobody = "none";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await registry.RegisterAsync(
            new EntityDescriptor { Id = EntityId, Name = "Signed-in user", Kind = EntityKind.Sensor, Icon = "mdi:account" },
            stoppingToken);

        var publisher = new ChangeOnlyPublisher(bus);
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            // Not known only briefly, as while users are switched.
            if (system.ReadConsoleUser() is { } user)
            {
                await publisher.PublishAsync(EntityId, user.Length > 0 ? user : Nobody, cancellationToken: stoppingToken);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>
/// Publishes whether the session at the computer's own screen is locked. Runs in the service, so it keeps
/// working while nobody is signed in and when no window or tray app runs.
/// </summary>
public sealed class SessionLockSensor(IEventBus bus, IEntityRegistry registry, ISystemReadings system) : EagerBackgroundService
{
    public const string EntityId = BuiltInEntityIds.SessionLocked;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await registry.RegisterAsync(
            new EntityDescriptor
            {
                Id = EntityId,
                Name = "Session locked",
                Kind = EntityKind.BinarySensor,
                Icon = "mdi:account-lock",
            },
            stoppingToken);

        var publisher = new ChangeOnlyPublisher(bus);
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            if (system.ReadConsoleLocked() is { } locked)
            {
                await publisher.PublishAsync(EntityId, BinaryState.From(locked), cancellationToken: stoppingToken);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>
/// Publishes whether the system wants a restart, for updates that are installed but not finished. Runs in the
/// service, so it is reported with nobody signed in. Left out where the system cannot tell.
/// </summary>
public sealed class RestartRequiredSensor(IEventBus bus, IEntityRegistry registry, ISystemReadings system) : EagerBackgroundService
{
    public const string EntityId = BuiltInEntityIds.RestartRequired;

    // Updates finish installing at their own pace; nobody waits on this by the second.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (system.ReadPendingRestart() is null)
        {
            return;
        }

        await registry.RegisterAsync(
            new EntityDescriptor { Id = EntityId, Name = "Restart required", Kind = EntityKind.BinarySensor, Icon = "mdi:restart-alert" },
            stoppingToken);

        var publisher = new ChangeOnlyPublisher(bus);
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            if (system.ReadPendingRestart() is { } reasons)
            {
                await publisher.PublishAsync(
                    EntityId,
                    BinaryState.From(reasons.Length > 0),
                    new Dictionary<string, object?> { ["reason"] = reasons.Length > 0 ? reasons : null },
                    stoppingToken);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>
/// Publishes how full each of the computer's own disks is, as <c>disk_c_usage</c>, <c>disk_root_usage</c> and so
/// on, with the free and total space as attributes. Runs in the service.
/// </summary>
public sealed class DiskUsageSensor(IEventBus bus, IEntityRegistry registry, ISystemReadings system) : EagerBackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    public static string EntityIdFor(string key) => BuiltInEntityIds.DiskUsage(key);

    /// <summary>Whether an id is one this sensor may register, so a custom sensor cannot take it.</summary>
    public static bool IsEntityId(string id) => BuiltInEntityIds.IsDiskUsage(id);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var registered = new HashSet<string>(StringComparer.Ordinal);
        var publisher = new ChangeOnlyPublisher(bus);
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            foreach (var disk in system.ReadDisks())
            {
                var id = EntityIdFor(disk.Key);

                // Registered when first seen, so a disk added later, e.g. a BitLocker drive unlocked after sign-in, appears too.
                if (registered.Add(disk.Key))
                {
                    await registry.RegisterAsync(
                        new EntityDescriptor
                        {
                            Id = id,
                            Name = $"Disk {disk.Label} usage",
                            Kind = EntityKind.Sensor,
                            Icon = "mdi:harddisk",
                            UnitOfMeasurement = "%",
                            StateClass = "measurement",
                        },
                        stoppingToken);
                }

                await publisher.PublishAsync(
                    id,
                    disk.UsedPercent.ToString(CultureInfo.InvariantCulture),
                    new Dictionary<string, object?> { ["free_gb"] = disk.FreeGigabytes, ["total_gb"] = disk.TotalGigabytes },
                    stoppingToken);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

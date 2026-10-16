using System.Globalization;
using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Hosting;
using HADA.Core.Messaging;
using HADA.Core.Models;

namespace HADA.Core.Sensors;

/// <summary>Publishes whether the laptop's lid is open. Registers nothing where there is no lid.</summary>
public sealed class LidSensor(IEventBus bus, IEntityRegistry registry, IHardwareReadings hardware) : EagerBackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (hardware.ReadLidOpen() is null)
        {
            return;
        }

        await registry.RegisterAsync(BuiltInDescriptors.Lid, stoppingToken);
        var publisher = new ChangeOnlyPublisher(bus);
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            if (hardware.ReadLidOpen() is { } open)
            {
                await publisher.PublishAsync(BuiltInEntityIds.LidOpen, BinaryState.From(open), cancellationToken: stoppingToken);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>Publishes how busy the graphics processor is. Registered with the first reading; left out where the system cannot tell.</summary>
public sealed class GpuLoadSensor(IEventBus bus, IEntityRegistry registry, IHardwareReadings hardware) : EagerBackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!hardware.HasGpuLoad)
        {
            return;
        }

        var registered = false;
        var publisher = new ChangeOnlyPublisher(bus);
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            if (hardware.ReadGpuLoadPercent() is not { } load)
            {
                continue;
            }

            if (!registered)
            {
                registered = true;
                await registry.RegisterAsync(BuiltInDescriptors.GpuLoad, stoppingToken);
            }

            await publisher.PublishAsync(BuiltInEntityIds.GpuLoad, load.ToString(CultureInfo.InvariantCulture), cancellationToken: stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>Publishes whether a screen other than the laptop's own is connected, with how many there are.</summary>
public sealed class ExternalDisplaySensor(IEventBus bus, IEntityRegistry registry, IDisplayReadings displays) : EagerBackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (displays.ReadDisplays() is null)
        {
            return;
        }

        await registry.RegisterAsync(BuiltInDescriptors.ExternalDisplay, stoppingToken);
        var publisher = new ChangeOnlyPublisher(bus);
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            if (displays.ReadDisplays() is { } count)
            {
                await publisher.PublishAsync(
                    BuiltInEntityIds.ExternalDisplay,
                    BinaryState.From(count.External > 0),
                    new Dictionary<string, object?> { ["displays"] = count.Total, ["external_displays"] = count.External },
                    stoppingToken);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>
/// Publishes the window in front: its title, or the program's name where the system keeps titles to itself.
/// Registers nothing where the system does not tell, as Wayland does not.
/// </summary>
public sealed class ActiveWindowSensor(IEventBus bus, IEntityRegistry registry, IDisplayReadings displays) : EagerBackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!displays.HasActiveWindow)
        {
            return;
        }

        await registry.RegisterAsync(BuiltInDescriptors.ActiveWindow, stoppingToken);
        ActiveWindowInfo? last = null;
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            if (displays.ReadActiveWindow() is not { } window || window == last)
            {
                continue;
            }

            last = window;
            await bus.PublishAsync(
                new TelemetryEvent
                {
                    SensorId = BuiltInEntityIds.ActiveWindow,
                    State = window.Title.Length > 0 ? window.Title : window.ProcessName ?? "unknown",
                    Attributes = new Dictionary<string, object?> { ["process_name"] = window.ProcessName },
                },
                stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>Publishes whether the screens are on, on Linux and macOS. Left out where the system does not tell.</summary>
public sealed class DisplayPowerSensor(IEventBus bus, IEntityRegistry registry, IDisplayPower display) : EagerBackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (display.ReadDisplayOn() is null)
        {
            return;
        }

        await registry.RegisterAsync(BuiltInDescriptors.Display, stoppingToken);
        var publisher = new ChangeOnlyPublisher(bus);
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            if (display.ReadDisplayOn() is { } on)
            {
                await publisher.PublishAsync(BuiltInEntityIds.DisplayOn, BinaryState.From(on), cancellationToken: stoppingToken);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

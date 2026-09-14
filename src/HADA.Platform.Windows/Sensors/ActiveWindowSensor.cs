using HADA.Core.Abstractions;
using HADA.Core.Entities;
using HADA.Core.Models;
using Microsoft.Extensions.Hosting;

namespace HADA.Platform.Windows.Sensors;

/// <summary>
/// Publishes the title of the focused window. Must run in the user's session (the tray app), not the service.
/// </summary>
/// <remarks>
/// Polling rather than a foreground-change hook also catches title changes within the same window, such as browser tabs.
/// </remarks>
public sealed class ActiveWindowSensor(IEventBus bus, IEntityRegistry registry) : BackgroundService
{
    public const string EntityId = "active_window";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await registry.RegisterAsync(
            new EntityDescriptor
            {
                Id = EntityId,
                Name = "Active window",
                Kind = EntityKind.Sensor,
                Icon = "mdi:application-outline",
            },
            stoppingToken);

        ForegroundWindowInfo? last = null;
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            var current = ForegroundWindow.TryRead();
            if (current is not { } window || current == last)
            {
                continue;
            }

            last = current;
            await bus.PublishAsync(
                new TelemetryEvent
                {
                    SensorId = EntityId,
                    State = window.Title.Length > 0 ? window.Title : window.ProcessName ?? "unknown",
                    Attributes = new Dictionary<string, object?> { ["process_name"] = window.ProcessName },
                },
                stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
